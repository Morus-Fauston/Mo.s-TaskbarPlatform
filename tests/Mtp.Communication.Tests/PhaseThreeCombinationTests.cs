using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class PhaseThreeCombinationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task One_clean_host_combines_dynamic_collections_and_flyout_requests_with_fault_isolation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-phase-three-combination-" + Guid.NewGuid().ToString("N"));
        string dynamicDirectory = Path.Combine(directory, "dynamic");
        string flyoutDirectory = Path.Combine(directory, "flyout");
        Directory.CreateDirectory(dynamicDirectory);
        Directory.CreateDirectory(flyoutDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        HostBrokerSession? host = null;
        var processIds = new List<int>();
        var monitors = new List<Process>();
        try
        {
            string dynamicProbe = Path.Combine(AppContext.BaseDirectory, "DynamicContentProcessProbe/Mtp.DynamicContent.ProcessProbe.dll");
            string flyoutProbe = Path.Combine(AppContext.BaseDirectory, "FlyoutProcessProbe/Mtp.Flyout.ProcessProbe.dll");
            Assert.True(File.Exists(dynamicProbe));
            Assert.True(File.Exists(flyoutProbe));
            host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["dynamic", "flyout"], timeout.Token);
            processIds.Add(host.BrokerProcessId);
            int dynamicPid = await host.StartServiceAsync("dynamic", dynamicProbe, timeout.Token, arguments: ["combined", dynamicDirectory]);
            processIds.Add(dynamicPid);
            int flyoutPid = await host.StartServiceAsync("flyout", flyoutProbe, timeout.Token, arguments: ["raw", flyoutDirectory]);
            processIds.Add(flyoutPid);
            Assert.Equal(3, processIds.Distinct().Count());
            Assert.DoesNotContain(Environment.ProcessId, processIds);
            foreach (int pid in processIds)
            {
                var monitor = Process.GetProcessById(pid);
                monitors.Add(monitor);
                _ = monitor.Handle;
            }
            using var dynamicReady = await MarkerAsync(dynamicDirectory, "ready", timeout.Token);
            using var flyoutReady = await MarkerAsync(flyoutDirectory, "ready", timeout.Token);
            Assert.Equal(dynamicPid, dynamicReady.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(flyoutPid, flyoutReady.RootElement.GetProperty("pid").GetInt32());
            string dynamicSession = dynamicReady.RootElement.GetProperty("sessionId").GetString()!;
            string flyoutSession = flyoutReady.RootElement.GetProperty("sessionId").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(dynamicSession));
            Assert.False(string.IsNullOrWhiteSpace(flyoutSession));
            Assert.NotEqual(dynamicSession, flyoutSession);
            Assert.Equal(2, host.States.Snapshots.Count);
            Assert.All(host.States.Snapshots, snapshot => { Assert.True(snapshot.IsInteractive); Assert.NotNull(snapshot.Declaration); Assert.Equal(0, snapshot.State!.Revision); });
            output.WriteLine($"hostPid={Environment.ProcessId}; brokerPid={host.BrokerProcessId}; dynamicPid={dynamicPid}; flyoutPid={flyoutPid}; dynamicSession={dynamicSession}; flyoutSession={flyoutSession}");
            await ContinueAsync(dynamicDirectory, "ready", timeout.Token);

            using var addedMarker = await MarkerAsync(dynamicDirectory, "added", timeout.Token);
            var added = DynamicSnapshot(host, dynamicSession, 1, 2);
            Assert.Equal(2, Content(added, "island").Activities.Count);
            string addedJson = JsonSerializer.Serialize(added.State);
            await File.WriteAllTextAsync(Path.Combine(flyoutDirectory, "run"), "run", timeout.Token);
            using var flyoutComplete = await MarkerAsync(flyoutDirectory, "complete", timeout.Token);
            Assert.Equal(flyoutSession, flyoutComplete.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal(flyoutPid, flyoutComplete.RootElement.GetProperty("pid").GetInt32());
            var flyoutResults = flyoutComplete.RootElement.GetProperty("results");
            Assert.Equal(11, flyoutResults.EnumerateObject().Count());
            Assert.Equal("DuplicateRequest", flyoutResults.GetProperty("duplicate").GetString());
            Assert.Equal("SessionMismatch", flyoutResults.GetProperty("old-session").GetString());
            Assert.Equal("SessionMismatch", flyoutResults.GetProperty("wrong-application").GetString());
            Assert.Equal("InvalidEnvelope", flyoutResults.GetProperty("missing-payload").GetString());
            Assert.Equal("InvalidEnvelope", flyoutResults.GetProperty("state-with-flyout").GetString());
            Assert.Equal("InvalidEnvelope", flyoutResults.GetProperty("declare-with-flyout").GetString());
            Assert.Equal("Received", flyoutResults.GetProperty("current-after-mixed").GetString());
            Assert.Equal("UnknownEntry", flyoutResults.GetProperty("unknown-channel").GetString());
            var flyout = host.States.GetSnapshot("flyout")!;
            Assert.Equal(flyoutSession, flyout.SessionId);
            Assert.Equal(123, flyout.State!.Revision);
            Assert.Equal("flyout-probe:123", Assert.Single(flyout.State.Components).Text);
            var unaffected = DynamicSnapshot(host, dynamicSession, 1, 2);
            Assert.Equal(addedJson, JsonSerializer.Serialize(unaffected.State));
            Assert.Null(host.States.FlyoutRequests.GetLastResult("dynamic"));
            Assert.Equal("UnknownEntry", host.States.FlyoutRequests.GetLastResult("flyout")!.Result.Code);
            output.WriteLine("foreignApplicationFaults=" + flyoutComplete.RootElement.GetRawText());
            output.WriteLine("faultIsolationCheckpoint: dynamicRevision=1; ordinaryItems=2; islandItems=2; activities=2; snapshotUnchanged=true");
            await ContinueAsync(dynamicDirectory, "added", timeout.Token);

            using var updatedMarker = await MarkerAsync(dynamicDirectory, "updated", timeout.Token);
            var updated = DynamicSnapshot(host, dynamicSession, 2, 3);
            var paused = Content(updated, "island").Items.Single(item => item.ItemId == "visible-a");
            Assert.True(paused.Fields.Timer!.IsPaused);
            Assert.Equal(45_000, paused.Fields.Timer.ValueMillisecondsAtReference);
            Assert.Equal(7, paused.Fields.Counter!.Value);
            Assert.Equal(45, paused.Fields.Progress!.Value);
            string updatedJson = JsonSerializer.Serialize(updated.State);
            await ContinueAsync(dynamicDirectory, "updated", timeout.Token);

            using var rejectedMarker = await MarkerAsync(dynamicDirectory, "rejected", timeout.Token);
            Assert.Equal(10, rejectedMarker.RootElement.GetProperty("rejections").EnumerateObject().Count());
            Assert.Equal(updatedJson, JsonSerializer.Serialize(DynamicSnapshot(host, dynamicSession, 2, 3).State));
            Assert.Equal(123, host.States.GetSnapshot("flyout")!.State!.Revision);
            await ContinueAsync(dynamicDirectory, "rejected", timeout.Token);

            using var removedMarker = await MarkerAsync(dynamicDirectory, "removed", timeout.Token);
            var removed = DynamicSnapshot(host, dynamicSession, 3, 1);
            Assert.Equal("business-a", Assert.Single(Content(removed, "island").Activities).ActivityId);
            await ContinueAsync(dynamicDirectory, "removed", timeout.Token);

            using var complete = await MarkerAsync(dynamicDirectory, "complete", timeout.Token);
            Assert.Equal(dynamicPid, complete.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(dynamicSession, complete.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal(4, complete.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal("Received", complete.RootElement.GetProperty("flyoutResult").GetString());
            var final = DynamicSnapshot(host, dynamicSession, 4, 0);
            Assert.Empty(Content(final, "island").Activities);
            Assert.Equal(2, final.Declaration!.DynamicContents.Count);
            var receipt = host.States.FlyoutRequests.GetLastResult("dynamic");
            Assert.NotNull(receipt);
            Assert.True(receipt.Result.Accepted);
            Assert.Equal("Received", receipt.Result.Code);
            Assert.Equal(1, receipt.RequestSequence);
            Assert.False(string.IsNullOrWhiteSpace(receipt.RequestId));
            Assert.Equal(addedJson, JsonSerializer.Serialize(added.State));
            Assert.Equal(updatedJson, JsonSerializer.Serialize(updated.State));
            Assert.Equal(2, host.States.Snapshots.Count);
            Assert.Null(host.LastError);
            Assert.InRange(host.PeakPendingRequests, 1, ProtocolLimits.MaximumPendingRequests);
            output.WriteLine("dynamicCombinationComplete=" + complete.RootElement.GetRawText());
            output.WriteLine($"combinationComplete=true; dynamicRevision=4; flyoutRevision=123; dynamicReceipt={receipt.Result.Code}; requestSequence={receipt.RequestSequence}; queuePeak={host.PeakPendingRequests}; dynamicSessionUnchanged=true; windowsNotCreated=true");
        }
        finally
        {
            try
            {
                if (host is not null) { await host.DisposeAsync(); await host.DisposeAsync(); Assert.Empty(host.ServiceProcessIds); }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var monitor in monitors)
                {
                    await monitor.WaitForExitAsync(cleanup.Token);
                    Assert.True(monitor.HasExited);
                    output.WriteLine($"pid={monitor.Id}; processExited=true; exitCode={monitor.ExitCode}");
                }
            }
            finally
            {
                foreach (var monitor in monitors) monitor.Dispose();
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static BrokerApplicationSnapshot DynamicSnapshot(HostBrokerSession host, string session, long revision, int itemCount)
    {
        var snapshot = host.States.GetSnapshot("dynamic")!;
        Assert.Equal(session, snapshot.SessionId);
        Assert.True(snapshot.IsInteractive);
        Assert.Equal(revision, snapshot.State!.Revision);
        Assert.Equal(itemCount, Content(snapshot, "ordinary").Items.Count);
        Assert.Equal(itemCount, Content(snapshot, "island").Items.Count);
        return snapshot;
    }

    private static DynamicContentState Content(BrokerApplicationSnapshot snapshot, string component) =>
        snapshot.State!.DynamicEntries!.Single(entry => entry.FeatureGroupId == "main" && entry.ComponentId == component).Content;

    private static Task ContinueAsync(string directory, string phase, CancellationToken token) =>
        File.WriteAllTextAsync(Path.Combine(directory, "after-" + phase), "continue", token);

    private static async Task<JsonDocument> MarkerAsync(string directory, string marker, CancellationToken token)
    {
        string path = Path.Combine(directory, marker + ".json");
        while (!File.Exists(path))
        {
            string failed = Path.Combine(directory, "failed.json");
            if (File.Exists(failed)) Assert.Fail("Combination probe failed: " + await File.ReadAllTextAsync(failed, token));
            await Task.Delay(10, token);
        }
        return JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
    }
}

using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class FlyoutProcessTests(ITestOutputHelper output)
{
    [Fact]
    public Task Four_sdk_flyout_kinds_receive_only_receipts_and_policy_rejections_preserve_healthy_state_flow() => RunAsync("sdk");

    [Fact]
    public Task Replayed_and_foreign_session_requests_are_rejected_and_disconnection_is_isolated() => RunAsync("raw");

    private async Task RunAsync(string mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-flyout-process-" + Guid.NewGuid().ToString("N"));
        string flyoutDirectory = Path.Combine(directory, "flyout");
        string healthyDirectory = Path.Combine(directory, "healthy");
        Directory.CreateDirectory(flyoutDirectory);
        Directory.CreateDirectory(healthyDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        HostBrokerSession? host = null;
        var processIds = new List<int>();
        try
        {
            string flyoutPath = Path.Combine(AppContext.BaseDirectory, "FlyoutProcessProbe/Mtp.Flyout.ProcessProbe.dll");
            string healthyPath = Path.Combine(AppContext.BaseDirectory, "CommunicationProcessProbe/Mtp.Communication.ProcessProbe.dll");
            Assert.True(File.Exists(flyoutPath));
            Assert.True(File.Exists(healthyPath));
            host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["flyout", "healthy"], timeout.Token);
            processIds.Add(host.BrokerProcessId);
            int flyoutPid = await host.StartServiceAsync("flyout", flyoutPath, timeout.Token, arguments: [mode, flyoutDirectory]);
            processIds.Add(flyoutPid);
            int healthyPid = await host.StartServiceAsync("healthy", healthyPath, timeout.Token, arguments: ["normal", healthyDirectory]);
            processIds.Add(healthyPid);
            Assert.Equal(3, processIds.Distinct().Count());
            Assert.DoesNotContain(Environment.ProcessId, processIds);
            output.WriteLine($"mode={mode}; hostPid={Environment.ProcessId}; brokerPid={host.BrokerProcessId}; flyoutPid={flyoutPid}; healthyPid={healthyPid}");
            using var ready = await MarkerAsync(flyoutDirectory, "ready", timeout.Token);
            using var healthyReady = await MarkerAsync(healthyDirectory, "ready", timeout.Token);
            Assert.Equal(flyoutPid, ready.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(healthyPid, healthyReady.RootElement.GetProperty("pid").GetInt32());
            var initial = host.States.GetSnapshot("flyout")!;
            Assert.True(initial.IsInteractive);
            Assert.Equal(0, initial.State!.Revision);
            Assert.Null(host.States.FlyoutRequests.GetLastResult("flyout"));

            if (mode == "sdk")
            {
                var blocked = initial.Declaration!.FlyoutEntries.Single(x => x.Identity.LocalId.Value == "blocked");
                Assert.True(host.States.FlyoutRequests.SetEntryEnabled(blocked, false).Accepted);
            }
            await File.WriteAllTextAsync(Path.Combine(healthyDirectory, "run"), "run", timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(flyoutDirectory, "run"), "run", timeout.Token);

            if (mode == "sdk")
            {
                (string Name, string Code)[] checkpoints =
                [ ("disabled", "EntryDisabled"), ("reenabled", "Received"), ("taskbar", "Received"),
                  ("short", "Received"), ("interactive", "Received"), ("event", "Received"),
                  ("unknown", "UnknownEntry"), ("kind", "KindMismatch"), ("revision", "StaleState"), ("budget", "InvalidRequest") ];
                for (int index = 0; index < checkpoints.Length; index++)
                {
                    var expected = checkpoints[index];
                    using var marker = await MarkerAsync(flyoutDirectory, expected.Name, timeout.Token);
                    Assert.Equal(flyoutPid, marker.RootElement.GetProperty("pid").GetInt32());
                    Assert.Equal(expected.Code, marker.RootElement.GetProperty("code").GetString());
                    var receipt = host.States.FlyoutRequests.GetLastResult("flyout");
                    Assert.NotNull(receipt);
                    Assert.Equal(expected.Code, receipt.Result.Code);
                    Assert.Equal(expected.Code == "Received", receipt.Result.Accepted);
                    Assert.Equal(index + 1, receipt.RequestSequence);
                    Assert.False(string.IsNullOrWhiteSpace(receipt.RequestId));
                    Assert.NotEqual("caller-ignored", receipt.RequestId);
                    Assert.NotEqual("Displayed", receipt.Result.Code);
                    Assert.Equal(0, host.States.GetSnapshot("flyout")!.State!.Revision);
                    output.WriteLine($"scenario={expected.Name}; requestSequence={receipt.RequestSequence}; result={receipt.Result.Code}; accepted={receipt.Result.Accepted}; windowDisplayClaimed=false");
                    if (expected.Name == "disabled")
                    {
                        var blocked = initial.Declaration!.FlyoutEntries.Single(x => x.Identity.LocalId.Value == "blocked");
                        Assert.True(host.States.FlyoutRequests.SetEntryEnabled(blocked, true).Accepted);
                    }
                    await File.WriteAllTextAsync(Path.Combine(flyoutDirectory, "after-" + expected.Name), "continue", timeout.Token);
                }
            }

            using var complete = await MarkerAsync(flyoutDirectory, "complete", timeout.Token);
            Assert.Equal(flyoutPid, complete.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(123, complete.RootElement.GetProperty("revision").GetInt32());
            if (mode == "raw")
            {
                var results = complete.RootElement.GetProperty("results");
                Assert.Equal(11, results.EnumerateObject().Count());
                Assert.Equal("Received", results.GetProperty("first").GetString());
                Assert.Equal("DuplicateRequest", results.GetProperty("duplicate").GetString());
                Assert.Equal("DuplicateRequest", results.GetProperty("older").GetString());
                Assert.Equal("SessionMismatch", results.GetProperty("old-session").GetString());
                Assert.Equal("SessionMismatch", results.GetProperty("wrong-application").GetString());
                Assert.Equal("Received", results.GetProperty("current-after-old").GetString());
                Assert.Equal("InvalidEnvelope", results.GetProperty("missing-payload").GetString());
                Assert.Equal("InvalidEnvelope", results.GetProperty("state-with-flyout").GetString());
                Assert.Equal("InvalidEnvelope", results.GetProperty("declare-with-flyout").GetString());
                Assert.Equal("Received", results.GetProperty("current-after-mixed").GetString());
                Assert.Equal("UnknownEntry", results.GetProperty("unknown-channel").GetString());
                Assert.True(complete.RootElement.GetProperty("connectionClosed").GetBoolean());
                while (host.States.GetSnapshot("flyout")!.IsConnected) await Task.Delay(10, timeout.Token);
                Assert.False(host.States.GetSnapshot("flyout")!.IsInteractive);
            }
            using var healthyComplete = await MarkerAsync(healthyDirectory, "complete", timeout.Token);
            Assert.Equal(healthyPid, healthyComplete.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(500, healthyComplete.RootElement.GetProperty("accepted").GetInt32());
            var healthy = host.States.GetSnapshot("healthy")!;
            Assert.True(healthy.IsInteractive);
            Assert.Equal(500, healthy.State!.Revision);
            Assert.Equal("complete:normal", Assert.Single(healthy.State.Components).Text);
            var flyout = host.States.GetSnapshot("flyout")!;
            Assert.Equal(123, flyout.State!.Revision);
            Assert.Equal("flyout-probe:123", Assert.Single(flyout.State.Components).Text);
            Assert.Equal(0, initial.State.Revision);
            Assert.Equal(2, host.States.Snapshots.Count);
            Assert.Null(host.LastError);
            Assert.InRange(host.PeakPendingRequests, 1, ProtocolLimits.MaximumPendingRequests);
            output.WriteLine("flyoutComplete=" + complete.RootElement.GetRawText());
            output.WriteLine("healthyComplete=" + healthyComplete.RootElement.GetRawText());
            output.WriteLine($"mode={mode}; finalFlyoutRevision=123; finalHealthyRevision=500; queuePeak={host.PeakPendingRequests}; processProtocolOnly=true");
        }
        finally
        {
            if (host is not null) { await host.DisposeAsync(); await host.DisposeAsync(); Assert.Empty(host.ServiceProcessIds); }
            foreach (int pid in processIds)
            {
                Assert.False(IsRunning(pid), $"Owned process {pid} must exit.");
                output.WriteLine($"mode={mode}; pid={pid}; processExited=true");
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<JsonDocument> MarkerAsync(string directory, string marker, CancellationToken token)
    {
        string path = Path.Combine(directory, marker + ".json");
        while (!File.Exists(path))
        {
            string failed = Path.Combine(directory, "failed.json");
            if (File.Exists(failed)) Assert.Fail("Flyout process probe failed: " + await File.ReadAllTextAsync(failed, token));
            await Task.Delay(10, token);
        }
        return JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}

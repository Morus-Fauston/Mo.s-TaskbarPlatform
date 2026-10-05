using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Mtp.Transport;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class PhaseFourCombinationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Two_bounded_rounds_combine_expansion_timeout_recovery_permissions_and_expiry()
    {
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var elapsed = Stopwatch.StartNew();
        output.WriteLine("budget: rounds=2; businessSecondsPerRound=20; cleanupSecondsPerRound=5; totalSeconds=50; brokerRecoverySeconds=8; independentFailureCleanupSeconds=5");
        for (int round = 1; round <= 2; round++) await RunRoundAsync(round, total.Token);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(50));
        output.WriteLine($"combinationComplete=true; roundsCompleted=2; totalElapsedMs={elapsed.ElapsedMilliseconds}; allOwnedProcessesExited=true");
    }

    private async Task RunRoundAsync(int round, CancellationToken totalToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-phase-four-" + round + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var business = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
        business.CancelAfter(TimeSpan.FromSeconds(20));
        var elapsed = Stopwatch.StartNew();
        var owned = new Dictionary<int, Process>();
        var servicePids = new Dictionary<string, int>(StringComparer.Ordinal);
        HostBrokerSession? host = null;
        ItemPresentationController? items = null;
        string phase = "startup";
        try
        {
            host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["activities", "slow", "healthy"], business.Token);
            Track(host.BrokerProcessId);
            foreach (string application in new[] { "activities", "slow", "healthy" })
            {
                string coordination = DirectoryFor(application);
                Directory.CreateDirectory(coordination);
                bool activity = application == "activities";
                string probe = activity ? "ActivityProcessProbe/Mtp.Activity.ProcessProbe.dll" : "ActionProcessProbe/Mtp.Action.ProcessProbe.dll";
                string[] arguments = activity ? [coordination] : [application == "slow" ? "slow" : "normal", coordination];
                int pid = await host.StartServiceAsync(application, Path.Combine(AppContext.BaseDirectory, probe), business.Token, arguments);
                Track(pid);
                servicePids.Add(application, pid);
                using var ready = await MarkerAsync(application, "ready");
                Assert.Equal(pid, ready.RootElement.GetProperty("pid").GetInt32());
                Assert.True(host.States.GetSnapshot(application)!.IsInteractive);
                output.WriteLine($"round={round}; application={application}; servicePid={pid}; initialSession={host.States.GetSnapshot(application)!.SessionId}");
            }
            Assert.Equal(4, owned.Count);
            Assert.DoesNotContain(Environment.ProcessId, owned.Keys);
            items = new ItemPresentationController(host.States);
            Assert.True(items.UpdateScreens(["screen-a", "screen-b"]).Accepted);
            Assert.True(host.States.SetEntryDisplayAllowed("activities", "main", "island", true).Accepted);
            using var firstPublication = await MarkerAsync("activities", "publication-1");
            var initialItem = Item("screen-a", "item-a");
            var expansion = items.Toggle(initialItem.Handle);
            Assert.True(expansion.Result.Accepted, expansion.Result.Code);
            Assert.True(Item("screen-a", "item-a").Expanded);
            Assert.False(Item("screen-b", "item-a").Expanded);

            phase = "timeout-and-late-result";
            var actionElapsed = Stopwatch.StartNew();
            var slow = host.SendActionAsync(Slot("slow"), new(), business.Token);
            using var started = await MarkerAsync("slow", "started-1");
            await HealthyActionAsync(10);
            Assert.False(slow.IsCompleted);
            var timeout = await slow;
            Assert.Equal("ActionTimedOut", timeout.Code);
            Assert.InRange(actionElapsed.Elapsed.TotalSeconds, 4, 9);
            Assert.Equal(0, host.Actions.BusyCount);
            Assert.False(Assert.Single(host.Actions.GetPending("slow")).IsBusy);
            Assert.Equal(0, host.States.GetSnapshot("slow")!.State!.Revision);
            Assert.True(host.States.GetSnapshot("slow")!.IsInteractive);
            var hint = host.Actions.GetLastErrorHint("slow");
            Assert.NotNull(hint);
            Assert.Equal("ActionTimedOut", hint.Result.Code);
            await File.WriteAllTextAsync(Path.Combine(DirectoryFor("slow"), "release-1"), "continue", business.Token);
            await UntilAsync(() => host.States.GetSnapshot("slow")!.State!.Revision == 1 && host.Actions.OutstandingCount == 0, business.Token);
            Assert.Equal(10, Assert.Single(host.States.GetSnapshot("slow")!.State!.Components).Number);
            Assert.Same(hint, host.Actions.GetLastErrorHint("slow"));
            using var finished = await MarkerAsync("slow", "finished-1");
            Assert.Equal(1, finished.RootElement.GetProperty("call").GetInt64());
            Assert.True(Item("screen-a", "item-a").Expanded);
            output.WriteLine($"round={round}; timeout=ActionTimedOut; timeoutMs={actionElapsed.ElapsedMilliseconds}; lateValue=10; repeatedHint=false; healthyActionWhileSlowBlocked=true");

            phase = "closed-permission";
            Assert.True(host.States.SetEntryDisplayAllowed("activities", "main", "island", false).Accepted);
            using var partial = await CommandAsync(1, "update-add");
            var result = partial.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("accepted").GetBoolean());
            Assert.Equal("AcceptedWithActivityRejections", result.GetProperty("code").GetString());
            Assert.Equal("running-b", Assert.Single(result.GetProperty("activityRejections").EnumerateArray()).GetProperty("activityId").GetString());
            Assert.Equal("running-a", Assert.Single(Content().Activities).ActivityId);
            Assert.Equal("running-a", Assert.Single(Assert.Single(Content().Items).ActivityIds));
            Assert.False(items.Toggle(initialItem.Handle).Result.Accepted);
            var beforeRecovery = Item("screen-a", "shared-item");
            Assert.True(items.Toggle(beforeRecovery.Handle).Result.Accepted);
            Assert.True(Item("screen-a", "shared-item").Expanded);
            Assert.False(Item("screen-b", "shared-item").Expanded);

            phase = "broker-recovery-while-expanded";
            var sessions = servicePids.Keys.ToDictionary(id => id, id => host.States.GetSnapshot(id)!.SessionId);
            int oldBroker = host.BrokerProcessId;
            owned[oldBroker].Kill(entireProcessTree: true);
            await owned[oldBroker].WaitForExitAsync(business.Token);
            using var recovering = CancellationTokenSource.CreateLinkedTokenSource(business.Token);
            recovering.CancelAfter(TimeSpan.FromSeconds(8));
            await UntilAsync(() => host.BrokerRecovery.State == RecoveryState.Available && sessions.All(pair =>
                host.States.GetSnapshot(pair.Key) is { IsInteractive: true } snapshot && snapshot.SessionId != pair.Value &&
                host.GetRecovery(pair.Key)?.State == RecoveryState.Available), recovering.Token);
            Track(host.BrokerProcessId);
            Assert.NotEqual(oldBroker, host.BrokerProcessId);
            Assert.Equal(servicePids.Values.Order().ToArray(), host.ServiceProcessIds.Order().ToArray());
            Assert.All(servicePids.Values, pid => Assert.False(owned[pid].HasExited));
            var recoveredItem = Item("screen-a", "shared-item");
            Assert.Equal(beforeRecovery.Handle.Item, recoveredItem.Handle.Item);
            Assert.NotEqual(beforeRecovery.Handle.Generation, recoveredItem.Handle.Generation);
            Assert.True(recoveredItem.Expanded);
            Assert.False(Item("screen-b", "shared-item").Expanded);
            Assert.False(items.Toggle(beforeRecovery.Handle).Result.Accepted);
            Assert.Equal("running-a", Assert.Single(Content().Activities).ActivityId);
            using var report = await CommandAsync(2, "report");
            var redeclared = report.RootElement.GetProperty("initialResult");
            Assert.Equal("AcceptedWithActivityRejections", redeclared.GetProperty("code").GetString());
            Assert.Equal("running-b", Assert.Single(redeclared.GetProperty("activityRejections").EnumerateArray()).GetProperty("activityId").GetString());
            var liveSdk = report.RootElement.GetProperty("lifecycle");
            Assert.False(liveSdk.GetProperty("credentialReaderCompleted").GetBoolean());
            Assert.False(liveSdk.GetProperty("recoveryWorkerCompleted").GetBoolean());
            Assert.Equal(0, liveSdk.GetProperty("credentialQueueCount").GetInt32());
            var liveConnection = liveSdk.GetProperty("currentConnection");
            Assert.Equal(host.States.GetSnapshot("activities")!.SessionId, liveConnection.GetProperty("sessionId").GetString());
            Assert.False(liveConnection.GetProperty("heartbeatWorkerCompleted").GetBoolean());
            Assert.Equal(1, liveConnection.GetProperty("activePermissionSubscriptions").GetInt32());
            AssertRetiredConnection(liveSdk.GetProperty("lastRetiredConnection"));
            Assert.Equal(sessions["activities"], liveSdk.GetProperty("lastRetiredConnection").GetProperty("sessionId").GetString());
            var liveHost = host.GetLifecycleSnapshot();
            Assert.Equal(3, liveHost.OwnedServices);
            Assert.Equal(1, liveHost.OwnedBrokers);
            Assert.Equal(1, liveHost.ActiveClockTasks);
            AssertRetiredBroker(liveHost.LastRetiredBroker);
            output.WriteLine($"round={round}; liveHostLifecycle={JsonSerializer.Serialize(liveHost, LengthPrefixedJson.Options)}");
            await HealthyActionAsync(20);
            Assert.Equal(1, host.BrokerRecovery.Attempts);
            foreach (string application in servicePids.Keys)
            {
                Assert.Equal(1, host.GetRecovery(application)!.Attempts);
                Assert.Equal(0, host.GetRecovery(application)!.Restarts);
                output.WriteLine($"round={round}; recoveredApplication={application}; pid={servicePids[application]}; oldSession={sessions[application]}; newSession={host.States.GetSnapshot(application)!.SessionId}");
            }
            Assert.Equal(3, host.States.Snapshots.Count);
            Assert.Empty(host.ServiceProcessExits);
            Assert.Equal(0, host.Actions.OutstandingCount);
            Assert.Equal(0, host.Actions.BusyCount);
            output.WriteLine($"round={round}; oldBrokerPid={oldBroker}; newBrokerPid={host.BrokerProcessId}; expansionPreserved=true; oldItemHandleRejected=true; servicePidsPreserved=true; healthyActionAfterRecovery=true");

            phase = "permission-restoration-and-expiry";
            Assert.True(host.States.SetEntryDisplayAllowed("activities", "main", "island", true).Accepted);
            using var restored = await MarkerAsync("activities", "publication-2");
            Assert.True(restored.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
            Assert.False(Item("screen-a", "item-a").Expanded);
            Assert.False(items.Toggle(recoveredItem.Handle).Result.Accepted);
            using var expiring = await CommandAsync(3, "expires-short");
            Assert.True(expiring.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
            long revision = host.States.GetSnapshot("activities")!.State!.Revision;
            var expiryHandle = Item("screen-a", "item-a").Handle;
            Assert.True(host.States.SetEntryDisplayAllowed("activities", "main", "island", false).Accepted);
            await UntilAsync(() => items.GetPresentation("screen-a").Count == 0, business.Token);
            Assert.Empty(items.GetPresentation("screen-b"));
            Assert.False(items.Toggle(expiryHandle).Result.Accepted);
            Assert.Empty(Content().Activities);
            Assert.Empty(Content().Items);
            Assert.Equal(revision, host.States.GetSnapshot("activities")!.State!.Revision);
            Assert.Equal(7, Assert.Single(host.States.GetSnapshot("activities")!.State!.Components, value => value.ComponentId == "ordinary").Number);
            await HealthyActionAsync(30);
            Assert.Equal(10, Assert.Single(host.States.GetSnapshot("slow")!.State!.Components).Number);
            Assert.Equal(0, host.Actions.OutstandingCount);
            Assert.Equal(0, host.Actions.BusyCount);
            Assert.Null(host.LastError);
            phase = "explicit-sdk-stop-with-host-input-open";
            using var stopped = await CommandAsync(4, "stop");
            var stoppedSdk = stopped.RootElement.GetProperty("lifecycle");
            Assert.True(stoppedSdk.GetProperty("credentialReaderCompleted").GetBoolean());
            Assert.True(stoppedSdk.GetProperty("recoveryWorkerCompleted").GetBoolean());
            Assert.Equal(0, stoppedSdk.GetProperty("credentialQueueCount").GetInt32());
            Assert.True(!stoppedSdk.TryGetProperty("currentConnection", out var stoppedConnection) || stoppedConnection.ValueKind == JsonValueKind.Null);
            AssertRetiredConnection(stoppedSdk.GetProperty("lastRetiredConnection"));
            Assert.Equal(host.States.GetSnapshot("activities")!.SessionId, stoppedSdk.GetProperty("lastRetiredConnection").GetProperty("sessionId").GetString());
            Assert.False(owned[servicePids["activities"]].HasExited);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20));
            output.WriteLine($"round={round}; businessComplete=true; elapsedMs={elapsed.ElapsedMilliseconds}; permissionRestoredByProvider=true; expiryRemovedBothScreens=true; expiryRevision={revision}; retainedStateSnapshots={host.States.Snapshots.Count}; sdkExplicitlyStopped=true; pendingActions=0; busyActions=0");
        }
        catch (Exception error)
        {
            output.WriteLine($"round={round}; failedPhase={phase}; elapsedMs={elapsed.ElapsedMilliseconds}; exception={error.GetType().Name}; totalCancelled={totalToken.IsCancellationRequested}; businessCancelled={business.IsCancellationRequested}");
            throw;
        }
        finally
        {
            var cleanupElapsed = Stopwatch.StartNew();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                items?.Close();
                if (host is not null)
                {
                    if (!owned.ContainsKey(host.BrokerProcessId)) Track(host.BrokerProcessId);
                    await host.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
                    await host.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
                    Assert.Empty(host.ServiceProcessIds);
                    Assert.Equal(0, host.Actions.OutstandingCount);
                    Assert.Equal(0, host.Actions.BusyCount);
                    var stoppedHost = host.GetLifecycleSnapshot();
                    AssertStoppedHost(stoppedHost);
                    output.WriteLine($"round={round}; stoppedHostLifecycle={JsonSerializer.Serialize(stoppedHost, LengthPrefixedJson.Options)}");
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => host.SendActionAsync(Slot("healthy"), new(), cleanup.Token));
                    AssertStoppedHost(host.GetLifecycleSnapshot());
                }
                foreach (var process in owned.Values)
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    Assert.True(process.HasExited);
                    output.WriteLine($"round={round}; ownedPid={process.Id}; processExited=true; exitCode={process.ExitCode}");
                }
                Assert.True(cleanupElapsed.Elapsed < TimeSpan.FromSeconds(5));
                output.WriteLine($"round={round}; cleanupComplete=true; cleanupMs={cleanupElapsed.ElapsedMilliseconds}; ownedProcesses={owned.Count}; pendingActions=0; busyActions=0");
            }
            finally
            {
                foreach (var process in owned.Values) process.Dispose();
                Directory.Delete(directory, recursive: true);
            }
        }

        string DirectoryFor(string application) => Path.Combine(directory, application);
        void Track(int pid)
        {
            if (owned.ContainsKey(pid)) return;
            var process = Process.GetProcessById(pid);
            owned.Add(pid, process);
            _ = process.Handle;
        }
        HostItemPresentation Item(string screen, string id) => Assert.Single(items!.GetPresentation(screen), value =>
            value.Handle.Item.ApplicationId == "activities" && value.Handle.Item.ComponentId == "island" && value.Handle.Item.ItemId == id);
        DynamicContentState Content() => Assert.Single(host!.States.GetSnapshot("activities")!.State!.DynamicEntries!).Content;
        async Task HealthyActionAsync(int expected)
        {
            var result = await host!.SendActionAsync(Slot("healthy"), new(), business.Token);
            Assert.True(result.Accepted, result.Code);
            Assert.Equal(expected, Assert.Single(host.States.GetSnapshot("healthy")!.State!.Components).Number);
        }
        async Task<JsonDocument> CommandAsync(int number, string scenario)
        {
            string path = Path.Combine(DirectoryFor("activities"), "command-" + number);
            await File.WriteAllTextAsync(path + ".tmp", scenario, business.Token);
            File.Move(path + ".tmp", path);
            return await MarkerAsync("activities", "command-" + number + "-result");
        }
        async Task<JsonDocument> MarkerAsync(string application, string name)
        {
            string path = Path.Combine(DirectoryFor(application), name + ".json");
            while (!File.Exists(path))
            {
                string failed = Path.Combine(DirectoryFor(application), "failed.json");
                if (File.Exists(failed)) Assert.Fail("Combination probe failed: " + await File.ReadAllTextAsync(failed, business.Token));
                await Task.Delay(10, business.Token);
            }
            string json = await File.ReadAllTextAsync(path, business.Token);
            output.WriteLine($"round={round}; application={application}; marker={name}; payload={json}");
            return JsonDocument.Parse(json);
        }
    }

    private static ActionSlotReference Slot(string application) => new(application, "main", ActionEntryKind.Component, "counter", "activate");
    private static void AssertRetiredConnection(JsonElement snapshot)
    {
        Assert.Equal(JsonValueKind.Object, snapshot.ValueKind);
        Assert.True(snapshot.GetProperty("readerCompleted").GetBoolean());
        Assert.True(snapshot.GetProperty("actionWorkerCompleted").GetBoolean());
        Assert.True(snapshot.GetProperty("heartbeatWorkerCompleted").GetBoolean());
        Assert.True(snapshot.GetProperty("permissionWorkerCompleted").GetBoolean());
        Assert.False(snapshot.GetProperty("pendingRequest").GetBoolean());
        Assert.Equal(0, snapshot.GetProperty("actionQueueCount").GetInt32());
        Assert.Equal(0, snapshot.GetProperty("permissionQueueCount").GetInt32());
        Assert.Equal(0, snapshot.GetProperty("activePermissionSubscriptions").GetInt32());
    }
    private static void AssertRetiredBroker(BrokerGenerationLifecycleSnapshot? snapshot)
    {
        Assert.NotNull(snapshot);
        Assert.True(snapshot.ReceiverCompleted);
        Assert.True(snapshot.ActionDispatcherCompleted);
        Assert.True(snapshot.PermissionPublisherCompleted);
        Assert.Equal(0, snapshot.ActionQueueCount);
        Assert.Equal(0, snapshot.PermissionSnapshotCount);
        Assert.Equal(0, snapshot.PendingRegistrationCount);
    }
    private static void AssertStoppedHost(HostLifecycleSnapshot snapshot)
    {
        Assert.Equal(0, snapshot.OwnedServices);
        Assert.Equal(0, snapshot.OwnedBrokers);
        Assert.Equal(0, snapshot.ActiveClockTasks);
        Assert.Equal(0, snapshot.ActiveRecoveryTasks);
        Assert.Equal(0, snapshot.ActiveOutputDrains);
        Assert.Equal(0, snapshot.OutstandingActions);
        Assert.Equal(0, snapshot.BusyActions);
        Assert.Null(snapshot.CurrentBroker);
        AssertRetiredBroker(snapshot.LastRetiredBroker);
    }
    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(10, token);
    }
}

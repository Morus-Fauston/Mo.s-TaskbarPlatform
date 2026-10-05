using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class RecoveryProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Service_recovery_budget_survives_success_exhausts_once_and_manual_retry_restores_it()
    {
        await using var fixture = await RecoveryFixture.StartAsync(output, TimeSpan.FromSeconds(60), ("subject", "stubborn"), ("healthy", "sdk"));
        var host = fixture.Host;
        string initialSession = host.States.GetSnapshot("subject")!.SessionId;
        await fixture.KillBrokerAsync();
        await fixture.UntilAsync(() => host.States.GetSnapshot("subject") is { IsInteractive: true } subject && subject.SessionId != initialSession &&
            host.GetRecovery("subject")?.State == RecoveryState.Available && host.States.GetSnapshot("healthy")?.IsInteractive == true);
        fixture.TrackCurrentBroker();
        var successfulBudget = host.GetRecovery("subject")!;
        Assert.InRange(successfulBudget.Attempts, 1, RecoveryLimits.MaximumTicketAttempts);
        Assert.Equal(0, successfulBudget.Restarts);
        Assert.False(successfulBudget.CanRetry);
        string beforeFailureSession = host.States.GetSnapshot("subject")!.SessionId;
        int originalPid = fixture.Service("subject").Id;
        string coordination = fixture.DirectoryFor("subject");
        await File.WriteAllTextAsync(Path.Combine(coordination, "force-unresponsive"), "fail future starts", fixture.Token);
        var elapsed = Stopwatch.StartNew();
        await File.WriteAllTextAsync(Path.Combine(coordination, "dispose-sdk"), "stop connection", fixture.Token);
        using var disposed = await fixture.MarkerAsync("subject", "disposed-" + originalPid);
        long firstExitMs = -1;
        int largestAttempts = successfulBudget.Attempts;
        while (host.GetRecovery("subject")?.State != RecoveryState.Exhausted)
        {
            fixture.TrackCurrentServices();
            var current = host.GetRecovery("subject")!;
            Assert.InRange(current.Attempts, largestAttempts, RecoveryLimits.MaximumTicketAttempts);
            largestAttempts = current.Attempts;
            Assert.InRange(current.Restarts, 0, RecoveryLimits.MaximumServiceRestarts);
            if (fixture.Service("subject").HasExited && firstExitMs < 0)
            {
                firstExitMs = elapsed.ElapsedMilliseconds;
                Assert.True(firstExitMs >= 19500, $"Original service terminated before its 20-second recovery window: {firstExitMs}ms");
            }
            Assert.True(host.States.GetSnapshot("healthy")!.IsInteractive);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(36), "Automatic recovery did not reach its bounded exhausted state.");
            await Task.Delay(20, fixture.Token);
        }
        fixture.TrackCurrentServices();
        var exhausted = host.GetRecovery("subject")!;
        Assert.Equal(RecoveryLimits.MaximumTicketAttempts, exhausted.Attempts);
        Assert.Equal(1, exhausted.Restarts);
        Assert.True(exhausted.CanRetry);
        Assert.False(string.IsNullOrWhiteSpace(exhausted.Error));
        Assert.True(fixture.Service("subject").HasExited);
        if (firstExitMs < 0) firstExitMs = elapsed.ElapsedMilliseconds;
        Assert.True(firstExitMs >= 19500, $"Original service terminated before its 20-second recovery window: {firstExitMs}ms");
        Assert.Equal(2, Directory.GetFiles(coordination, "boot-*.json").Length);
        Assert.False(host.States.GetSnapshot("subject")!.IsInteractive);
        Assert.Equal(7, Assert.Single(host.States.GetSnapshot("subject")!.State!.Components).Number);
        Assert.True((await host.SendActionAsync(Slot("healthy"), new(), fixture.Token)).Accepted);
        Assert.Equal(exhausted, host.GetRecovery("subject"));
        output.WriteLine($"automaticWindowMs={elapsed.ElapsedMilliseconds}; originalExitMs={firstExitMs}; successfulAttemptsRetained={successfulBudget.Attempts}; finalAutomaticAttempts={exhausted.Attempts}; automaticRestarts=1; automaticState=Exhausted");

        File.Delete(Path.Combine(coordination, "force-unresponsive"));
        File.Delete(Path.Combine(coordination, "dispose-sdk"));
        await File.WriteAllTextAsync(Path.Combine(coordination, "current.json"), "{\"revision\":30,\"number\":91}", fixture.Token);
        var retry = host.RetryApplicationAsync("subject", fixture.Token);
        while (!retry.IsCompleted)
        {
            fixture.TrackCurrentServices();
            await Task.Delay(20, fixture.Token);
        }
        var result = await retry;
        Assert.True(result.Accepted, result.Code);
        fixture.TrackCurrentServices();
        var restored = host.GetRecovery("subject")!;
        Assert.Equal(RecoveryState.Available, restored.State);
        Assert.False(restored.CanRetry);
        Assert.InRange(restored.Attempts, 0, RecoveryLimits.MaximumTicketAttempts);
        Assert.InRange(restored.Restarts, 0, RecoveryLimits.MaximumServiceRestarts);
        Assert.True(host.States.GetSnapshot("subject")!.IsInteractive);
        Assert.NotEqual(beforeFailureSession, host.States.GetSnapshot("subject")!.SessionId);
        Assert.Equal(91, Assert.Single(host.States.GetSnapshot("subject")!.State!.Components).Number);
        Assert.True((await host.SendActionAsync(Slot("subject"), new(), fixture.Token)).Accepted);
        Assert.Equal(101, Assert.Single(host.States.GetSnapshot("subject")!.State!.Components).Number);
        output.WriteLine($"manualRetry={result.Code}; manualAttempts={restored.Attempts}; manualRestarts={restored.Restarts}; state=Available; totalRecoveryMs={elapsed.ElapsedMilliseconds}");
    }

    [Fact]
    public async Task Closing_Host_during_service_recovery_cancels_retries_and_reaps_owned_processes()
    {
        await using var fixture = await RecoveryFixture.StartAsync(output, TimeSpan.FromSeconds(12), ("subject", "sdk"));
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryFor("subject"), "dispose-sdk"), "dispose", fixture.Token);
        using var disposed = await fixture.MarkerAsync("subject", "disposed-" + fixture.Service("subject").Id);
        await fixture.UntilAsync(() => fixture.Host.GetRecovery("subject") is { State: RecoveryState.Recovering, Attempts: > 0 });
        var before = fixture.Host.GetRecovery("subject")!;
        fixture.TrackCurrentServices();
        var elapsed = Stopwatch.StartNew();
        await fixture.Host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), fixture.Token);
        await fixture.Host.DisposeAsync();
        Assert.Empty(fixture.Host.ServiceProcessIds);
        Assert.Equal(0, fixture.Host.Actions.OutstandingCount);
        Assert.True(fixture.Service("subject").HasExited);
        Assert.Single(Directory.GetFiles(fixture.DirectoryFor("subject"), "boot-*.json"));
        output.WriteLine($"closedDuring=Recovering; attemptsAtClose={before.Attempts}; disposalMs={elapsed.ElapsedMilliseconds}; restartsCreated=0; originalProcessExited=true");
    }

    [Fact]
    public async Task Broker_recovery_asks_providers_for_current_snapshots_and_preserves_both_service_processes()
    {
        await using var fixture = await RecoveryFixture.StartAsync(output, TimeSpan.FromSeconds(20), ("subject", "sdk"), ("healthy", "sdk"));
        var host = fixture.Host;
        var sessions = new[] { "subject", "healthy" }.ToDictionary(id => id, id => host.States.GetSnapshot(id)!.SessionId);
        var servicePids = host.ServiceProcessIds.Order().ToArray();
        int oldBroker = host.BrokerProcessId;
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryFor("subject"), "current.json"), "{\"revision\":20,\"number\":89}", fixture.Token);
        await fixture.KillBrokerAsync();
        await fixture.UntilAsync(() => sessions.All(pair => host.States.GetSnapshot(pair.Key) is { IsInteractive: true } current && current.SessionId != pair.Value));
        fixture.TrackCurrentBroker();
        Assert.NotEqual(oldBroker, host.BrokerProcessId);
        Assert.Equal(servicePids, host.ServiceProcessIds.Order().ToArray());
        Assert.False(fixture.Service("subject").HasExited);
        Assert.False(fixture.Service("healthy").HasExited);
        Assert.Equal(20, host.States.GetSnapshot("subject")!.State!.Revision);
        Assert.Equal(89, Assert.Single(host.States.GetSnapshot("subject")!.State!.Components).Number);
        Assert.Equal(7, Assert.Single(host.States.GetSnapshot("healthy")!.State!.Components).Number);
        using var snapshot = await fixture.MarkerAsync("subject", "snapshot-" + fixture.Service("subject").Id + "-2");
        Assert.Equal(89, snapshot.RootElement.GetProperty("number").GetInt64());
        foreach (string id in sessions.Keys)
        {
            var result = await host.SendActionAsync(Slot(id), new(), fixture.Token);
            Assert.True(result.Accepted, result.Code);
            output.WriteLine($"recoveredApplication={id}; oldSession={sessions[id]}; newSession={host.States.GetSnapshot(id)!.SessionId}; stablePid={fixture.Service(id).Id}");
        }
        Assert.Equal(99, Assert.Single(host.States.GetSnapshot("subject")!.State!.Components).Number);
        Assert.Equal(17, Assert.Single(host.States.GetSnapshot("healthy")!.State!.Components).Number);
        output.WriteLine($"oldBrokerPid={oldBroker}; newBrokerPid={host.BrokerProcessId}; freshProviderSnapshot=true; servicePidsPreserved=true");
    }

    [Fact]
    public async Task SDK_disposal_finishes_while_the_live_Host_keeps_credentials_input_open()
    {
        await using var fixture = await RecoveryFixture.StartAsync(output, TimeSpan.FromSeconds(12), ("subject", "sdk"));
        int pid = fixture.Service("subject").Id;
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryFor("subject"), "dispose-sdk"), "dispose", fixture.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var disposed = await fixture.MarkerAsync("subject", "disposed-" + pid, deadline.Token);
        Assert.InRange(disposed.RootElement.GetProperty("elapsedMs").GetInt64(), 0, 2999);
        Assert.False(fixture.Service("subject").HasExited);
        Assert.Contains(pid, fixture.Host.ServiceProcessIds);
        output.WriteLine($"sdkDisposedTwice=true; hostStillRunning=true; servicePid={pid}; serviceStillAlive=true");
    }

    [Fact]
    public async Task Broker_replacement_recovers_a_new_session_without_killing_the_healthy_service()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["counter"], deadline.Token);
        var pid = await host.StartServiceAsync("counter", Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), deadline.Token);
        while (host.States.GetSnapshot("counter")?.IsInteractive != true) await Task.Delay(20, deadline.Token);
        var previous = host.States.GetSnapshot("counter")!.SessionId;
        using var service = Process.GetProcessById(pid);
        _ = service.Handle;
        using var broker = Process.GetProcessById(host.BrokerProcessId);
        broker.Kill(entireProcessTree: true);
        await broker.WaitForExitAsync(deadline.Token);
        using var restored = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        restored.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            while (host.States.GetSnapshot("counter") is not { IsInteractive: true } state || state.SessionId == previous)
                await Task.Delay(20, restored.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Broker replacement did not restore a fresh confirmed session within eight seconds.");
        }
        Assert.False(service.HasExited);
        Assert.Equal(pid, Assert.Single(host.ServiceProcessIds));
        Assert.NotEqual(broker.Id, host.BrokerProcessId);
        Assert.NotEqual(previous, host.States.GetSnapshot("counter")!.SessionId);
    }

    private static ActionSlotReference Slot(string application) => new(application, "main", ActionEntryKind.Component, "counter", "activate");

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-recovery-process-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline;
        private readonly Dictionary<int, Process> processes = [];
        private readonly Dictionary<string, int> services = new(StringComparer.Ordinal);
        private RecoveryFixture(ITestOutputHelper output, TimeSpan timeout)
        {
            this.output = output;
            deadline = new(timeout);
            Directory.CreateDirectory(directory);
        }
        public HostBrokerSession Host { get; private set; } = null!;
        public CancellationToken Token => deadline.Token;
        public string DirectoryFor(string application) => Path.Combine(directory, application);
        public Process Service(string application) => processes[services[application]];

        public static async Task<RecoveryFixture> StartAsync(ITestOutputHelper output, TimeSpan timeout, params (string Id, string Mode)[] registrations)
        {
            var fixture = new RecoveryFixture(output, timeout);
            try
            {
                fixture.Host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), registrations.Select(value => value.Id).ToArray(), fixture.Token);
                fixture.TrackCurrentBroker();
                string probe = Path.Combine(AppContext.BaseDirectory, "RecoveryProcessProbe/Mtp.Recovery.ProcessProbe.dll");
                Assert.True(File.Exists(probe));
                foreach (var registration in registrations)
                {
                    Directory.CreateDirectory(fixture.DirectoryFor(registration.Id));
                    int pid = await fixture.Host.StartServiceAsync(registration.Id, probe, fixture.Token, [registration.Mode, fixture.DirectoryFor(registration.Id)]);
                    fixture.Track(pid);
                    fixture.services.Add(registration.Id, pid);
                    using var marker = await fixture.MarkerAsync(registration.Id, "ready-" + pid);
                    Assert.True(fixture.Host.States.GetSnapshot(registration.Id)!.IsInteractive);
                    output.WriteLine($"application={registration.Id}; mode={registration.Mode}; pid={pid}; session={fixture.Host.States.GetSnapshot(registration.Id)!.SessionId}; brokerPid={fixture.Host.BrokerProcessId}");
                }
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private void Track(int pid)
        {
            if (processes.ContainsKey(pid)) return;
            var process = Process.GetProcessById(pid);
            processes.Add(pid, process);
            _ = process.Handle;
        }

        public void TrackCurrentBroker() => Track(Host.BrokerProcessId);

        public void TrackCurrentServices()
        {
            foreach (int pid in Host.ServiceProcessIds) Track(pid);
        }

        public async Task KillBrokerAsync()
        {
            TrackCurrentBroker();
            var broker = processes[Host.BrokerProcessId];
            broker.Kill(entireProcessTree: true);
            await broker.WaitForExitAsync(Token);
            output.WriteLine($"killedOwnedBroker={broker.Id}; exited=true; exitCode={broker.ExitCode}");
        }

        public async Task UntilAsync(Func<bool> predicate)
        {
            while (!predicate()) await Task.Delay(10, Token);
        }

        public async Task<JsonDocument> MarkerAsync(string application, string marker, CancellationToken? token = null)
        {
            var wait = token ?? Token;
            string path = Path.Combine(DirectoryFor(application), marker + ".json");
            while (!File.Exists(path))
            {
                int pid = services.GetValueOrDefault(application);
                string failed = Path.Combine(DirectoryFor(application), "failed-" + pid + ".json");
                if (File.Exists(failed)) Assert.Fail("Recovery probe failed: " + await File.ReadAllTextAsync(failed, wait));
                await Task.Delay(10, wait);
            }
            string json = await File.ReadAllTextAsync(path, wait);
            output.WriteLine("recoveryMarker=" + json);
            return JsonDocument.Parse(json);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Host is not null)
                {
                    try { TrackCurrentBroker(); } catch (InvalidOperationException) { }
                    foreach (int pid in Host.ServiceProcessIds) Track(pid);
                    await Host.DisposeAsync();
                    await Host.DisposeAsync();
                    Assert.Empty(Host.ServiceProcessIds);
                    Assert.Equal(0, Host.Actions.OutstandingCount);
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var process in processes.Values)
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    Assert.True(process.HasExited);
                    output.WriteLine($"recoveryPid={process.Id}; processExited=true; exitCode={process.ExitCode}");
                }
            }
            finally
            {
                foreach (var process in processes.Values) process.Dispose();
                deadline.Dispose();
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class ActionProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Counter_action_confirms_an_increment_before_the_next_automatic_tick()
    {
        await using var fixture = await ActionFixture.StartAsync(output, ("counter", "counter"));
        var host = fixture.Host;
        var result = await host.SendActionAsync(Slot("counter"), new ActionParameter(), fixture.Token);
        Assert.True(result.Accepted, result.Code);
        Assert.Equal(10, Assert.Single(host.States.GetSnapshot("counter")!.State!.Components).Number);
        Assert.Equal(1, host.States.GetSnapshot("counter")!.State!.Revision);
        output.WriteLine($"actionResult={result.Code}; count=10; revision=1");
    }

    [Fact]
    public async Task Automatic_counter_ticks_preserve_action_increments_and_monotonic_revisions()
    {
        await using var fixture = await ActionFixture.StartAsync(output, ("counter", "counter-fast"));
        long previousRevision = -1;
        for (int index = 1; index <= 20; index++)
        {
            var result = await fixture.Host.SendActionAsync(Slot("counter"), new ActionParameter(), fixture.Token);
            Assert.True(result.Accepted, result.Code);
            var state = fixture.Host.States.GetSnapshot("counter")!.State!;
            Assert.True(state.Revision > previousRevision);
            previousRevision = state.Revision;
            Assert.Equal(9d * index, Assert.Single(state.Components).Number - state.Revision);
        }
        Assert.True(fixture.Host.States.GetSnapshot("counter")!.IsInteractive);
        Assert.Null(fixture.Host.LastError);
        output.WriteLine($"counterActions=20; finalRevision={previousRevision}; countMinusRevision=180; automaticTicksPreserved=true");
    }

    [Fact]
    public async Task Unknown_slots_and_parameters_never_execute_and_business_failure_keeps_confirmed_state()
    {
        await using var fixture = await ActionFixture.StartAsync(output, ("normal", "normal"), ("failed", "fail"));
        var host = fixture.Host;
        foreach (var slot in new[] { Slot("normal") with { ActionSlotId = "missing" }, Slot("unregistered"), Slot("normal") with { FeatureGroupId = "foreign-group" } })
            Assert.Equal("ActionNotAvailable", (await host.SendActionAsync(slot, new ActionParameter(), fixture.Token)).Code);
        Assert.Equal("ActionNotAvailable", (await host.SendActionAsync(Slot("normal"), new ActionParameter(ActionParameterKind.Number, Number: 5), fixture.Token)).Code);
        Assert.Equal("ActionNotAvailable", (await host.SendActionAsync(Slot("normal"), new ActionParameter(Text: "undeclared-field"), fixture.Token)).Code);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryFor("normal"), "started-*.json"));
        var failed = await host.SendActionAsync(Slot("failed"), new ActionParameter(), fixture.Token);
        Assert.False(failed.Accepted);
        Assert.Equal("BusinessRejected", failed.Code);
        Assert.Equal(0, host.States.GetSnapshot("failed")!.State!.Revision);
        Assert.Equal(0, Assert.Single(host.States.GetSnapshot("failed")!.State!.Components).Number);
        Assert.Equal("BusinessRejected", host.Actions.GetLastErrorHint("failed")!.Result.Code);
        Assert.Empty(host.Actions.GetPending("failed"));
        var normal = await host.SendActionAsync(Slot("normal"), new ActionParameter(), fixture.Token);
        Assert.True(normal.Accepted, normal.Code);
        Assert.Equal(10, Assert.Single(host.States.GetSnapshot("normal")!.State!.Components).Number);
        using var finished = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(fixture.DirectoryFor("normal"), "finished-*.json")), fixture.Token));
        Assert.Equal(1, finished.RootElement.GetProperty("call").GetInt64());
        Assert.Equal(0, host.Actions.BusyCount);
        output.WriteLine($"invalidActionsExecuted=0; businessFailure={failed.Code}; failedRevision=0; healthyValue=10; healthyCalls=1");
    }

    [Fact]
    public async Task Four_actions_are_ordered_and_bounded_while_another_service_completes_independently()
    {
        await using var fixture = await ActionFixture.StartAsync(output, ("slow", "slow"), ("fast", "normal"));
        var host = fixture.Host;
        var requests = new List<Task<ProtocolResult>> { host.SendActionAsync(Slot("slow"), new ActionParameter(), fixture.Token) };
        using var started = await fixture.MarkerAsync("slow", "started-1");
        for (int index = 0; index < 3; index++) requests.Add(host.SendActionAsync(Slot("slow"), new ActionParameter(), fixture.Token));
        await UntilAsync(() => host.Actions.GetPending("slow").Count == 4, fixture.Token);
        Assert.All(host.Actions.GetPending("slow"), pending => Assert.True(pending.IsBusy));
        var excess = await host.SendActionAsync(Slot("slow"), new ActionParameter(), fixture.Token);
        Assert.Equal("Busy", excess.Code);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryFor("slow"), "started-2.json")));
        var fast = await host.SendActionAsync(Slot("fast"), new ActionParameter(), fixture.Token);
        Assert.True(fast.Accepted, fast.Code);
        Assert.False(requests[0].IsCompleted);
        Assert.Equal(10, Assert.Single(host.States.GetSnapshot("fast")!.State!.Components).Number);
        for (int index = 1; index <= 4; index++)
        {
            using var next = await fixture.MarkerAsync("slow", "started-" + index);
            Assert.Equal(index, next.RootElement.GetProperty("call").GetInt64());
            Assert.Equal(index - 1, host.States.GetSnapshot("slow")!.State!.Revision);
            await fixture.ReleaseAsync("slow", index);
            Assert.True((await requests[index - 1]).Accepted);
            Assert.Equal(index * 10, Assert.Single(host.States.GetSnapshot("slow")!.State!.Components).Number);
        }
        Assert.Empty(host.Actions.GetPending("slow"));
        Assert.Equal(0, host.Actions.BusyCount);
        Assert.Equal(4, Directory.GetFiles(fixture.DirectoryFor("slow"), "finished-*.json").Length);
        output.WriteLine("slowActions=4; fifth=Busy; sameServiceOrdered=true; otherServiceCompletedWhileBlocked=true; slowValue=40; fastValue=10");
    }

    [Fact]
    public async Task A_real_timeout_releases_busy_and_a_late_success_updates_state_without_a_second_error_hint()
    {
        await using var fixture = await ActionFixture.StartAsync(output, ("slow", "slow"));
        var host = fixture.Host;
        var elapsed = Stopwatch.StartNew();
        var waiting = host.SendActionAsync(Slot("slow"), new ActionParameter(), fixture.Token);
        using var started = await fixture.MarkerAsync("slow", "started-1");
        var result = await waiting;
        Assert.Equal("ActionTimedOut", result.Code);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 4, 9);
        Assert.Equal(0, host.Actions.BusyCount);
        Assert.False(Assert.Single(host.Actions.GetPending("slow")).IsBusy);
        Assert.Equal(0, host.States.GetSnapshot("slow")!.State!.Revision);
        Assert.True(host.States.GetSnapshot("slow")!.IsInteractive);
        var hint = host.Actions.GetLastErrorHint("slow");
        Assert.NotNull(hint);
        Assert.Equal("ActionTimedOut", hint.Result.Code);
        await fixture.ReleaseAsync("slow", 1);
        await UntilAsync(() => host.States.GetSnapshot("slow")!.State!.Revision == 1 && host.Actions.GetPending("slow").Count == 0, fixture.Token);
        Assert.Equal(10, Assert.Single(host.States.GetSnapshot("slow")!.State!.Components).Number);
        Assert.Equal(0, host.Actions.BusyCount);
        Assert.Same(hint, host.Actions.GetLastErrorHint("slow"));
        using var finished = await fixture.MarkerAsync("slow", "finished-1");
        Assert.Equal(1, finished.RootElement.GetProperty("call").GetInt64());
        output.WriteLine($"waitResult=ActionTimedOut; elapsedMs={elapsed.ElapsedMilliseconds}; lateSuccessValue=10; finalRevision=1; repeatedHint=false; businessCalls=1");
    }

    [Fact]
    public async Task Cancelling_the_wait_keeps_the_channel_and_allows_the_matching_late_success()
    {
        await using var fixture = await ActionFixture.StartAsync(output, ("slow", "slow"));
        var host = fixture.Host;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var waiting = host.SendActionAsync(Slot("slow"), new ActionParameter(), caller.Token);
        using var started = await fixture.MarkerAsync("slow", "started-1");
        caller.Cancel();
        Assert.Equal("ActionCancelled", (await waiting).Code);
        Assert.Equal(0, host.Actions.BusyCount);
        Assert.False(Assert.Single(host.Actions.GetPending("slow")).IsBusy);
        Assert.Equal(0, host.States.GetSnapshot("slow")!.State!.Revision);
        await fixture.ReleaseAsync("slow", 1);
        await UntilAsync(() => host.States.GetSnapshot("slow")!.State!.Revision == 1 && host.Actions.GetPending("slow").Count == 0, fixture.Token);
        Assert.True(host.States.GetSnapshot("slow")!.IsInteractive);
        Assert.Equal(10, Assert.Single(host.States.GetSnapshot("slow")!.State!.Components).Number);
        Assert.Null(host.Actions.GetLastErrorHint("slow"));
        output.WriteLine("waitResult=ActionCancelled; lateSuccessValue=10; finalRevision=1; serviceConnected=true");
    }

    [Fact]
    public async Task Concurrent_action_batches_preserve_wire_order_and_complete_every_accepted_request()
    {
        await using var fixture = await ActionFixture.StartAsync(output, ("parallel", "normal"));
        for (int batch = 0; batch < 10; batch++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int arrivals = 0;
            var requests = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref arrivals) == 4) ready.SetResult();
                await start.Task.WaitAsync(fixture.Token);
                return await fixture.Host.SendActionAsync(Slot("parallel"), new ActionParameter(), fixture.Token);
            }, fixture.Token)).ToArray();
            await ready.Task.WaitAsync(fixture.Token);
            start.SetResult();
            var results = await Task.WhenAll(requests);
            Assert.All(results, result => Assert.True(result.Accepted, result.Code));
            Assert.Empty(fixture.Host.Actions.GetPending("parallel"));
            Assert.Equal((batch + 1) * 4, fixture.Host.States.GetSnapshot("parallel")!.State!.Revision);
            Assert.Equal((batch + 1) * 40, Assert.Single(fixture.Host.States.GetSnapshot("parallel")!.State!.Components).Number);
        }
        Assert.Equal(40, Directory.GetFiles(fixture.DirectoryFor("parallel"), "finished-*.json").Length);
        Assert.Null(fixture.Host.Actions.GetLastErrorHint("parallel"));
        output.WriteLine("concurrentBatches=10; requestsPerBatch=4; acceptedCompletions=40; confirmedRevision=40; confirmedValue=400");
    }

    [Fact]
    public async Task Old_session_wrong_sequence_and_duplicate_completions_cannot_replace_confirmed_state()
    {
        await using var fixture = await ActionFixture.StartAsync(output, ("raw", "raw-results"), ("healthy", "normal"));
        var host = fixture.Host;
        var waiting = host.SendActionAsync(Slot("raw"), new ActionParameter(), fixture.Token);
        using var invalid = await fixture.MarkerAsync("raw", "invalid-results-checked");
        Assert.Equal("SessionMismatch", invalid.RootElement.GetProperty("oldSession").GetString());
        Assert.False(waiting.IsCompleted);
        Assert.True(Assert.Single(host.Actions.GetPending("raw")).IsBusy);
        Assert.Equal(0, host.States.GetSnapshot("raw")!.State!.Revision);
        Assert.Equal(0, Assert.Single(host.States.GetSnapshot("raw")!.State!.Components).Number);
        Assert.True((await host.SendActionAsync(Slot("healthy"), new ActionParameter(), fixture.Token)).Accepted);
        Assert.False(waiting.IsCompleted);
        await fixture.ReleaseAsync("raw", 1);
        Assert.True((await waiting).Accepted);
        Assert.Empty(host.Actions.GetPending("raw"));
        Assert.Equal(1, host.States.GetSnapshot("raw")!.State!.Revision);
        Assert.Equal(10, Assert.Single(host.States.GetSnapshot("raw")!.State!.Components).Number);
        await fixture.ReleaseAsync("raw", 2);
        using var complete = await fixture.MarkerAsync("raw", "complete");
        Assert.Equal(2, host.States.GetSnapshot("raw")!.State!.Revision);
        Assert.Equal(10, Assert.Single(host.States.GetSnapshot("raw")!.State!.Components).Number);
        Assert.True(host.States.GetSnapshot("raw")!.IsInteractive);
        Assert.True(host.States.GetSnapshot("healthy")!.IsInteractive);
        Assert.Null(host.Actions.GetLastErrorHint("raw"));
        Assert.Equal(0, host.Actions.OutstandingCount);
        output.WriteLine("oldSession=SessionMismatch; wrongSequenceIgnored=true; duplicateIgnored=true; confirmedRevision=2; confirmedValue=10; otherServiceHealthy=true");
    }

    private static ActionSlotReference Slot(string application) => new(application, "main", ActionEntryKind.Component, "counter", "activate");

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

    private sealed class ActionFixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-action-process-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        private readonly List<Process> processes = [];
        private ActionFixture(ITestOutputHelper output) { this.output = output; Directory.CreateDirectory(directory); }
        public HostBrokerSession Host { get; private set; } = null!;
        public CancellationToken Token => deadline.Token;
        public string DirectoryFor(string application) => Path.Combine(directory, application);

        public static async Task<ActionFixture> StartAsync(ITestOutputHelper output, params (string Id, string Mode)[] services)
        {
            var fixture = new ActionFixture(output);
            try
            {
                fixture.Host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), services.Select(service => service.Id).ToArray(), fixture.Token);
                fixture.Track(fixture.Host.BrokerProcessId);
                foreach (var service in services)
                {
                    string coordination = fixture.DirectoryFor(service.Id);
                    Directory.CreateDirectory(coordination);
                    bool counter = service.Mode.StartsWith("counter", StringComparison.Ordinal);
                    string path = Path.Combine(AppContext.BaseDirectory, counter ? "CounterService/Mtp.CounterService.dll" : "ActionProcessProbe/Mtp.Action.ProcessProbe.dll");
                    Assert.True(File.Exists(path));
                    string[] arguments = counter ? ["--interval-ms", service.Mode == "counter-fast" ? "2" : "60000"] : [service.Mode, coordination];
                    int pid = await fixture.Host.StartServiceAsync(service.Id, path, fixture.Token, arguments: arguments);
                    fixture.Track(pid);
                    if (counter) await UntilAsync(() => fixture.Host.States.GetSnapshot(service.Id)?.IsInteractive == true, fixture.Token);
                    else
                    {
                        using var ready = await fixture.MarkerAsync(service.Id, "ready");
                        Assert.Equal(pid, ready.RootElement.GetProperty("pid").GetInt32());
                    }
                    var snapshot = fixture.Host.States.GetSnapshot(service.Id)!;
                    Assert.True(snapshot.IsInteractive);
                    output.WriteLine($"application={service.Id}; mode={service.Mode}; servicePid={pid}; session={snapshot.SessionId}; brokerPid={fixture.Host.BrokerProcessId}; fixturePid={Environment.ProcessId}");
                }
                Assert.Equal(fixture.processes.Count, fixture.processes.Select(process => process.Id).Distinct().Count());
                Assert.DoesNotContain(Environment.ProcessId, fixture.processes.Select(process => process.Id));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private void Track(int pid)
        {
            var process = Process.GetProcessById(pid);
            processes.Add(process);
            _ = process.Handle;
        }

        public Task ReleaseAsync(string application, int sequence) => File.WriteAllTextAsync(Path.Combine(DirectoryFor(application), "release-" + sequence), "continue", Token);

        public async Task<JsonDocument> MarkerAsync(string application, string marker)
        {
            string path = Path.Combine(DirectoryFor(application), marker + ".json");
            while (!File.Exists(path))
            {
                string failed = Path.Combine(DirectoryFor(application), "failed.json");
                if (File.Exists(failed)) Assert.Fail("Action probe failed: " + await File.ReadAllTextAsync(failed, Token));
                await Task.Delay(10, Token);
            }
            string json = await File.ReadAllTextAsync(path, Token);
            output.WriteLine("actionMarker=" + json);
            return JsonDocument.Parse(json);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Host is not null)
                {
                    await Host.DisposeAsync();
                    await Host.DisposeAsync();
                    Assert.Empty(Host.ServiceProcessIds);
                    Assert.Equal(0, Host.Actions.OutstandingCount);
                    Assert.Equal(0, Host.Actions.BusyCount);
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var process in processes)
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    Assert.True(process.HasExited);
                    output.WriteLine($"pid={process.Id}; processExited=true; exitCode={process.ExitCode}");
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
                deadline.Dispose();
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

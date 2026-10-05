using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class HeartbeatProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task One_host_starts_all_sixteen_registered_services_and_reaps_every_owned_process()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-heartbeat-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var elapsed = Stopwatch.StartNew();
        var processes = new List<Process>();
        string[] applications = Enumerable.Range(1, 16).Select(index => "service-" + index).ToArray();
        HostBrokerSession? host = null;
        try
        {
            host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), applications, deadline.Token);
            Track(host.BrokerProcessId);
            string probe = Path.Combine(AppContext.BaseDirectory, "HeartbeatProcessProbe/Mtp.Heartbeat.ProcessProbe.dll");
            Assert.True(File.Exists(probe));
            foreach (string application in applications)
            {
                string coordination = Path.Combine(directory, application);
                Directory.CreateDirectory(coordination);
                int pid = await host.StartServiceAsync(application, probe, deadline.Token, ["sdk-static", coordination]);
                Track(pid);
                output.WriteLine($"startedApplication={application}; servicePid={pid}; elapsedMs={elapsed.ElapsedMilliseconds}");
            }
            while (applications.Any(application => host.States.GetSnapshot(application)?.IsInteractive != true))
            {
                foreach (string application in applications)
                {
                    string failure = Path.Combine(directory, application, "failed.json");
                    if (File.Exists(failure)) Assert.Fail("Capacity probe failed: " + await File.ReadAllTextAsync(failure, deadline.Token));
                }
                await Task.Delay(10, deadline.Token);
            }
            Assert.Equal(16, host.ServiceProcessIds.Count);
            Assert.Equal(17, processes.Select(process => process.Id).Distinct().Count());
            Assert.DoesNotContain(Environment.ProcessId, processes.Select(process => process.Id));
            foreach (string application in applications)
            {
                var snapshot = host.States.GetSnapshot(application)!;
                Assert.True(snapshot.IsConnected);
                Assert.Null(snapshot.LastError);
                Assert.Equal(0, snapshot.State!.Revision);
                Assert.Equal(7, Assert.Single(snapshot.State.Components).Number);
                output.WriteLine($"interactiveApplication={application}; session={snapshot.SessionId}; revision=0; elapsedMs={elapsed.ElapsedMilliseconds}");
            }
            foreach (string application in new[] { applications[0], applications[^1] })
            {
                var result = await host.SendActionAsync(Slot(application), new(), deadline.Token);
                Assert.True(result.Accepted, result.Code);
                Assert.Equal(17, Assert.Single(host.States.GetSnapshot(application)!.State!.Components).Number);
            }
            Assert.All(processes, process => Assert.False(process.HasExited));
            Assert.All(applications, application => Assert.True(host.States.GetSnapshot(application)!.IsInteractive));
            Assert.Empty(host.ServiceProcessExits);
            Assert.Null(host.LastError);
            output.WriteLine($"capacity=16; allInteractive=true; firstAndLastAction=Accepted; elapsedMs={elapsed.ElapsedMilliseconds}; protocolDeadlineUnchanged=true");
        }
        finally
        {
            try
            {
                if (host is not null)
                {
                    await host.DisposeAsync();
                    await host.DisposeAsync();
                    Assert.Empty(host.ServiceProcessIds);
                    Assert.Equal(0, host.Actions.OutstandingCount);
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var process in processes)
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    Assert.True(process.HasExited);
                    output.WriteLine($"capacityPid={process.Id}; processExited=true; exitCode={process.ExitCode}");
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
                Directory.Delete(directory, recursive: true);
            }
        }

        void Track(int pid)
        {
            var process = Process.GetProcessById(pid);
            processes.Add(process);
            _ = process.Handle;
        }
    }

    [Fact]
    public async Task Static_SDK_business_state_stays_available_beyond_the_heartbeat_deadline()
    {
        await using var fixture = await HeartbeatFixture.StartAsync(output, "sdk-static");
        using var elapsed = await fixture.MarkerAsync("subject", "static-window-complete");
        Assert.True(elapsed.RootElement.GetProperty("elapsedMs").GetInt32() > 5000);
        Assert.Equal(0, elapsed.RootElement.GetProperty("businessPublications").GetInt32());
        var subject = fixture.Host.States.GetSnapshot("subject")!;
        Assert.True(subject.IsInteractive);
        Assert.True(subject.IsConnected);
        Assert.Null(subject.LastError);
        Assert.Equal(0, subject.State!.Revision);
        Assert.Equal(7, Assert.Single(subject.State.Components).Number);
        Assert.True((await fixture.Host.SendActionAsync(Slot("subject"), new(), fixture.Token)).Accepted);
        Assert.Equal(17, Assert.Single(fixture.Host.States.GetSnapshot("subject")!.State!.Components).Number);
        await fixture.AssertHealthyServiceAsync();
        output.WriteLine("staticBusinessMs=6500; businessPublications=0; sdkHeartbeatKeptSession=true; actionAfterIdle=Accepted");
    }

    [Theory]
    [InlineData("raw-silent")]
    [InlineData("raw-duplicate")]
    public async Task Business_updates_and_duplicate_heartbeats_cannot_extend_a_silent_session(string mode)
    {
        await using var fixture = await HeartbeatFixture.StartAsync(output, mode);
        var elapsed = Stopwatch.StartNew();
        await fixture.WaitForFaultAsync("HeartbeatTimedOut");
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 4, 9);
        Assert.False(fixture.Subject.HasExited);
        var failed = fixture.Host.States.GetSnapshot("subject")!;
        Assert.False(failed.IsInteractive);
        Assert.False(failed.IsConnected);
        Assert.True(failed.State!.Revision >= 3);
        Assert.Equal(failed.State.Revision + 7, Assert.Single(failed.State.Components).Number);
        Assert.True(Directory.GetFiles(fixture.DirectoryFor("subject"), "state-*.json").Length >= 3);
        Assert.Equal("ActionNotAvailable", (await fixture.Host.SendActionAsync(Slot("subject"), new(), fixture.Token)).Code);
        await fixture.AssertHealthyServiceAsync();
        var after = fixture.Host.States.GetSnapshot("subject")!;
        Assert.Equal("HeartbeatTimedOut", after.LastError!.Code);
        Assert.Equal(failed.State, after.State);
        Assert.False(fixture.Subject.HasExited);
        output.WriteLine($"mode={mode}; failure=HeartbeatTimedOut; elapsedMs={elapsed.ElapsedMilliseconds}; lastRevision={failed.State.Revision}; liveProcess=true; lastValueRetained=true; healthyAction=Accepted");
    }

    [Fact]
    public async Task A_closed_pipe_is_distinguished_from_a_process_exit_and_isolates_only_that_service()
    {
        await using var fixture = await HeartbeatFixture.StartAsync(output, "pipe-close");
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryFor("subject"), "close-pipe"), "close", fixture.Token);
        using var marker = await fixture.MarkerAsync("subject", "pipe-closed");
        await fixture.WaitForFaultAsync("PipeDisconnected");
        Assert.False(fixture.Subject.HasExited);
        Assert.Equal(7, Assert.Single(fixture.Host.States.GetSnapshot("subject")!.State!.Components).Number);
        Assert.False(fixture.Host.States.GetSnapshot("subject")!.IsInteractive);
        Assert.Equal("ActionNotAvailable", (await fixture.Host.SendActionAsync(Slot("subject"), new(), fixture.Token)).Code);
        await fixture.AssertHealthyServiceAsync();
        Assert.Equal("PipeDisconnected", fixture.Host.States.GetSnapshot("subject")!.LastError!.Code);
        Assert.False(fixture.Subject.HasExited);
        output.WriteLine("failure=PipeDisconnected; liveProcess=true; confirmedValue=7; healthyAction=Accepted");
    }

    [Fact]
    public async Task An_owned_service_process_exit_keeps_its_last_value_and_other_services_available()
    {
        await using var fixture = await HeartbeatFixture.StartAsync(output, "sdk-static");
        string session = fixture.Host.States.GetSnapshot("subject")!.SessionId;
        int processId = fixture.Subject.Id;
        fixture.Subject.Kill(entireProcessTree: true);
        await fixture.Subject.WaitForExitAsync(fixture.Token);
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        observation.CancelAfter(TimeSpan.FromSeconds(5));
        while (fixture.Host.States.GetSnapshot("subject")!.IsConnected)
            await Task.Delay(10, observation.Token);
        Assert.True(fixture.Subject.HasExited);
        var failed = fixture.Host.States.GetSnapshot("subject")!;
        var firstReason = failed.LastError;
        Assert.NotNull(firstReason);
        Assert.Contains(firstReason.Code, new[] { "ProcessExited", "PipeDisconnected" });
        while (!fixture.Host.ServiceProcessExits.Any(exit => exit.ApplicationId == "subject" && exit.SessionId == session && exit.ProcessId == processId))
            await Task.Delay(10, observation.Token);
        var exit = Assert.Single(fixture.Host.ServiceProcessExits, value => value.ApplicationId == "subject");
        Assert.Equal(session, exit.SessionId);
        Assert.Equal(processId, exit.ProcessId);
        Assert.Equal(fixture.Subject.ExitCode, exit.ExitCode);
        Assert.False(failed.IsConnected);
        Assert.False(failed.IsInteractive);
        Assert.Equal(0, failed.State!.Revision);
        Assert.Equal(7, Assert.Single(failed.State.Components).Number);
        Assert.Equal("ActionNotAvailable", (await fixture.Host.SendActionAsync(Slot("subject"), new(), fixture.Token)).Code);
        await fixture.AssertHealthyServiceAsync();
        Assert.Same(firstReason, fixture.Host.States.GetSnapshot("subject")!.LastError);
        Assert.Same(exit, Assert.Single(fixture.Host.ServiceProcessExits, value => value.ApplicationId == "subject"));
        output.WriteLine($"firstFailure={firstReason.Code}; firstFailurePreserved=true; ownedPid={processId}; session={session}; processExited=true; confirmedExitCode={exit.ExitCode}; confirmedValue=7; healthyAction=Accepted");
    }

    private static ActionSlotReference Slot(string application) => new(application, "main", ActionEntryKind.Component, "counter", "activate");

    private sealed class HeartbeatFixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-heartbeat-process-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        private readonly Dictionary<string, Process> processes = new(StringComparer.Ordinal);
        private HeartbeatFixture(ITestOutputHelper output) { this.output = output; Directory.CreateDirectory(directory); }
        public HostBrokerSession Host { get; private set; } = null!;
        public Process Subject => processes["subject"];
        public CancellationToken Token => deadline.Token;
        public string DirectoryFor(string application) => Path.Combine(directory, application);

        public static async Task<HeartbeatFixture> StartAsync(ITestOutputHelper output, string mode)
        {
            var fixture = new HeartbeatFixture(output);
            try
            {
                fixture.Host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["subject", "healthy"], fixture.Token);
                fixture.Track("broker", fixture.Host.BrokerProcessId);
                string path = Path.Combine(AppContext.BaseDirectory, "HeartbeatProcessProbe/Mtp.Heartbeat.ProcessProbe.dll");
                Assert.True(File.Exists(path));
                foreach (var service in new[] { (Id: "healthy", Mode: "sdk-static"), (Id: "subject", Mode: mode) })
                {
                    Directory.CreateDirectory(fixture.DirectoryFor(service.Id));
                    int pid = await fixture.Host.StartServiceAsync(service.Id, path, fixture.Token, [service.Mode, fixture.DirectoryFor(service.Id)]);
                    fixture.Track(service.Id, pid);
                    using var ready = await fixture.MarkerAsync(service.Id, "ready");
                    Assert.Equal(pid, ready.RootElement.GetProperty("pid").GetInt32());
                    Assert.True(fixture.Host.States.GetSnapshot(service.Id)!.IsInteractive);
                    output.WriteLine($"application={service.Id}; mode={service.Mode}; servicePid={pid}; session={fixture.Host.States.GetSnapshot(service.Id)!.SessionId}; brokerPid={fixture.Host.BrokerProcessId}; fixturePid={Environment.ProcessId}");
                }
                Assert.Equal(3, fixture.processes.Values.Select(process => process.Id).Distinct().Count());
                Assert.DoesNotContain(Environment.ProcessId, fixture.processes.Values.Select(process => process.Id));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private void Track(string name, int pid)
        {
            var process = Process.GetProcessById(pid);
            processes.Add(name, process);
            _ = process.Handle;
        }

        public async Task WaitForFaultAsync(string expected)
        {
            using var faultDeadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            faultDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (Host.States.GetSnapshot("subject")?.LastError?.Code != expected)
            {
                string failed = Path.Combine(DirectoryFor("subject"), "failed.json");
                if (File.Exists(failed)) Assert.Fail("Heartbeat probe failed: " + await File.ReadAllTextAsync(failed, Token));
                try { await Task.Delay(10, faultDeadline.Token); }
                catch (OperationCanceledException)
                {
                    Assert.Fail($"Expected {expected}; actual={Host.States.GetSnapshot("subject")?.LastError?.Code ?? "none"}; processExited={Subject.HasExited}");
                    throw;
                }
            }
        }

        public async Task AssertHealthyServiceAsync()
        {
            var healthy = Host.States.GetSnapshot("healthy")!;
            Assert.True(healthy.IsInteractive);
            Assert.True(healthy.IsConnected);
            Assert.Null(healthy.LastError);
            Assert.False(processes["healthy"].HasExited);
            var result = await Host.SendActionAsync(Slot("healthy"), new(), Token);
            Assert.True(result.Accepted, result.Code);
            Assert.Equal(17, Assert.Single(Host.States.GetSnapshot("healthy")!.State!.Components).Number);
            Assert.Null(Host.LastError);
        }

        public async Task<JsonDocument> MarkerAsync(string application, string marker)
        {
            string path = Path.Combine(DirectoryFor(application), marker + ".json");
            while (!File.Exists(path))
            {
                string failed = Path.Combine(DirectoryFor(application), "failed.json");
                if (File.Exists(failed)) Assert.Fail("Heartbeat probe failed: " + await File.ReadAllTextAsync(failed, Token));
                await Task.Delay(10, Token);
            }
            string json = await File.ReadAllTextAsync(path, Token);
            output.WriteLine("heartbeatMarker=" + json);
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
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var process in processes.Values)
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    Assert.True(process.HasExited);
                    output.WriteLine($"pid={process.Id}; processExited=true; exitCode={process.ExitCode}");
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

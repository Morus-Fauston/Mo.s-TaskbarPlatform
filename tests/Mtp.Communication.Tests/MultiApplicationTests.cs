using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class MultiApplicationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Two_sdk_services_reach_distinct_final_revisions_and_clean_up_over_two_runs()
    {
        for (int round = 1; round <= 2; round++)
            await RunPairAsync("high-frequency", round);
    }

    [Fact]
    public Task Bad_states_old_revisions_and_forged_load_preserve_the_valid_snapshot_and_other_application() => RunPairAsync("bad-state", 1);

    [Fact]
    public Task A_service_that_stops_reading_responses_does_not_starve_a_healthy_service() => RunPairAsync("slow-reader", 1);

    private async Task RunPairAsync(string mode, int round)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-communication-pair-" + Guid.NewGuid().ToString("N"));
        string subjectDirectory = Path.Combine(directory, "subject");
        string healthyDirectory = Path.Combine(directory, "healthy");
        Directory.CreateDirectory(subjectDirectory);
        Directory.CreateDirectory(healthyDirectory);
        string brokerPath = Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll");
        string probePath = Path.Combine(AppContext.BaseDirectory, "CommunicationProcessProbe/Mtp.Communication.ProcessProbe.dll");
        Assert.True(File.Exists(probePath), "The independently built communication process probe must be copied to test output.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        HostBrokerSession? host = null;
        var processIds = new List<int>();
        try
        {
            host = await HostBrokerSession.StartAsync(brokerPath, ["subject", "healthy"], timeout.Token);
            processIds.Add(host.BrokerProcessId);
            int subjectPid = await host.StartServiceAsync("subject", probePath, timeout.Token, arguments: [mode, subjectDirectory]);
            processIds.Add(subjectPid);
            int healthyPid = await host.StartServiceAsync("healthy", probePath, timeout.Token, arguments: ["normal", healthyDirectory]);
            processIds.Add(healthyPid);
            Assert.Equal(3, processIds.Distinct().Count());
            Assert.DoesNotContain(Environment.ProcessId, processIds);
            output.WriteLine($"mode={mode}; round={round}; hostPid={Environment.ProcessId}; brokerPid={host.BrokerProcessId}; subjectPid={subjectPid}; healthyPid={healthyPid}");
            using var subjectReady = await WaitMarkerAsync(subjectDirectory, "ready", timeout.Token);
            using var healthyReady = await WaitMarkerAsync(healthyDirectory, "ready", timeout.Token);
            Assert.Equal(subjectPid, subjectReady.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(healthyPid, healthyReady.RootElement.GetProperty("pid").GetInt32());
            var frozenSubject = host.States.GetSnapshot("subject")!;
            var frozenHealthy = host.States.GetSnapshot("healthy")!;
            Assert.Equal(0, frozenSubject.State!.Revision);
            Assert.Equal(0, frozenHealthy.State!.Revision);
            await File.WriteAllTextAsync(Path.Combine(subjectDirectory, "run"), "run", timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(healthyDirectory, "run"), "run", timeout.Token);

            if (mode == "bad-state")
            {
                using var checkpoint = await WaitMarkerAsync(subjectDirectory, "checkpoint", timeout.Token);
                Assert.Equal(subjectPid, checkpoint.RootElement.GetProperty("pid").GetInt32());
                var errors = checkpoint.RootElement.GetProperty("rejections");
                Assert.Equal(6, errors.EnumerateObject().Count());
                Assert.Equal("InvalidState", errors.GetProperty("unknown-component").GetString());
                Assert.Equal("InvalidState", errors.GetProperty("duplicate-component").GetString());
                Assert.Equal("StaleRevision", errors.GetProperty("duplicate-revision").GetString());
                Assert.Equal("StaleRevision", errors.GetProperty("old-revision").GetString());
                Assert.Equal("SessionMismatch", errors.GetProperty("old-session").GetString());
                Assert.Equal("InvalidEnvelope", errors.GetProperty("forged-load").GetString());
                var preserved = host.States.GetSnapshot("subject")!;
                Assert.Equal(500, preserved.State!.Revision);
                Assert.Equal("500", Assert.Single(preserved.State.Components).Text);
                Assert.Equal(500, preserved.State.Components[0].Number);
                Assert.True(preserved.IsInteractive);
                Assert.InRange(host.PeakPendingRequests, 1, ProtocolLimits.MaximumApplications);
                output.WriteLine("badStateCheckpoint=" + checkpoint.RootElement.GetRawText());
                await File.WriteAllTextAsync(Path.Combine(subjectDirectory, "continue"), "continue", timeout.Token);
            }

            using var healthyComplete = await WaitMarkerAsync(healthyDirectory, "complete", timeout.Token);
            Assert.Equal(healthyPid, healthyComplete.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(500, healthyComplete.RootElement.GetProperty("accepted").GetInt32());
            var healthy = host.States.GetSnapshot("healthy")!;
            Assert.Equal(500, healthy.State!.Revision);
            Assert.Equal("complete:normal", Assert.Single(healthy.State.Components).Text);
            Assert.Equal(500, healthy.State.Components[0].Number);
            Assert.True(healthy.IsInteractive);
            using var subjectComplete = await WaitMarkerAsync(subjectDirectory, "complete", timeout.Token);
            Assert.Equal(subjectPid, subjectComplete.RootElement.GetProperty("pid").GetInt32());
            var subject = host.States.GetSnapshot("subject")!;
            if (mode == "slow-reader")
            {
                Assert.InRange(subjectComplete.RootElement.GetProperty("writtenFrames").GetInt32(), 1, 10_000);
                Assert.Contains(subjectComplete.RootElement.GetProperty("outcome").GetString(), new[] { "bounded-cancellation", "connection-closed", "frame-limit" });
                Assert.InRange(subject.State!.Revision, 1, 10_000);
            }
            else
            {
                Assert.Equal(1000, subject.State!.Revision);
                Assert.Equal("complete:" + mode, Assert.Single(subject.State.Components).Text);
                Assert.Equal(1000, subject.State.Components[0].Number);
                Assert.True(subject.IsInteractive);
            }
            Assert.Equal(2, host.States.Snapshots.Count);
            Assert.Equal(0, frozenSubject.State.Revision);
            Assert.Equal(0, frozenHealthy.State.Revision);
            Assert.Equal("0", Assert.Single(frozenSubject.State.Components).Text);
            Assert.Equal("0", Assert.Single(frozenHealthy.State.Components).Text);
            Assert.InRange(host.PeakPendingRequests, 1, ProtocolLimits.MaximumApplications);
            Assert.Null(host.LastError);
            output.WriteLine("healthyComplete=" + healthyComplete.RootElement.GetRawText());
            output.WriteLine("subjectComplete=" + subjectComplete.RootElement.GetRawText());
            output.WriteLine($"mode={mode}; round={round}; finalSubjectRevision={subject.State.Revision}; finalHealthyRevision={healthy.State.Revision}; queuePeak={host.PeakPendingRequests}; retainedApplications={host.States.Snapshots.Count}; frozenSnapshotsUnchanged=true");
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
                await host.DisposeAsync();
                Assert.Empty(host.ServiceProcessIds);
            }
            foreach (int processId in processIds)
            {
                Assert.False(IsRunning(processId), $"Owned process {processId} must exit.");
                output.WriteLine($"mode={mode}; round={round}; pid={processId}; processExited=true");
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<JsonDocument> WaitMarkerAsync(string directory, string marker, CancellationToken token)
    {
        string path = Path.Combine(directory, marker + ".json");
        string failure = Path.Combine(directory, "failed.json");
        while (!File.Exists(path))
        {
            if (File.Exists(failure))
            {
                string message = await File.ReadAllTextAsync(failure, token);
                Assert.Fail("Independent probe reported failure: " + message);
            }
            await Task.Delay(10, token);
        }
        return JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
    }

    private static bool IsRunning(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}

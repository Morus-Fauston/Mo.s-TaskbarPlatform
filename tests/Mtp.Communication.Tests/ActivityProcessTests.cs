using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class ActivityProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Closed_entry_accepts_existing_updates_rejects_new_activity_and_still_processes_end_and_expiry()
    {
        await using var fixture = await ActivityFixture.StartAsync(output);
        await fixture.AllowAsync("activities", true, publication: 1);
        var host = fixture.Host;
        Assert.True(host.States.SetEntryDisplayAllowed("activities", "main", "island", false).Accepted);
        using var added = await fixture.CommandAsync("activities", "update-add");
        var result = added.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.Equal("AcceptedWithActivityRejections", result.GetProperty("code").GetString());
        var rejection = Assert.Single(result.GetProperty("activityRejections").EnumerateArray());
        Assert.Equal("running-b", rejection.GetProperty("activityId").GetString());
        Assert.Equal("DisplayNotAllowed", rejection.GetProperty("code").GetString());
        var projected = fixture.Content("activities");
        Assert.Equal("running-a", Assert.Single(projected.Activities).ActivityId);
        var shared = Assert.Single(projected.Items);
        Assert.Equal("shared-item", shared.ItemId);
        Assert.Equal("running-a", Assert.Single(shared.ActivityIds));
        using var ended = await fixture.CommandAsync("activities", "end-a");
        Assert.True(ended.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Empty(fixture.Content("activities").Activities);
        Assert.Empty(fixture.Content("activities").Items);
        await fixture.AllowAsync("activities", true, publication: 2);
        using var expiring = await fixture.CommandAsync("activities", "expires-short");
        Assert.True(expiring.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
        long revision = host.States.GetSnapshot("activities")!.State!.Revision;
        Assert.True(host.States.SetEntryDisplayAllowed("activities", "main", "island", false).Accepted);
        await fixture.UntilAsync(() => fixture.Content("activities").Activities.Count == 0);
        Assert.Empty(fixture.Content("activities").Items);
        Assert.Equal(revision, host.States.GetSnapshot("activities")!.State!.Revision);
        Assert.Equal(7, Assert.Single(host.States.GetSnapshot("activities")!.State!.Components, value => value.ComponentId == "ordinary").Number);
        Assert.True(host.States.GetSnapshot("activities")!.IsInteractive);
        await fixture.AllowAsync("healthy", true, publication: 1);
        Assert.Equal("running-a", Assert.Single(fixture.Content("healthy").Activities).ActivityId);
        output.WriteLine("closedExistingUpdate=Accepted; newActivity=DisplayNotAllowed; sharedReferencesFiltered=true; hiddenEndAndExpiryApplied=true; expiryDidNotChangeRevision=true; otherApplicationPublished=true");
    }

    [Fact]
    public async Task Broker_reconnection_redeclares_current_activity_and_updates_initial_rejection_details()
    {
        await using var fixture = await ActivityFixture.StartAsync(output);
        await fixture.AllowAsync("activities", true, publication: 1);
        Assert.True(fixture.Host.States.SetEntryDisplayAllowed("activities", "main", "island", false).Accepted);
        using var updated = await fixture.CommandAsync("activities", "update-add");
        string oldSession = fixture.Host.States.GetSnapshot("activities")!.SessionId;
        int originalPid = fixture.Subject.Id;
        await fixture.KillBrokerAsync();
        await fixture.UntilAsync(() => fixture.Host.States.GetSnapshot("activities") is { IsInteractive: true } snapshot && snapshot.SessionId != oldSession);
        fixture.TrackCurrentBroker();
        Assert.Contains(originalPid, fixture.Host.ServiceProcessIds);
        Assert.False(fixture.Subject.HasExited);
        using var report = await fixture.CommandAsync("activities", "report");
        Assert.NotEqual(oldSession, report.RootElement.GetProperty("sessionId").GetString());
        var initial = report.RootElement.GetProperty("initialResult");
        Assert.Equal("AcceptedWithActivityRejections", initial.GetProperty("code").GetString());
        Assert.Equal("running-b", Assert.Single(initial.GetProperty("activityRejections").EnumerateArray()).GetProperty("activityId").GetString());
        Assert.Equal("running-a", Assert.Single(fixture.Content("activities").Activities).ActivityId);
        Assert.Equal("running-a", Assert.Single(Assert.Single(fixture.Content("activities").Items).ActivityIds));
        Assert.False(Assert.Single(fixture.Host.States.GetDisplayPermissions("activities")!.Entries).Allowed);
        await fixture.AllowAsync("healthy", true, publication: 1);
        output.WriteLine($"reconnectedServicePid={originalPid}; oldSession={oldSession}; newSession={fixture.Host.States.GetSnapshot("activities")!.SessionId}; currentActivityReconfirmed=true; rejectedActivityNotStored=true; refreshedInitialPublicationResult=true");
    }

    [Fact]
    public async Task Old_session_permission_callback_is_cancelled_before_a_new_closed_session_can_publish()
    {
        await using var fixture = await ActivityFixture.StartAsync(output);
        string directory = fixture.DirectoryFor("activities");
        await File.WriteAllTextAsync(Path.Combine(directory, "hold-permission"), "hold", fixture.Token);
        Assert.True(fixture.Host.States.SetEntryDisplayAllowed("activities", "main", "island", true).Accepted);
        using var held = await fixture.MarkerAsync("activities", "permission-held");
        string oldSession = fixture.Host.States.GetSnapshot("activities")!.SessionId;
        Assert.True(fixture.Host.States.SetEntryDisplayAllowed("activities", "main", "island", false).Accepted);
        await fixture.KillBrokerAsync();
        using var cancelled = await fixture.MarkerAsync("activities", "permission-cancelled");
        await fixture.UntilAsync(() => fixture.Host.States.GetSnapshot("activities") is { IsInteractive: true } current && current.SessionId != oldSession);
        fixture.TrackCurrentBroker();
        await File.WriteAllTextAsync(Path.Combine(directory, "release-permission"), "release stale callback", fixture.Token);
        using var report = await fixture.CommandAsync("activities", "report");
        Assert.Equal(0, report.RootElement.GetProperty("publications").GetInt32());
        Assert.Empty(fixture.Content("activities").Activities);
        Assert.False(File.Exists(Path.Combine(directory, "permission-published.json")));
        Assert.Equal("AcceptedWithActivityRejections", report.RootElement.GetProperty("initialResult").GetProperty("code").GetString());
        await fixture.AllowAsync("healthy", true, publication: 1);
        output.WriteLine("oldPermissionCallbackCancelled=true; oldCallbackPublications=0; newSessionDisplayAllowed=false; otherApplicationPublished=true");
    }

    [Fact]
    public async Task A_new_Host_instance_starts_without_activity_instances_and_requires_a_fresh_provider_submission()
    {
        await using (var previous = await ActivityFixture.StartAsync(output))
        {
            await previous.AllowAsync("activities", true, publication: 1);
            Assert.Single(previous.Content("activities").Activities);
        }
        await using var current = await ActivityFixture.StartAsync(output, startSubject: false);
        Assert.Null(current.Host.States.GetSnapshot("activities"));
        await current.StartSubjectAsync();
        Assert.Empty(current.Content("activities").Activities);
        Assert.True(current.Host.States.GetSnapshot("activities")!.IsInteractive);
        await current.AllowAsync("activities", true, publication: 1);
        Assert.Equal("running-a", Assert.Single(current.Content("activities").Activities).ActivityId);
        output.WriteLine("newHostHasNoActivitySnapshot=true; newProviderInitiallyRejected=true; freshProviderPublicationRequired=true");
    }

    [Fact]
    public async Task Initial_activity_rejection_keeps_the_service_usable_and_permission_notification_prompts_a_fresh_publication()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-activity-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HostBrokerSession? host = null;
        var processes = new List<Process>();
        try
        {
            host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["activities"], deadline.Token);
            Track(host.BrokerProcessId);
            string probe = Path.Combine(AppContext.BaseDirectory, "ActivityProcessProbe/Mtp.Activity.ProcessProbe.dll");
            Assert.True(File.Exists(probe));
            int pid = await host.StartServiceAsync("activities", probe, deadline.Token, [directory]);
            Track(pid);
            using var ready = await MarkerAsync("ready");
            Assert.Equal(pid, ready.RootElement.GetProperty("pid").GetInt32());
            Assert.True(ready.RootElement.GetProperty("connected").GetBoolean());
            var initial = ready.RootElement.GetProperty("initialResult");
            Assert.True(initial.GetProperty("accepted").GetBoolean());
            Assert.Equal("AcceptedWithActivityRejections", initial.GetProperty("code").GetString());
            var rejection = Assert.Single(initial.GetProperty("activityRejections").EnumerateArray());
            Assert.Equal("activities", rejection.GetProperty("applicationId").GetString());
            Assert.Equal("main", rejection.GetProperty("featureGroupId").GetString());
            Assert.Equal("island", rejection.GetProperty("componentId").GetString());
            Assert.Equal("running-a", rejection.GetProperty("activityId").GetString());
            Assert.Equal("DisplayNotAllowed", rejection.GetProperty("code").GetString());
            string session = host.States.GetSnapshot("activities")!.SessionId;
            Assert.True(host.States.GetSnapshot("activities")!.IsInteractive);
            Assert.Equal(7, Assert.Single(host.States.GetSnapshot("activities")!.State!.Components, value => value.ComponentId == "ordinary").Number);
            var hidden = Assert.Single(host.States.GetSnapshot("activities")!.State!.DynamicEntries!);
            Assert.Empty(hidden.Content.Activities);
            Assert.Empty(hidden.Content.Items);
            Assert.False(File.Exists(Path.Combine(directory, "permission-published.json")));

            host.States.SetEntryDisplayAllowed("activities", "main", "island", true);
            using var published = await MarkerAsync("permission-published");
            Assert.True(published.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
            Assert.Equal(1, published.RootElement.GetProperty("publication").GetInt32());
            Assert.Equal(1, published.RootElement.GetProperty("stateRevision").GetInt64());
            Assert.Equal(session, host.States.GetSnapshot("activities")!.SessionId);
            Assert.True(host.States.GetSnapshot("activities")!.IsInteractive);
            var accepted = Assert.Single(host.States.GetSnapshot("activities")!.State!.DynamicEntries!);
            Assert.Equal("running-a", Assert.Single(accepted.Content.Activities).ActivityId);
            Assert.Equal("item-a", Assert.Single(accepted.Content.Items).ItemId);
            Assert.Null(host.LastError);
            output.WriteLine($"application=activities; pid={pid}; session={session}; initial=AcceptedWithActivityRejections; normalReading=7; resumedViaProviderPublication=true; publicationCount=1");
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
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var process in processes)
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    Assert.True(process.HasExited);
                    output.WriteLine($"activityPid={process.Id}; processExited=true; exitCode={process.ExitCode}");
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
        async Task<JsonDocument> MarkerAsync(string name)
        {
            string path = Path.Combine(directory, name + ".json");
            while (!File.Exists(path))
            {
                string failed = Path.Combine(directory, "failed.json");
                if (File.Exists(failed)) Assert.Fail("Activity probe failed: " + await File.ReadAllTextAsync(failed, deadline.Token));
                await Task.Delay(10, deadline.Token);
            }
            string json = await File.ReadAllTextAsync(path, deadline.Token);
            output.WriteLine("activityMarker=" + json);
            return JsonDocument.Parse(json);
        }
    }
    private sealed class ActivityFixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-activity-scenarios-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        private readonly Dictionary<int, Process> processes = [];
        private readonly Dictionary<string, int> commands = new(StringComparer.Ordinal);
        private int subjectPid;
        private ActivityFixture(ITestOutputHelper output) { this.output = output; Directory.CreateDirectory(directory); }
        public HostBrokerSession Host { get; private set; } = null!;
        public CancellationToken Token => deadline.Token;
        public Process Subject => processes[subjectPid];
        public string DirectoryFor(string application) => Path.Combine(directory, application);
        public DynamicContentState Content(string application) => Assert.Single(Host.States.GetSnapshot(application)!.State!.DynamicEntries!).Content;

        public static async Task<ActivityFixture> StartAsync(ITestOutputHelper output, bool startSubject = true)
        {
            var fixture = new ActivityFixture(output);
            try
            {
                fixture.Host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["activities", "healthy"], fixture.Token);
                fixture.TrackCurrentBroker();
                await fixture.StartServiceAsync("healthy");
                if (startSubject) await fixture.StartSubjectAsync();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task StartSubjectAsync() => subjectPid = await StartServiceAsync("activities");

        private async Task<int> StartServiceAsync(string application)
        {
            Directory.CreateDirectory(DirectoryFor(application));
            int pid = await Host.StartServiceAsync(application, Path.Combine(AppContext.BaseDirectory, "ActivityProcessProbe/Mtp.Activity.ProcessProbe.dll"), Token, [DirectoryFor(application)]);
            Track(pid);
            using var ready = await MarkerAsync(application, "ready");
            Assert.Equal(pid, ready.RootElement.GetProperty("pid").GetInt32());
            Assert.True(Host.States.GetSnapshot(application)!.IsInteractive);
            return pid;
        }

        private void Track(int pid)
        {
            if (processes.ContainsKey(pid)) return;
            var process = Process.GetProcessById(pid);
            processes.Add(pid, process);
            _ = process.Handle;
        }
        public void TrackCurrentBroker() => Track(Host.BrokerProcessId);

        public async Task KillBrokerAsync()
        {
            TrackCurrentBroker();
            var broker = processes[Host.BrokerProcessId];
            broker.Kill(entireProcessTree: true);
            await broker.WaitForExitAsync(Token);
            output.WriteLine($"killedActivityBroker={broker.Id}; exited=true; exitCode={broker.ExitCode}");
        }

        public async Task AllowAsync(string application, bool allowed, int publication)
        {
            Assert.True(Host.States.SetEntryDisplayAllowed(application, "main", "island", allowed).Accepted);
            using var marker = await MarkerAsync(application, "publication-" + publication);
            var result = marker.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("accepted").GetBoolean(), result.GetProperty("code").GetString());
        }

        public async Task<JsonDocument> CommandAsync(string application, string scenario)
        {
            int command = commands.GetValueOrDefault(application) + 1;
            commands[application] = command;
            string path = Path.Combine(DirectoryFor(application), "command-" + command);
            await File.WriteAllTextAsync(path + ".tmp", scenario, Token);
            File.Move(path + ".tmp", path);
            return await MarkerAsync(application, "command-" + command + "-result");
        }

        public async Task UntilAsync(Func<bool> predicate)
        {
            while (!predicate()) await Task.Delay(10, Token);
        }

        public async Task<JsonDocument> MarkerAsync(string application, string name)
        {
            string path = Path.Combine(DirectoryFor(application), name + ".json");
            while (!File.Exists(path))
            {
                string failed = Path.Combine(DirectoryFor(application), "failed.json");
                if (File.Exists(failed)) Assert.Fail("Activity probe failed: " + await File.ReadAllTextAsync(failed, Token));
                await Task.Delay(10, Token);
            }
            string json = await File.ReadAllTextAsync(path, Token);
            output.WriteLine("activityMarker=" + json);
            return JsonDocument.Parse(json);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Host is not null)
                {
                    try { TrackCurrentBroker(); } catch (InvalidOperationException) { }
                    await Host.DisposeAsync();
                    await Host.DisposeAsync();
                    Assert.Empty(Host.ServiceProcessIds);
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var process in processes.Values)
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    Assert.True(process.HasExited);
                    output.WriteLine($"activityPid={process.Id}; processExited=true; exitCode={process.ExitCode}");
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

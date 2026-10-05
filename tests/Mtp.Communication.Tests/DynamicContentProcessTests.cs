using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class DynamicContentProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Dynamic_collections_and_island_fields_cross_processes_in_one_session_and_rejections_keep_the_snapshot()
    {
        await RunAsync("phases", async (host, servicePid, directory, token) =>
        {
            using var ready = await MarkerAsync(directory, "ready", token);
            string session = ready.RootElement.GetProperty("sessionId").GetString()!;
            Assert.Equal(servicePid, ready.RootElement.GetProperty("pid").GetInt32());
            var initial = host.States.GetSnapshot("dynamic")!;
            Assert.True(initial.IsInteractive);
            Assert.Equal(session, initial.SessionId);
            Assert.Equal(0, initial.State!.Revision);
            Assert.Equal(2, initial.Declaration!.DynamicContents.Count);
            var ordinaryDeclaration = initial.Declaration.DynamicContents.Single(x => x.Declaration.Kind == DynamicContentKind.OrdinaryItems).Declaration;
            var islandDeclaration = initial.Declaration.DynamicContents.Single(x => x.Declaration.Kind == DynamicContentKind.LiveIsland).Declaration;
            Assert.Equal(DynamicGrouping.UserChoice, islandDeclaration.Grouping);
            var ordinaryStructure = Assert.Single(ordinaryDeclaration.Structures);
            Assert.Equal("ring", ordinaryStructure.StructureId);
            Assert.True(ordinaryStructure.IsRepeated);
            Assert.Equal(1, ordinaryStructure.Normal.Width.Slots);
            Assert.Equal(3, ordinaryStructure.Expanded!.Width.Slots);
            Assert.Equal("ring", Assert.Single(ordinaryStructure.ExpandTargetStructureIds!));
            Assert.Equal(SemanticAnimation.ContentChange, ordinaryStructure.Animation);
            Assert.Equal(PresetTemplate.Composite, Assert.Single(islandDeclaration.Structures).Normal.Template);
            Assert.True(host.States.SetEntryDisplayAllowed("dynamic", "main", "island", true).Accepted);
            await ContinueAsync(directory, "ready", token);

            using var addedMarker = await MarkerAsync(directory, "added", token);
            var added = Snapshot(host, session, 1);
            AssertMarker(addedMarker, servicePid, session, 1);
            var addedIsland = Entry(added, "island");
            Assert.Equal(2, addedIsland.Activities.Count);
            Assert.Equal(2, addedIsland.Items.Count);
            var countdown = addedIsland.Items.Single(x => x.ItemId == "visible-a");
            Assert.Equal("business-a", Assert.Single(countdown.ActivityIds));
            Assert.NotEqual(countdown.ItemId, countdown.ActivityIds[0]);
            Assert.Equal(TimerDirection.CountDown, countdown.Fields.Timer!.Direction);
            Assert.False(countdown.Fields.Timer.IsPaused);
            Assert.Equal(90_000, countdown.Fields.Timer.ValueMillisecondsAtReference);
            Assert.Equal(CounterSemantics.CurrentIndex, countdown.Fields.Counter!.Semantics);
            Assert.Equal(7, countdown.Fields.Counter.Value);
            Assert.Equal(25, countdown.Fields.Progress!.Value);
            Assert.Equal(100, countdown.Fields.Progress.Maximum);
            var countUp = addedIsland.Items.Single(x => x.ItemId == "visible-b");
            Assert.Equal(TimerDirection.CountUp, countUp.Fields.Timer!.Direction);
            Assert.Equal(12_000, countUp.Fields.Timer.ValueMillisecondsAtReference);
            Assert.Equal(ProgressMode.Indeterminate, countUp.Fields.Progress!.Mode);
            Assert.Null(countUp.Fields.Progress.Value);
            Assert.Equal(CounterSemantics.CompletedCount, countUp.Fields.Counter!.Semantics);
            Assert.Equal(2, Entry(added, "ordinary").Items.Count);
            await ContinueAsync(directory, "added", token);

            using var updatedMarker = await MarkerAsync(directory, "updated", token);
            var updated = Snapshot(host, session, 2);
            AssertMarker(updatedMarker, servicePid, session, 2);
            var updatedIsland = Entry(updated, "island");
            Assert.Equal(3, updatedIsland.Activities.Count);
            Assert.Equal(3, updatedIsland.Items.Count);
            Assert.Equal(3, Entry(updated, "ordinary").Items.Count);
            var paused = updatedIsland.Items.Single(x => x.ItemId == "visible-a");
            Assert.True(paused.Fields.Timer!.IsPaused);
            Assert.True(paused.Fields.Timer.ShowOvertime);
            Assert.Equal(45_000, paused.Fields.Timer.ValueMillisecondsAtReference);
            Assert.Equal("暂停后调整", paused.Fields.Status!.Text);
            var zero = updatedIsland.Items.Single(x => x.ItemId == "visible-c");
            Assert.Equal(0, zero.Fields.Timer!.ValueMillisecondsAtReference);
            Assert.Equal(100, zero.Fields.Progress!.Value);
            Assert.False(updatedIsland.Activities.Single(x => x.ActivityId == "business-c").Ended);
            string frozenUpdatedJson = JsonSerializer.Serialize(updated.State);
            await ContinueAsync(directory, "updated", token);

            using var rejected = await MarkerAsync(directory, "rejected", token);
            AssertMarker(rejected, servicePid, session, 2);
            var codes = rejected.RootElement.GetProperty("rejections");
            Assert.Equal(10, codes.EnumerateObject().Count());
            Assert.Equal("dynamic_structure_invalid", codes.GetProperty("unknown-structure").GetString());
            Assert.Equal("dynamic_identity_invalid", codes.GetProperty("invalid-item-id").GetString());
            Assert.Equal("dynamic_duplicate_identity", codes.GetProperty("duplicate-item-id").GetString());
            Assert.Equal("dynamic_duplicate_identity", codes.GetProperty("duplicate-activity-id").GetString());
            Assert.Equal("dynamic_reference_invalid", codes.GetProperty("cross-activity-reference").GetString());
            Assert.Equal("dynamic_expiry_invalid", codes.GetProperty("expired-activity").GetString());
            Assert.Equal("dynamic_expiry_invalid", codes.GetProperty("excess-retention").GetString());
            Assert.Equal("dynamic_budget_exceeded", codes.GetProperty("item-budget").GetString());
            Assert.Equal("dynamic_budget_exceeded", codes.GetProperty("activity-budget").GetString());
            Assert.Equal("dynamic_fields_invalid", codes.GetProperty("invalid-fields").GetString());
            var unchanged = Snapshot(host, session, 2);
            Assert.Equal(frozenUpdatedJson, JsonSerializer.Serialize(unchanged.State));
            output.WriteLine("dynamicRejectionCheckpoint=" + rejected.RootElement.GetRawText());
            await ContinueAsync(directory, "rejected", token);

            using var removedMarker = await MarkerAsync(directory, "removed", token);
            AssertMarker(removedMarker, servicePid, session, 3);
            var removed = Snapshot(host, session, 3);
            Assert.Equal("business-a", Assert.Single(Entry(removed, "island").Activities).ActivityId);
            Assert.Equal("visible-a", Assert.Single(Entry(removed, "island").Items).ItemId);
            Assert.Equal("ring-a", Assert.Single(Entry(removed, "ordinary").Items).ItemId);
            await ContinueAsync(directory, "removed", token);

            using var complete = await MarkerAsync(directory, "complete", token);
            AssertMarker(complete, servicePid, session, 4);
            var final = Snapshot(host, session, 4);
            Assert.All(final.State!.DynamicEntries!, entry => { Assert.Empty(entry.Content.Activities); Assert.Empty(entry.Content.Items); });
            Assert.Equal(2, final.Declaration!.DynamicContents.Count);
            Assert.Equal(2, Entry(added, "island").Items.Count);
            Assert.Equal(90_000, Entry(added, "island").Items[0].Fields.Timer!.ValueMillisecondsAtReference);
            Assert.Equal(frozenUpdatedJson, JsonSerializer.Serialize(updated.State));
            Assert.All(initial.State.DynamicEntries!, entry => Assert.Empty(entry.Content.Items));
            Assert.Single(host.States.Snapshots);
            Assert.InRange(host.PeakPendingRequests, 1, ProtocolLimits.MaximumPendingRequests);
            Assert.Null(host.LastError);
            output.WriteLine("dynamicComplete=" + complete.RootElement.GetRawText());
            output.WriteLine($"finalRevision=4; sameSession=true; declarationsRetained=2; dynamicItems=0; activities=0; queuePeak={host.PeakPendingRequests}; frozenSnapshotsUnchanged=true");
        });
    }

    [Fact]
    public async Task An_invalid_preset_field_combination_is_rejected_by_the_real_sdk_broker_host_path()
    {
        await RunAsync("invalid-template", async (host, servicePid, directory, token) =>
        {
            using var complete = await MarkerAsync(directory, "complete", token);
            Assert.Equal(servicePid, complete.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal("dynamic_structure_invalid", complete.RootElement.GetProperty("result").GetString());
            var snapshot = host.States.GetSnapshot("dynamic")!;
            Assert.NotNull(snapshot);
            Assert.False(snapshot.IsInteractive);
            Assert.Null(snapshot.Declaration);
            Assert.Null(snapshot.State);
            output.WriteLine("invalidTemplateComplete=" + complete.RootElement.GetRawText());
        });
    }

    private async Task RunAsync(string mode, Func<HostBrokerSession, int, string, CancellationToken, Task> scenario)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-dynamic-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        HostBrokerSession? host = null;
        var processIds = new List<int>();
        try
        {
            string probe = Path.Combine(AppContext.BaseDirectory, "DynamicContentProcessProbe/Mtp.DynamicContent.ProcessProbe.dll");
            Assert.True(File.Exists(probe), "The dynamic process probe must be copied to test output.");
            host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["dynamic"], timeout.Token);
            processIds.Add(host.BrokerProcessId);
            int servicePid = await host.StartServiceAsync("dynamic", probe, timeout.Token, arguments: [mode, directory]);
            processIds.Add(servicePid);
            Assert.Equal(2, processIds.Distinct().Count());
            Assert.DoesNotContain(Environment.ProcessId, processIds);
            output.WriteLine($"mode={mode}; hostPid={Environment.ProcessId}; brokerPid={host.BrokerProcessId}; servicePid={servicePid}");
            await scenario(host, servicePid, directory, timeout.Token);
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

    private static BrokerApplicationSnapshot Snapshot(HostBrokerSession host, string session, long revision)
    {
        var snapshot = host.States.GetSnapshot("dynamic")!;
        Assert.Equal(session, snapshot.SessionId);
        Assert.Equal(revision, snapshot.State!.Revision);
        Assert.True(snapshot.IsInteractive);
        Assert.Equal(2, snapshot.State.DynamicEntries!.Count);
        return snapshot;
    }

    private static DynamicContentState Entry(BrokerApplicationSnapshot snapshot, string component) =>
        snapshot.State!.DynamicEntries!.Single(entry => entry.FeatureGroupId == "main" && entry.ComponentId == component).Content;

    private static void AssertMarker(JsonDocument marker, int pid, string session, long revision)
    {
        Assert.Equal(pid, marker.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(session, marker.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(revision, marker.RootElement.GetProperty("revision").GetInt64());
    }

    private static Task ContinueAsync(string directory, string phase, CancellationToken token) =>
        File.WriteAllTextAsync(Path.Combine(directory, "after-" + phase), "continue", token);

    private static async Task<JsonDocument> MarkerAsync(string directory, string marker, CancellationToken token)
    {
        string path = Path.Combine(directory, marker + ".json");
        while (!File.Exists(path))
        {
            string failed = Path.Combine(directory, "failed.json");
            if (File.Exists(failed)) Assert.Fail("Dynamic process probe failed: " + await File.ReadAllTextAsync(failed, token));
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

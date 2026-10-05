using Mtp.Contracts;
using Mtp.Host;

public sealed class PresetDemoProviderTests
{
    [Fact]
    public async Task Production_validator_accepts_all_published_modes_and_returned_collections_are_frozen()
    {
        var clock = new TestClock();
        var published = new List<ApplicationState>();
        var provider = new PresetDemoProvider("preset-app", clock, (state, _) =>
        { published.Add(state); return Task.FromResult(ProtocolResult.Success()); });
        var initial = await provider.GetSnapshotAsync(CancellationToken.None);
        var validated = new DeclarationValidator().Validate(initial.Declaration);
        Assert.True(validated.IsSuccess, validated.Error?.ToString());
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        var samples = new List<ApplicationState> { initial.State, Assert.Single(published) };
        long sequence = 0;
        foreach (string command in new[] { "forward", "backward", "mode", "mode", "semantics", "status", "status", "status", "end" })
        {
            var result = await provider.HandleAsync(Action(command, ++sequence), CancellationToken.None);
            Assert.True(result.Result.Accepted);
            samples.Add(result.State!);
        }
        var validator = new DynamicContentValidator();
        var declaration = Assert.Single(validated.Value!.DynamicContents);
        foreach (var sample in samples)
        {
            var result = validator.ValidateState(declaration, Entry(sample).Content, clock.GetUtcNow());
            Assert.True(result.IsSuccess, result.Error?.ToString());
        }
        Assert.Throws<NotSupportedException>(() => ((IList<DynamicItemState>)Entry(samples[1]).Content.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)Entry(samples[1]).Content.Items[0].ActivityIds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ItemStructureDeclaration>)initial.Declaration.FeatureGroups![0].Components![0].DynamicContent!.Structures).Clear());
    }

    [Fact]
    public async Task Reconnect_reconfirms_valid_data_without_rebasing_and_expired_business_remains_absent()
    {
        var clock = new TestClock();
        var provider = await Started(clock);
        var active = provider.Tick();
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Empty(Entry((await provider.GetSnapshotAsync(CancellationToken.None)).State).Content.Items);
        Assert.False((await provider.HandleAsync(Action("forward"), CancellationToken.None)).Result.Accepted);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.Equal(Entry(active).Content.Activities, Entry(provider.Tick()).Content.Activities);
        Assert.Equal(Item(active, "composite").Fields.Timer, Item(provider.Tick(), "composite").Fields.Timer);
        clock.Advance(TimeSpan.FromMinutes(6));
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.Empty(Entry(provider.Tick()).Content.Items);
        Assert.Empty(Entry(provider.Tick()).Content.Activities);
        Assert.False((await provider.HandleAsync(Action("mode", 2), CancellationToken.None)).Result.Accepted);
    }

    [Fact]
    public async Task Invalid_action_or_permission_input_keeps_last_accepted_snapshot()
    {
        var provider = await Started(new TestClock());
        var retained = provider.Tick();
        var request = Action("mode");
        foreach (var invalid in new[]
        {
            request with { Slot = request.Slot with { ApplicationId = "other" } },
            request with { Slot = request.Slot with { FeatureGroupId = "other" } },
            request with { Slot = request.Slot with { EntryId = "other" } },
            request with { Slot = request.Slot with { ActionSlotId = "unknown" } },
            request with { Parameter = new(ActionParameterKind.None, Text: "unaccepted") },
            request with { Sequence = 0 }, request with { RequestId = new string('x', 257) }
        })
        {
            Assert.False((await provider.HandleAsync(invalid, CancellationToken.None)).Result.Accepted);
            Assert.Same(retained, provider.Tick());
        }
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OnDisplayPermissionsChangedAsync(new(2,
            Enumerable.Repeat(new EntryDisplayPermission("main", "presets", true), 129).ToArray()), CancellationToken.None));
        Assert.Same(retained, provider.Tick());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.HandleAsync(request, cancelled.Token));
        Assert.Same(retained, provider.Tick());
    }

    [Fact]
    public async Task Noop_does_not_emit_a_state_or_increment_business_revision()
    {
        var provider = await Started(new TestClock());
        var retained = provider.Tick();
        var noop = await provider.HandleAsync(Action("backward"), CancellationToken.None);
        Assert.True(noop.Result.Accepted);
        Assert.Null(noop.State);
        Assert.Same(retained, provider.Tick());
    }

    [Fact]
    public async Task Permission_binding_wait_is_cancellable_and_late_old_completion_cannot_replace_new_state()
    {
        var unbound = new PresetDemoProvider("preset-app");
        await unbound.GetSnapshotAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = unbound.OnDisplayPermissionsChangedAsync(Permission(1, true), cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Empty(Entry(unbound.Tick()).Content.Items);
        Assert.Equal(0, unbound.Tick().Revision);

        var release = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new PresetDemoProvider("preset-app", publish: (_, _) => release.Task);
        await provider.GetSnapshotAsync(CancellationToken.None);
        var oldPublication = provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.False(oldPublication.IsCompleted);
        var replacement = await provider.GetSnapshotAsync(CancellationToken.None);
        release.SetResult(ProtocolResult.Success());
        await oldPublication;
        Assert.Same(replacement.State, provider.Tick());
        Assert.Empty(Entry(provider.Tick()).Content.Items);
    }

    [Fact]
    public async Task End_is_idempotent_and_permission_or_reconnection_cannot_revive_the_activity()
    {
        var provider = await Started(new TestClock());
        var end = await provider.HandleAsync(Action("end"), CancellationToken.None);
        Assert.True(end.Result.Accepted);
        Assert.Empty(Entry(end.State!).Content.Items);
        Assert.True(Assert.Single(Entry(end.State!).Content.Activities).Ended);
        var again = await provider.HandleAsync(Action("end", 2), CancellationToken.None);
        Assert.True(again.Result.Accepted);
        Assert.Null(again.State);
        Assert.Same(end.State, provider.Tick());
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.Empty(Entry(provider.Tick()).Content.Items);
        Assert.True(Assert.Single(Entry(provider.Tick()).Content.Activities).Ended);
        Assert.False((await provider.HandleAsync(Action("forward", 3), CancellationToken.None)).Result.Accepted);
    }

    [Fact]
    public async Task Counter_and_status_actions_change_fields_without_changing_structure_timer_or_expiry()
    {
        var provider = await Started(new TestClock());
        var initial = provider.Tick();
        var changed = await provider.HandleAsync(Action("semantics"), CancellationToken.None);
        Assert.True(changed.Result.Accepted);
        Assert.Equal(CounterSemantics.CompletedCount, Item(changed.State!, "counter-ring").Fields.Counter!.Semantics);
        Assert.Equal(CounterSemantics.CurrentIndex, Item(changed.State!, "counter-text").Fields.Counter!.Semantics);
        Assert.Equal(25, Item(changed.State!, "counter-ring").Fields.Progress!.Value);
        var attention = await provider.HandleAsync(Action("status", 2), CancellationToken.None);
        Assert.True(attention.Result.Accepted);
        Assert.Equal(StatusMarker.Attention, Item(attention.State!, "status").Fields.Status!.Marker);
        Assert.InRange(Item(attention.State!, "status").Fields.Status!.Text.Length, 40, 1024);
        var error = await provider.HandleAsync(Action("status", 3), CancellationToken.None);
        Assert.Equal(StatusMarker.Error, Item(error.State!, "status").Fields.Status!.Marker);
        var normal = await provider.HandleAsync(Action("status", 4), CancellationToken.None);
        Assert.Equal(new StatusReading("就绪"), Item(normal.State!, "status").Fields.Status);
        foreach (var snapshot in new[] { changed.State!, attention.State!, error.State!, normal.State! })
        {
            Assert.Equal(Entry(initial).Content.Items.Select(item => (item.ItemId, item.StructureId)), Entry(snapshot).Content.Items.Select(item => (item.ItemId, item.StructureId)));
            Assert.Equal(Item(initial, "composite").Fields.Timer, Item(snapshot, "composite").Fields.Timer);
            Assert.Equal(Entry(initial).Content.Activities, Entry(snapshot).Content.Activities);
        }
    }

    [Fact]
    public async Task Progress_actions_reverse_independent_graph_values_and_indeterminate_has_no_ratio()
    {
        var provider = await Started(new TestClock());
        var initial = provider.Tick();
        var ring = Item(initial, "counter-ring");
        Assert.Equal(CounterSemantics.CurrentIndex, ring.Fields.Counter!.Semantics);
        Assert.Equal(4, ring.Fields.Counter.Value);
        Assert.Equal(10, ring.Fields.Counter.Total);
        Assert.Equal(25, ring.Fields.Progress!.Value);
        var forward = await provider.HandleAsync(Action("forward"), CancellationToken.None);
        Assert.True(forward.Result.Accepted);
        Assert.Equal(75, Item(forward.State!, "progress").Fields.Progress!.Value);
        var reverse = await provider.HandleAsync(Action("backward", 2), CancellationToken.None);
        Assert.True(reverse.Result.Accepted);
        Assert.Equal(25, Item(reverse.State!, "counter-ring").Fields.Progress!.Value);
        var unknown = await provider.HandleAsync(Action("mode", 3), CancellationToken.None);
        Assert.True(unknown.Result.Accepted);
        Assert.All(Entry(unknown.State!).Content.Items.Where(item => item.Fields.Progress is not null), item =>
        {
            Assert.Equal(ProgressMode.Indeterminate, item.Fields.Progress!.Mode);
            Assert.Null(item.Fields.Progress.Value); Assert.Null(item.Fields.Progress.Maximum);
        });
        Assert.Equal(Item(initial, "composite").Fields.Timer, Item(unknown.State!, "composite").Fields.Timer);
        Assert.Equal(Entry(initial).Content.Activities, Entry(unknown.State!).Content.Activities);
        Assert.Equal(25, Item(initial, "counter-ring").Fields.Progress!.Value);
    }

    [Fact]
    public async Task Permission_creates_six_items_once_and_observation_never_publishes_or_renews()
    {
        var clock = new TestClock();
        var publications = new List<ApplicationState>();
        var provider = new PresetDemoProvider("preset-app", clock, (state, _) =>
        { publications.Add(state); return Task.FromResult(ProtocolResult.Success()); });
        var initial = await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.Empty(Entry(initial.State).Content.Items);
        Assert.Empty(Entry(initial.State).Content.Activities);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, false), CancellationToken.None);
        Assert.Empty(publications);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, true), CancellationToken.None);
        var current = Assert.Single(publications);
        Assert.Equal(6, Entry(current).Content.Items.Count);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), Assert.Single(Entry(current).Content.Activities).ExpiresAt);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Same(current, provider.Tick());
        await provider.OnDisplayPermissionsChangedAsync(Permission(3, true), CancellationToken.None);
        Assert.Single(publications);
        Assert.Same(current, provider.Tick());
    }

    private static DynamicEntryState Entry(ApplicationState state) => Assert.Single(state.DynamicEntries!);
    private static DynamicItemState Item(ApplicationState state, string id) => Entry(state).Content.Items.Single(item => item.ItemId == id);
    private static ActionInvocation Action(string command, long sequence = 1) => new("request-" + sequence, sequence,
        new("preset-app", "main", ActionEntryKind.Component, "controls", command), new());
    private static async Task<PresetDemoProvider> Started(TestClock clock)
    {
        var provider = new PresetDemoProvider("preset-app", clock, (_, _) => Task.FromResult(ProtocolResult.Success()));
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        return provider;
    }
    private static DisplayPermissionSnapshot Permission(long revision, bool allowed) => new(revision, [new("main", "presets", allowed)]);
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}

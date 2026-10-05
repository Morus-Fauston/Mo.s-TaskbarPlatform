using Mtp.Contracts;
using Mtp.Host;

public sealed class TimerDemoProviderTests
{
    [Fact]
    public async Task Full_sample_declaration_and_published_snapshots_pass_production_validation()
    {
        var clock = new TestClock();
        var published = new List<ApplicationState>();
        var provider = new TimerDemoProvider("custom-timer-application", clock, (state, _) =>
        { published.Add(state); return Task.FromResult(ProtocolResult.Success()); });
        var initial = await provider.GetSnapshotAsync(CancellationToken.None);
        var valid = new DeclarationValidator().Validate(initial.Declaration);
        Assert.True(valid.IsSuccess, valid.Error?.ToString());
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        var declaration = Assert.Single(valid.Value!.DynamicContents);
        foreach (var state in published.Prepend(initial.State))
        {
            var stateResult = new DynamicContentValidator().ValidateState(declaration, state.DynamicEntries![0].Content, clock.GetUtcNow());
            Assert.True(stateResult.IsSuccess, stateResult.Error?.ToString());
        }
        var retained = provider.Tick();
        Assert.Throws<NotSupportedException>(() => ((IList<DynamicItemState>)retained.DynamicEntries![0].Content.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)retained.DynamicEntries![0].Content.Items[0].ActivityIds).Clear());
    }

    [Fact]
    public async Task Reconnection_reconfirms_valid_business_without_rebasing_or_renewing()
    {
        var clock = new TestClock();
        var provider = await Started(clock);
        var initial = provider.Tick();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Empty((await provider.GetSnapshotAsync(CancellationToken.None)).State.DynamicEntries![0].Content.Items);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, false), CancellationToken.None);
        Assert.False((await provider.HandleAsync(Action("controls", "add"), CancellationToken.None)).Result.Accepted);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, true), CancellationToken.None);
        var restored = provider.Tick();
        Assert.Equal(initial.DynamicEntries![0].Content.Activities, restored.DynamicEntries![0].Content.Activities);
        Assert.Equal(initial.DynamicEntries[0].Content.Items.Select(item => item.Fields.Timer), restored.DynamicEntries[0].Content.Items.Select(item => item.Fields.Timer));
        await provider.OnDisplayPermissionsChangedAsync(Permission(3, false), CancellationToken.None);
        Assert.False((await provider.HandleAsync(Action("controls", "add", 2), CancellationToken.None)).Result.Accepted);
        Assert.True((await provider.HandleAsync(Action("timers", "toggle-up", 3), CancellationToken.None)).Result.Accepted);
        Assert.Equal(initial.DynamicEntries[0].Content.Activities, provider.Tick().DynamicEntries![0].Content.Activities);
    }

    [Fact]
    public async Task Countdown_stops_at_zero_or_preserves_explicit_overtime_without_ending_activities()
    {
        var clock = new TestClock();
        var provider = await Started(clock);
        var added = await provider.HandleAsync(Action("controls", "add"), CancellationToken.None);
        var repeat = added.State!.DynamicEntries![0].Content.Items[^1];
        clock.Advance(TimeSpan.FromSeconds(10));
        var stopped = await provider.HandleAsync(Action("timers", "toggle-down", 2), CancellationToken.None);
        Assert.Equal(0, Item(stopped.State!, "down").Fields.Timer!.ValueMillisecondsAtReference);
        var overtime = await provider.HandleAsync(Action("timers", "toggle-" + repeat.StructureId, 3), CancellationToken.None);
        Assert.Equal(-2000, Item(overtime.State!, repeat.ItemId).Fields.Timer!.ValueMillisecondsAtReference);
        Assert.All(overtime.State!.DynamicEntries![0].Content.Activities, activity => Assert.False(activity.Ended));
        Assert.Equal(added.State.DynamicEntries[0].Content.Activities, overtime.State.DynamicEntries[0].Content.Activities);
    }

    [Fact]
    public async Task Rejected_cross_application_or_malformed_action_preserves_last_snapshot()
    {
        var provider = await Started(new TestClock());
        var retained = provider.Tick();
        var valid = Action("controls", "add");
        foreach (var invalid in new[]
        {
            valid with { Slot = valid.Slot with { ApplicationId = "other" } },
            valid with { Slot = valid.Slot with { FeatureGroupId = "other" } },
            valid with { Parameter = new(ActionParameterKind.None, Number: 4) },
            valid with { Sequence = 0 }, valid with { RequestId = new string('x', 257) }
        })
        {
            Assert.False((await provider.HandleAsync(invalid, CancellationToken.None)).Result.Accepted);
            Assert.Same(retained, provider.Tick());
        }
    }

    [Fact]
    public async Task Permission_before_bind_is_cancellable_and_never_creates_background_business()
    {
        var provider = new TimerDemoProvider("timer-app");
        await provider.GetSnapshotAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = provider.OnDisplayPermissionsChangedAsync(Permission(1, true), cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, provider.Tick().Revision);
        Assert.Empty(provider.Tick().DynamicEntries![0].Content.Items);
    }

    [Fact]
    public async Task Late_old_publication_completion_does_not_replace_new_connection_empty_snapshot()
    {
        var release = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TimerDemoProvider("timer-app", publish: (_, _) => release.Task);
        await provider.GetSnapshotAsync(CancellationToken.None);
        var first = provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.False(first.IsCompleted);
        var replacement = await provider.GetSnapshotAsync(CancellationToken.None);
        release.SetResult(ProtocolResult.Success());
        await first;
        Assert.Same(replacement.State, provider.Tick());
        Assert.Empty(provider.Tick().DynamicEntries![0].Content.Items);
    }

    [Fact]
    public async Task Expired_business_is_not_revived_by_reconnect_but_explicit_permitted_reset_starts_new_bases()
    {
        var clock = new TestClock();
        var provider = await Started(clock);
        var initial = provider.Tick();
        clock.Advance(TimeSpan.FromMinutes(6));
        var reconnect = await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.Empty(reconnect.State.DynamicEntries![0].Content.Items);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.Empty(provider.Tick().DynamicEntries![0].Content.Items);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, false), CancellationToken.None);
        Assert.False((await provider.HandleAsync(Action("controls", "reset"), CancellationToken.None)).Result.Accepted);
        await provider.OnDisplayPermissionsChangedAsync(Permission(3, true), CancellationToken.None);
        var reset = await provider.HandleAsync(Action("controls", "reset", 2), CancellationToken.None);
        Assert.True(reset.Result.Accepted);
        Assert.Equal(2, reset.State!.DynamicEntries![0].Content.Items.Count);
        Assert.All(reset.State.DynamicEntries[0].Content.Items, item => Assert.Equal(clock.GetUtcNow(), item.Fields.Timer!.ReferenceUtc));
        Assert.All(reset.State.DynamicEntries[0].Content.Activities, activity => Assert.Equal(clock.GetUtcNow().AddMinutes(5), activity.ExpiresAt));
        Assert.Equal(5000, Item(reset.State, "down").Fields.Timer!.ValueMillisecondsAtReference);
        Assert.Equal(2, initial.DynamicEntries![0].Content.Items.Count);
    }

    [Fact]
    public async Task Repeated_items_are_bounded_and_reusing_a_control_slot_never_reuses_item_identity()
    {
        var provider = await Started(new TestClock());
        for (int index = 0; index < 8; index++)
            Assert.True((await provider.HandleAsync(Action("controls", "add", index + 1), CancellationToken.None)).Result.Accepted);
        var full = provider.Tick();
        Assert.Equal(10, full.DynamicEntries![0].Content.Items.Count);
        var rejected = await provider.HandleAsync(Action("controls", "add", 9), CancellationToken.None);
        Assert.False(rejected.Result.Accepted);
        Assert.Same(full, provider.Tick());
        var removedItem = full.DynamicEntries[0].Content.Items[^1];
        Assert.True((await provider.HandleAsync(Action("controls", "remove", 10), CancellationToken.None)).Result.Accepted);
        var replacement = await provider.HandleAsync(Action("controls", "add", 11), CancellationToken.None);
        Assert.True(replacement.Result.Accepted);
        var replacementItem = replacement.State!.DynamicEntries![0].Content.Items[^1];
        Assert.Equal(removedItem.StructureId, replacementItem.StructureId);
        Assert.NotEqual(removedItem.ItemId, replacementItem.ItemId);
        var toggled = await provider.HandleAsync(Action("timers", "toggle-" + replacementItem.StructureId, 12), CancellationToken.None);
        Assert.True(Item(toggled.State!, replacementItem.ItemId).Fields.Timer!.IsPaused);
        Assert.All(toggled.State!.DynamicEntries![0].Content.Items.Where(item => item.ItemId != replacementItem.ItemId), item => Assert.False(item.Fields.Timer!.IsPaused));
        Assert.Equal(10, full.DynamicEntries[0].Content.Items.Count);
        Assert.Equal(removedItem, full.DynamicEntries[0].Content.Items[^1]);
    }

    [Fact]
    public async Task Pause_and_resume_use_business_time_without_refresh_or_permission_renewal()
    {
        var clock = new TestClock();
        var provider = await Started(clock);
        var initial = provider.Tick();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Same(initial, provider.Tick());
        var paused = await provider.HandleAsync(Action("timers", "toggle-up"), CancellationToken.None);
        Assert.True(paused.Result.Accepted);
        var up = Item(paused.State!, "up").Fields.Timer!;
        Assert.True(up.IsPaused);
        Assert.Equal(2000, up.ValueMillisecondsAtReference);
        Assert.Equal(Item(initial, "down").Fields.Timer, Item(paused.State!, "down").Fields.Timer);
        Assert.Equal(initial.DynamicEntries![0].Content.Activities, paused.State!.DynamicEntries![0].Content.Activities);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Same(paused.State, provider.Tick());
        var resumed = await provider.HandleAsync(Action("timers", "toggle-up", 2), CancellationToken.None);
        Assert.True(resumed.Result.Accepted);
        Assert.False(Item(resumed.State!, "up").Fields.Timer!.IsPaused);
        Assert.Equal(2000, Item(resumed.State!, "up").Fields.Timer!.ValueMillisecondsAtReference);
        clock.Advance(TimeSpan.FromSeconds(1));
        var again = await provider.HandleAsync(Action("timers", "toggle-up", 3), CancellationToken.None);
        Assert.Equal(3000, Item(again.State!, "up").Fields.Timer!.ValueMillisecondsAtReference);
        Assert.Equal(0, Item(initial, "up").Fields.Timer!.ValueMillisecondsAtReference);
    }

    [Fact]
    public async Task Permission_creates_initial_business_once_and_duplicate_notifications_do_not_renew_it()
    {
        var clock = new TestClock();
        var publications = new List<ApplicationState>();
        var provider = new TimerDemoProvider("timer-app", clock, (state, _) =>
        { publications.Add(state); return Task.FromResult(ProtocolResult.Success()); });
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, false), CancellationToken.None);
        Assert.Empty(publications);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, true), CancellationToken.None);
        var initial = Assert.Single(publications);
        Assert.Equal(1, initial.Revision);
        Assert.Equal(new[] { "up", "down" }, initial.DynamicEntries![0].Content.Items.Select(item => item.ItemId));
        Assert.All(initial.DynamicEntries[0].Content.Activities, activity => Assert.Equal(clock.GetUtcNow().AddMinutes(5), activity.ExpiresAt));
        clock.Advance(TimeSpan.FromSeconds(3));
        await provider.OnDisplayPermissionsChangedAsync(Permission(3, true), CancellationToken.None);
        Assert.Single(publications);
    }

    [Fact]
    public async Task Initial_snapshot_contains_no_unpermitted_activity()
    {
        var provider = new TimerDemoProvider("timer-app");
        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(0, snapshot.State.Revision);
        var timers = Assert.Single(snapshot.State.DynamicEntries!);
        Assert.Empty(timers.Content.Activities);
        Assert.Empty(timers.Content.Items);
    }

    private static DisplayPermissionSnapshot Permission(long revision, bool allowed) => new(revision, [new("main", "timers", allowed)]);
    private static ActionInvocation Action(string entry, string slot, long sequence = 1) => new("request-" + sequence, sequence,
        new("timer-app", "main", ActionEntryKind.Component, entry, slot), new());
    private static DynamicItemState Item(ApplicationState state, string id) => state.DynamicEntries![0].Content.Items.Single(item => item.ItemId == id);
    private static async Task<TimerDemoProvider> Started(TestClock clock)
    {
        var provider = new TimerDemoProvider("timer-app", clock, (_, _) => Task.FromResult(ProtocolResult.Success()));
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        return provider;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        private long ticks;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) { now += elapsed; ticks += elapsed.Ticks; }
    }
}

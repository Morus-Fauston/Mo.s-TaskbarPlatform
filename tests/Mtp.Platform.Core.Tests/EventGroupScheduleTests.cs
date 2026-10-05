using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class EventGroupScheduleTests
{
    [Fact]
    public void Same_channel_refresh_preserves_generation_and_first_shown_order()
    {
        var schedule = new EventGroupSchedule();
        var request = new EventGroupRequest("app/group/entry/channel", "screen", "session");
        var initial = schedule.Prepare(request);
        Assert.True(initial.IsSuccess);
        Assert.Equal(EventGroupProposalKind.Create, initial.Value!.Kind);
        var identity = schedule.Commit(initial.Value).Value!;
        Assert.True(schedule.MarkVisible(identity));
        var shown = Assert.Single(schedule.Snapshot());
        var refresh = schedule.Prepare(request).Value!;
        Assert.Equal(EventGroupProposalKind.Refresh, refresh.Kind);
        Assert.Equal(identity, schedule.Commit(refresh).Value);
        Assert.Equal(shown.FirstShownOrder, Assert.Single(schedule.Snapshot()).FirstShownOrder);
    }

    [Fact]
    public void Protected_owner_is_retained_until_native_destruction_is_confirmed()
    {
        var clock = new ManualClock();
        var schedule = new EventGroupSchedule(1, clock);
        var first = Open(schedule, "first");
        Assert.True(schedule.SetInteraction(first, "pointer", true));
        Assert.Equal("AllProtected", schedule.Prepare(Request("second")).Error!.Code);
        Assert.True(schedule.SetInteraction(first, "pointer", false));
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.Equal("AllProtected", schedule.Prepare(Request("second")).Error!.Code);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var replacement = schedule.Prepare(Request("second")).Value!;
        Assert.Equal(first, replacement.Victim);
        Assert.Equal(EventGroupProposalKind.Replace, replacement.Kind);
        Assert.True(schedule.BeginEviction(replacement).IsSuccess);
        Assert.False(schedule.Commit(replacement).IsSuccess);
        Assert.Equal(first, Assert.Single(schedule.Snapshot()).Identity);
        Assert.True(schedule.ConfirmDestroyed(first));
        Assert.True(schedule.Commit(replacement).IsSuccess);
        Assert.False(schedule.ConfirmDestroyed(first));
        Assert.Equal(replacement.Identity, Assert.Single(schedule.Snapshot()).Identity);
    }

    private static EventGroupRequest Request(string key, string screen = "screen", string session = "session", bool persistent = false) =>
        new(key, screen, session, persistent);
    private static EventGroupIdentity Open(EventGroupSchedule schedule, string key, string screen = "screen", bool persistent = false)
    {
        var proposal = schedule.Prepare(Request(key, screen, persistent: persistent));
        Assert.True(proposal.IsSuccess, proposal.Error?.Message);
        var committed = schedule.Commit(proposal.Value!);
        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.True(schedule.MarkVisible(committed.Value!));
        return committed.Value!;
    }
    [Fact]
    public void Lowering_limit_preserves_protected_owners_and_blocks_new_channels_until_actual_convergence()
    {
        var clock = new ManualClock();
        var schedule = new EventGroupSchedule(3, clock);
        var first = Open(schedule, "first");
        var second = Open(schedule, "second");
        var third = Open(schedule, "third");
        Assert.True(schedule.SetInteraction(first, "focus", true));
        Assert.True(schedule.SetInteraction(second, "drag", true));
        Assert.True(schedule.SetLimit(1).IsSuccess);
        Assert.Equal(new[] { third }, schedule.ConvergenceCandidates("screen"));
        Assert.Equal("Converging", schedule.Prepare(Request("fourth")).Error!.Code);
        var refresh = schedule.Prepare(Request("first"));
        Assert.True(refresh.IsSuccess);
        Assert.True(schedule.Commit(refresh.Value!).IsSuccess);
        Assert.True(schedule.BeginClose(third));
        Assert.Empty(schedule.ConvergenceCandidates("screen"));
        Assert.Equal(3, schedule.Snapshot().Count);
        Assert.True(schedule.ConfirmDestroyed(third));
        Assert.True(schedule.SetInteraction(first, "focus", false));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { first }, schedule.ConvergenceCandidates("screen"));
        Assert.True(schedule.BeginClose(first));
        Assert.True(schedule.ConfirmDestroyed(first));
        Assert.Equal(second, Assert.Single(schedule.Snapshot()).Identity);
        Assert.Equal("AllProtected", schedule.Prepare(Request("fourth")).Error!.Code);
    }
    private sealed class ManualClock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now;
        internal void Advance(TimeSpan time) => now += time.Ticks;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Invalid_limit_is_rejected_without_changing_existing_owners(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventGroupSchedule(limit));
        var schedule = new EventGroupSchedule();
        var identity = Open(schedule, "one");
        Assert.False(schedule.SetLimit(limit).IsSuccess);
        Assert.Equal(5, schedule.Limit);
        Assert.Equal(identity, Assert.Single(schedule.Snapshot()).Identity);
    }

    [Fact]
    public void Identity_and_screen_budgets_are_checked_before_mutation()
    {
        var schedule = new EventGroupSchedule();
        foreach (var request in new[] { null, Request(""), Request(" key"), Request(new string('k', 2049)),
            Request("key", ""), Request("key", new string('s', 257)), Request("key", session: "bad ") })
            Assert.False(schedule.Prepare(request!).IsSuccess);
        Assert.Empty(schedule.Snapshot());
        for (int i = 0; i < 16; i++) Open(schedule, "channel" + i, "screen" + i);
        Assert.Equal("ScreenCapacityExceeded", schedule.Prepare(Request("one", "overflow")).Error!.Code);
        Assert.Equal(16, schedule.Snapshot().Count);
    }

    [Fact]
    public void Prepared_work_is_bounded_cancellable_and_not_an_unbounded_queue()
    {
        var schedule = new EventGroupSchedule();
        var first = schedule.Prepare(Request("first")).Value!;
        Assert.Equal("Busy", schedule.Prepare(Request("second")).Error!.Code);
        for (int i = 1; i < 16; i++) Assert.True(schedule.Prepare(Request("channel" + i, "screen" + i)).IsSuccess);
        Assert.Equal("ScreenCapacityExceeded", schedule.Prepare(Request("one", "overflow")).Error!.Code);
        Assert.True(schedule.Cancel(first));
        Assert.False(schedule.Cancel(first));
        Assert.False(schedule.Commit(first).IsSuccess);
        var next = schedule.Prepare(Request("second")).Value!;
        Assert.True(schedule.Commit(next).IsSuccess);
        Assert.Equal("second", Assert.Single(schedule.Snapshot()).Identity.Key);
    }

    [Fact]
    public void Late_refresh_and_old_identity_cannot_resurrect_a_closed_generation()
    {
        var schedule = new EventGroupSchedule();
        var old = Open(schedule, "one");
        var late = schedule.Prepare(Request("one")).Value!;
        Assert.True(schedule.ConfirmDestroyed(old));
        Assert.False(schedule.Commit(late).IsSuccess);
        var next = Open(schedule, "one");
        Assert.True(next.Generation > old.Generation);
        Assert.False(schedule.BeginClose(old));
        Assert.False(schedule.MarkVisible(old));
        Assert.False(schedule.SetInteraction(old, "pointer", true));
        Assert.False(schedule.ConfirmDestroyed(old));
        Assert.Equal(next, Assert.Single(schedule.Snapshot()).Identity);
    }

    [Fact]
    public void Session_replacement_requires_old_native_owner_to_be_destroyed_first()
    {
        var schedule = new EventGroupSchedule();
        var old = Open(schedule, "one");
        Assert.Equal("SessionCleanupRequired", schedule.Prepare(Request("one", session: "new-session")).Error!.Code);
        Assert.True(schedule.BeginClose(old));
        Assert.Equal("CleanupPending", schedule.Prepare(Request("one")).Error!.Code);
        Assert.True(schedule.ConfirmDestroyed(old));
        var next = schedule.Prepare(Request("one", session: "new-session")).Value!;
        Assert.True(schedule.Commit(next).IsSuccess);
        Assert.True(next.Identity.Generation > old.Generation);
        Assert.Equal("new-session", Assert.Single(schedule.Snapshot()).Identity.SessionId);
    }

    [Fact]
    public void Replacement_rechecks_new_interaction_and_refresh_does_not_make_old_group_young()
    {
        var schedule = new EventGroupSchedule(2);
        var first = Open(schedule, "first");
        Open(schedule, "second");
        Assert.True(schedule.Commit(schedule.Prepare(Request("first")).Value!).IsSuccess);
        var replacement = schedule.Prepare(Request("third")).Value!;
        Assert.Equal(first, replacement.Victim);
        Assert.True(schedule.SetInteraction(first, "focus", true));
        Assert.Equal("AllProtected", schedule.BeginEviction(replacement).Error!.Code);
        Assert.False(Assert.Single(schedule.Snapshot(), x => x.Identity == first).Closing);
        Assert.True(schedule.Cancel(replacement));
        Assert.Equal(2, schedule.Snapshot().Count);
    }

    [Fact]
    public void Failed_eviction_retains_owner_and_cannot_cascade_into_other_evictions()
    {
        var schedule = new EventGroupSchedule(2);
        var first = Open(schedule, "first");
        Open(schedule, "second");
        var replacement = schedule.Prepare(Request("third")).Value!;
        Assert.True(schedule.BeginEviction(replacement).IsSuccess);
        Assert.True(schedule.Cancel(replacement));
        Assert.Equal("CleanupPending", schedule.Prepare(Request("fourth")).Error!.Code);
        Assert.Equal(2, schedule.Snapshot().Count);
        Assert.Equal(first, Assert.Single(schedule.Snapshot(), x => x.Closing).Identity);
    }

    [Fact]
    public void Limit_change_invalidates_prepared_work_without_discarding_owners()
    {
        var schedule = new EventGroupSchedule(2);
        Open(schedule, "first");
        var pending = schedule.Prepare(Request("second")).Value!;
        Assert.True(schedule.SetLimit(1).IsSuccess);
        Assert.False(schedule.Commit(pending).IsSuccess);
        Assert.Single(schedule.Snapshot());
    }

    [Fact]
    public void Eight_second_lifetime_starts_at_actual_visibility_and_new_events_reset_it()
    {
        var clock = new ManualClock();
        var schedule = new EventGroupSchedule(clock: clock);
        var identity = schedule.Commit(schedule.Prepare(Request("one")).Value!).Value!;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(schedule.Expired());
        Assert.True(schedule.MarkVisible(identity));
        clock.Advance(TimeSpan.FromMilliseconds(7999));
        Assert.Empty(schedule.Expired());
        Assert.True(schedule.Commit(schedule.Prepare(Request("one")).Value!).IsSuccess);
        clock.Advance(TimeSpan.FromMilliseconds(7999));
        Assert.Empty(schedule.Expired());
        Assert.True(schedule.MarkVisible(identity)); // Layout/visibility checks do not refresh lifetime.
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(new[] { identity }, schedule.Expired());
        Assert.Single(schedule.Snapshot());
        Assert.True(schedule.BeginClose(identity));
        Assert.Empty(schedule.Expired());
        Assert.Single(schedule.Snapshot());
    }

    [Fact]
    public void Independent_interaction_sources_pause_timeout_but_persistence_does_not_prevent_eviction()
    {
        var clock = new ManualClock();
        var schedule = new EventGroupSchedule(2, clock);
        var normal = Open(schedule, "normal");
        var persistent = Open(schedule, "persistent", persistent: true);
        Assert.True(schedule.SetInteraction(normal, "pointer", true));
        Assert.True(schedule.SetInteraction(normal, "focus", true));
        Assert.True(schedule.SetInteraction(normal, "pointer", false));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(schedule.Expired());
        Assert.True(schedule.SetInteraction(normal, "focus", false));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(Assert.Single(schedule.Snapshot(), x => x.Identity == normal).Protected);
        Assert.Empty(schedule.Expired());
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { normal }, schedule.Expired());
        Assert.True(schedule.BeginClose(normal));
        Assert.True(schedule.ConfirmDestroyed(normal));
        Assert.True(schedule.SetLimit(1).IsSuccess);
        Assert.Equal(persistent, schedule.Prepare(Request("next")).Value!.Victim);
    }

    [Fact]
    public void Display_order_is_first_actual_visibility_not_creation_order()
    {
        var schedule = new EventGroupSchedule(2);
        var slow = schedule.Commit(schedule.Prepare(Request("slow")).Value!).Value!;
        var fast = Open(schedule, "fast");
        Assert.True(schedule.MarkVisible(slow));
        Assert.Equal(fast, schedule.Prepare(Request("next")).Value!.Victim);
    }

    [Fact]
    public void Same_channel_keeps_original_screen_until_closed_even_if_trigger_moves()
    {
        var schedule = new EventGroupSchedule();
        var first = Open(schedule, "channel", "left");
        var refresh = schedule.Prepare(Request("channel", "right")).Value!;
        Assert.Equal(EventGroupProposalKind.Refresh, refresh.Kind);
        Assert.Equal("left", refresh.Request.ScreenId);
        Assert.Equal(first, schedule.Commit(refresh).Value);
        Assert.Single(schedule.Snapshot());
        Assert.Equal("SessionCleanupRequired", schedule.Prepare(Request("channel", "right", "new")).Error!.Code);
        Assert.True(schedule.BeginClose(first));
        Assert.True(schedule.ConfirmDestroyed(first));
        var next = schedule.Prepare(Request("channel", "right")).Value!;
        Assert.Equal("right", schedule.Commit(next).Value!.ScreenId);
        Assert.True(next.Identity.Generation > first.Generation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Supported_capacity_selects_only_oldest_unprotected_and_never_removes_it_implicitly(int limit)
    {
        var schedule = new EventGroupSchedule(limit);
        var owners = Enumerable.Range(0, limit).Select(i => Open(schedule, "channel" + i)).ToArray();
        var proposed = schedule.Prepare(Request("extra")).Value!;
        Assert.Equal(owners[0], proposed.Victim);
        Assert.Equal(limit, schedule.Snapshot().Count);
        Assert.False(schedule.Commit(proposed).IsSuccess);
        Assert.True(schedule.Cancel(proposed));
        foreach (var owner in owners) Assert.True(schedule.SetInteraction(owner, "focus", true));
        Assert.Equal("AllProtected", schedule.Prepare(Request("extra")).Error!.Code);
        Assert.Equal(limit, schedule.Snapshot().Count);
    }

    [Fact]
    public void Refresh_cannot_change_a_live_groups_close_policy_or_extend_another_groups_lifetime()
    {
        var clock = new ManualClock();
        var schedule = new EventGroupSchedule(clock: clock);
        var first = Open(schedule, "first");
        Open(schedule, "second");
        Assert.Equal("EventPolicyChanged", schedule.Prepare(Request("first", persistent: true)).Error!.Code);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(schedule.Commit(schedule.Prepare(Request("second")).Value!).IsSuccess);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(new[] { first }, schedule.Expired());
    }
}

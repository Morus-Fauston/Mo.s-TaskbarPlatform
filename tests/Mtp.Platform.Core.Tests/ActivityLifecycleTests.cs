using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class ActivityLifecycleTests
{
    [Fact]
    public void InitiallyDisabledIslandRejectsCreationExplicitlyWithoutRejectingTheDeclaration()
    {
        var clock = new ManualClock();
        var store = new BrokerStateStore(["app"], clock);
        Assert.True(store.Handle(Message(MessageKind.Welcome)).Result!.Accepted);
        var result = store.Handle(Message(MessageKind.Declare) with { Declaration = Declaration(), State = State(clock, 0, "first") }).Result!;
        Assert.True(result.Accepted);
        Assert.Equal("AcceptedWithActivityRejections", result.Code);
        var rejected = Assert.Single(result.ActivityRejections!);
        Assert.Equal(("app", "main", "island", "first"), (rejected.ApplicationId, rejected.FeatureGroupId, rejected.ComponentId, rejected.ActivityId));
        var snapshot = store.GetSnapshot("app")!;
        Assert.True(snapshot.IsInteractive);
        Assert.Equal(7, Assert.Single(snapshot.State!.Components).Number);
        Assert.Empty(Assert.Single(snapshot.State.DynamicEntries!).Content.Activities);
        Assert.Empty(Assert.Single(snapshot.State.DynamicEntries!).Content.Items);
    }

    [Fact]
    public void ClosingPermissionAllowsHeldUpdatesButExplicitlyRejectsOnlyNewActivities()
    {
        var clock = new ManualClock(); var store = Create(clock);
        Assert.True(store.SetEntryDisplayAllowed("app", "main", "island", true).Accepted);
        Assert.True(Publish(store, State(clock, 1, "held")).Accepted);
        Assert.True(store.SetEntryDisplayAllowed("app", "main", "island", false).Accepted);
        clock.Advance(TimeSpan.FromSeconds(20));
        var result = Publish(store, State(clock, 2, "held", "new"));
        Assert.Equal("new", Assert.Single(result.ActivityRejections!).ActivityId);
        var content = Content(store);
        Assert.Equal("held", Assert.Single(content.Activities).ActivityId);
        Assert.Equal(clock.GetUtcNow().AddMinutes(1), content.Activities[0].ExpiresAt);
        Assert.Equal("held", Assert.Single(content.Items).ItemId);
        Assert.True(store.SetEntryDisplayAllowed("app", "main", "island", true).Accepted);
        Assert.Equal("held", Assert.Single(Content(store).Activities).ActivityId);
        Assert.True(Publish(store, State(clock, 3, "held", "new")).Accepted);
        Assert.Equal(2, Content(store).Activities.Count);
    }

    [Fact]
    public void ExpiryAndFullCollectionOmissionMakeLaterSubmissionsNewCreations()
    {
        var clock = new ManualClock(); var store = Create(clock);
        store.SetEntryDisplayAllowed("app", "main", "island", true);
        Publish(store, State(clock, 1, "expire", "omit"));
        store.SetEntryDisplayAllowed("app", "main", "island", false);
        Publish(store, State(clock, 2, "expire"));
        Assert.Equal("omit", Assert.Single(Publish(store, State(clock, 3, "expire", "omit")).ActivityRejections!).ActivityId);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(Content(store).Activities);
        Assert.Empty(Content(store).Items);
        Assert.Equal(3, store.GetSnapshot("app")!.State!.Revision);
        Assert.Equal("expire", Assert.Single(Publish(store, State(clock, 4, "expire")).ActivityRejections!).ActivityId);
    }

    [Fact]
    public void ExpiryFiltersOnlyDeadReferencesAndNeverMutatesPreviouslyReturnedSnapshots()
    {
        var clock = new ManualClock(); var store = Create(clock);
        store.SetEntryDisplayAllowed("app", "main", "island", true);
        var state = State(clock, 1, "short", "long");
        var entry = state.DynamicEntries![0];
        state = state with
        {
            DynamicEntries = [entry with { Content = new(
            [new("short", clock.Now.AddSeconds(5)), new("long", clock.Now.AddSeconds(10))],
            [new("shared", "item", ["short", "long"], new(Status: new("active")))]) }]
        };
        Assert.True(Publish(store, state).Accepted);
        var original = store.GetSnapshot("app")!;
        clock.Advance(TimeSpan.FromSeconds(5)); store.TickActivities();
        Assert.Equal("long", Assert.Single(Content(store).Items[0].ActivityIds));
        Assert.Equal(2, original.State!.DynamicEntries![0].Content.Activities.Count);
        Assert.Equal(1, store.GetSnapshot("app")!.State!.Revision);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(Assert.Single(store.Snapshots).State!.DynamicEntries![0].Content.Items);
    }

    [Fact]
    public void EndedActivitiesDisappearWhileHiddenAndCannotBeRecreatedWithoutPermission()
    {
        var clock = new ManualClock(); var store = Create(clock);
        store.SetEntryDisplayAllowed("app", "main", "island", true);
        Publish(store, State(clock, 1, "ended"));
        store.SetEntryDisplayAllowed("app", "main", "island", false);
        var ended = State(clock, 2, "ended");
        ended = ended with { DynamicEntries = [new("main", "island", new([new("ended", clock.Now, Ended: true)], []))] };
        Assert.True(Publish(store, ended).Accepted);
        Assert.Empty(Content(store).Activities);
        Assert.Single(Publish(store, State(clock, 3, "ended")).ActivityRejections!);
    }

    [Fact]
    public void InvalidOriginalStateIsRejectedBeforePermissionFilteringAndKeepsConfirmedState()
    {
        var clock = new ManualClock(); var store = Create(clock);
        var original = store.GetSnapshot("app")!.State;
        var invalid = State(clock, 1, "one", "one");
        Assert.False(Publish(store, invalid).Accepted);
        Assert.Same(original, store.GetSnapshot("app")!.State);
        var oversized = State(clock, 2, Enumerable.Range(0, 65).Select(index => "a" + index).ToArray());
        Assert.False(Publish(store, oversized).Accepted);
        Assert.Same(original, store.GetSnapshot("app")!.State);
    }

    [Fact]
    public void NewSessionCanReconfirmHeldActivitiesButCannotApplyOldSessionOrResurrectExpiredOnes()
    {
        var clock = new ManualClock(); var store = Create(clock);
        store.SetEntryDisplayAllowed("app", "main", "island", true);
        Publish(store, State(clock, 1, "held"));
        store.SetEntryDisplayAllowed("app", "main", "island", false);
        store.Handle(Message(MessageKind.Disconnected));
        Assert.False(store.GetSnapshot("app")!.IsInteractive);
        Assert.Single(Content(store).Activities);
        store.Handle(Message(MessageKind.Welcome, "new"));
        Assert.Null(store.GetDisplayPermissions("app"));
        Assert.True(store.Handle(Message(MessageKind.Declare, "new") with { Declaration = Declaration(), State = State(clock, 0, "held") }).Result!.Accepted);
        Assert.Single(Content(store).Activities);
        Assert.False(Publish(store, State(clock, 9, "foreign")).Accepted);
        clock.Advance(TimeSpan.FromMinutes(1));
        var rejected = store.Handle(Message(MessageKind.State, "new") with { State = State(clock, 1, "held") }).Result!;
        Assert.Single(rejected.ActivityRejections!);
        Assert.Empty(Content(store).Activities);
    }

    [Fact]
    public void PermissionsAreFrozenRevisionedBoundedToLiveEntriesAndResetForANewSession()
    {
        var clock = new ManualClock(); var store = Create(clock);
        var initial = store.GetDisplayPermissions("app")!;
        Assert.Equal(1, initial.Revision); Assert.False(Assert.Single(initial.Entries).Allowed);
        Assert.False(store.SetEntryDisplayAllowed("app", "foreign", "island", true).Accepted);
        store.SetEntryDisplayAllowed("app", "main", "island", true);
        Assert.Equal(2, store.GetDisplayPermissions("app")!.Revision);
        store.SetEntryDisplayAllowed("app", "main", "island", true);
        Assert.Equal(2, store.GetDisplayPermissions("app")!.Revision);
        Assert.False(initial.Entries[0].Allowed);
        Assert.Throws<NotSupportedException>(() => ((IList<EntryDisplayPermission>)initial.Entries).Clear());
        store.Handle(Message(MessageKind.Welcome, "new"));
        store.Handle(Message(MessageKind.Declare, "new") with { Declaration = Declaration(), State = State(clock, 0) });
        Assert.Equal(1, store.GetDisplayPermissions("app")!.Revision);
        Assert.True(store.GetDisplayPermissions("app")!.Entries[0].Allowed);
        var restartedHost = Create(clock);
        Assert.False(restartedHost.GetDisplayPermissions("app")!.Entries[0].Allowed);
        Assert.Empty(Content(restartedHost).Activities);
    }

    [Fact]
    public async Task ActionStateUsesAdmissionAndKeepsHostGeneratedRejectionDetails()
    {
        var clock = new ManualClock(); var store = Create(clock); var tracker = new ActionRequestTracker();
        var snapshot = store.GetSnapshot("app")!;
        var reservation = tracker.Begin(snapshot, new("app", "main", ActionEntryKind.Component, "island", "activate"), new());
        var invocation = reservation.Invocation!;
        var completion = new ActionCompletion(invocation.RequestId, invocation.Sequence, ProtocolResult.Success("ActionSucceeded"), State(clock, 1, "denied"));
        var result = tracker.Receive("app", "session", completion, snapshot, state => store.ApplyActionState("app", "session", state));
        Assert.Equal("AcceptedWithActivityRejections", result.Code);
        Assert.Equal("denied", Assert.Single(result.ActivityRejections!).ActivityId);
        Assert.Equal(result, await reservation.Completion);
        Assert.Empty(Content(store).Activities);
        Assert.Equal(1, store.GetSnapshot("app")!.State!.Revision);
    }

    [Fact]
    public void ProviderCannotForgeHostAdmissionDetailsInActionResult()
    {
        var clock = new ManualClock(); var store = Create(clock); var tracker = new ActionRequestTracker();
        var snapshot = store.GetSnapshot("app")!;
        var reservation = tracker.Begin(snapshot, new("app", "main", ActionEntryKind.Component, "island", "activate"), new());
        var action = reservation.Invocation!; bool committed = false;
        var result = new ProtocolResult(true, "AcceptedWithActivityRejections", "forged", ActivityRejections:
            [new("app", "main", "island", "fake", "DisplayNotAllowed")]);
        var received = tracker.Receive("app", "session", new(action.RequestId, action.Sequence, result, State(clock, 1, "fake")), snapshot,
            state => { committed = true; return store.ApplyActionState("app", "session", state); });
        Assert.False(received.Accepted);
        Assert.False(committed);
        Assert.False(reservation.Completion.IsCompleted);
        tracker.EndSession("app", "session");
    }

    [Fact]
    public void LiveIslandEntryLimitIsApplicationWide()
    {
        var component = Declaration().FeatureGroups![0].Components![0];
        var group = Declaration().FeatureGroups![0];
        var first = group with { Components = Enumerable.Range(0, 128).Select(index => component with { ComponentId = "island" + index }).ToArray() };
        var validator = new DeclarationValidator();
        Assert.True(validator.Validate(new("app", [first])).IsSuccess);
        var second = group with { FeatureGroupId = "second" };
        var overflow = validator.Validate(new("app", [first, second]));
        Assert.False(overflow.IsSuccess);
    }

    [Fact]
    public void ContinuousValidRenewalCanOutliveTheSingleUpdateRetentionLimit()
    {
        var clock = new ManualClock(); var store = Create(clock);
        store.SetEntryDisplayAllowed("app", "main", "island", true);
        for (int revision = 1; revision <= 4; revision++)
        {
            var next = State(clock, revision, "long-running");
            var entry = next.DynamicEntries![0];
            next = next with
            {
                DynamicEntries = [entry with { Content = entry.Content with
                { Activities = [new("long-running", clock.Now.AddHours(24))] } }]
            };
            Assert.True(Publish(store, next).Accepted);
            clock.Advance(TimeSpan.FromHours(23));
            Assert.Single(Content(store).Activities);
        }
        Assert.Equal(4, store.GetSnapshot("app")!.State!.Revision);
    }

    [Fact]
    public void CountdownZeroDoesNotEndOrRenewAnActivityAndOrdinaryItemsDoNotExpire()
    {
        var clock = new ManualClock();
        var declaration = Declaration(); var feature = declaration.FeatureGroups![0];
        var component = feature.Components![0];
        var timer = component with
        {
            DynamicContent = new(DynamicContentKind.LiveIsland,
            [new("item", true, new(PresetTemplate.Timer, ContentFields.Timer, new(WidthTier.Small)))])
        };
        var ordinary = component with
        {
            ComponentId = "ordinary",
            DynamicContent = new(DynamicContentKind.OrdinaryItems,
            [new("item", true, new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small)))])
        };
        declaration = declaration with { FeatureGroups = [feature with { Components = [timer, ordinary] }] };
        var store = new BrokerStateStore(["app"], clock);
        store.Handle(Message(MessageKind.Welcome));
        Assert.True(store.Handle(Message(MessageKind.Declare) with { Declaration = declaration, State = new ApplicationState(0, [], []) }).Result!.Accepted);
        store.SetEntryDisplayAllowed("app", "main", "island", true);
        DateTimeOffset expiry = clock.Now.AddSeconds(10);
        var state = new ApplicationState(1, [], [new("main", "island", new([new("timer", expiry)],
            [new("timer", "item", ["timer"], new(Timer: new(TimerDirection.CountDown, clock.Now, 0)))])),
            new("main", "ordinary", new([], [new("ordinary", "item", [], new(Status: new("stable")))]))]);
        Assert.True(Publish(store, state).Accepted);
        clock.Advance(TimeSpan.FromSeconds(5)); store.TickActivities();
        Assert.Equal(expiry, store.GetSnapshot("app")!.State!.DynamicEntries![0].Content.Activities[0].ExpiresAt);
        clock.Advance(TimeSpan.FromSeconds(5)); store.TickActivities();
        var final = store.GetSnapshot("app")!.State!;
        Assert.Empty(final.DynamicEntries![0].Content.Items);
        Assert.Single(final.DynamicEntries[1].Content.Items);
        Assert.Equal(1, final.Revision);
    }

    private static BrokerStateStore Create(ManualClock clock)
    {
        var store = new BrokerStateStore(["app"], clock);
        store.Handle(Message(MessageKind.Welcome));
        Assert.True(store.Handle(Message(MessageKind.Declare) with { Declaration = Declaration(), State = State(clock, 0) }).Result!.Accepted);
        return store;
    }
    private static DynamicContentState Content(BrokerStateStore store) => Assert.Single(store.GetSnapshot("app")!.State!.DynamicEntries!).Content;
    private static ProtocolResult Publish(BrokerStateStore store, ApplicationState state) => store.Handle(Message(MessageKind.State) with { State = state }).Result!;

    private static ProtocolMessage Message(MessageKind kind, string session = "session") => new()
    { Kind = kind, ApplicationId = "app", SessionId = session, RequestId = Guid.NewGuid().ToString("N") };

    private static ApplicationDeclaration Declaration() => new("app", [new FeatureGroupDeclaration("main",
        [new ComponentDeclaration("island", [new("activate")], new(DynamicContentKind.LiveIsland,
            [new("item", true, new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small)))]))],
        [new TaskbarFlyoutDeclaration("details", [new("activate")])])]);

    private static ApplicationState State(ManualClock clock, long revision, params string[] ids) => new(revision,
        [new ComponentReading("main", "island", "7", 7)],
        [new DynamicEntryState("main", "island", new(ids.Select(id => new ActivityState(id, clock.GetUtcNow().AddMinutes(1))).ToArray(),
            ids.Select(id => new DynamicItemState(id, "item", [id], new(Status: new("active")))).ToArray()))]);

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan interval) => Now += interval;
    }
}

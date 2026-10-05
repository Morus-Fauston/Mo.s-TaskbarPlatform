using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class ActionRequestTrackerTests
{
    [Fact]
    public async Task ActionUsesConfirmedSlotAndPublishesStateBeforeCompletingWaiter()
    {
        var store = Ready("one");
        var tracker = new ActionRequestTracker();
        var reservation = tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        Assert.True(reservation.Accepted);
        Assert.False(reservation.Completion.IsCompleted);
        Assert.Equal(1, reservation.Invocation!.Sequence);
        Assert.Equal(1, tracker.BusyCount);

        var response = tracker.Receive("one", "session", new(reservation.Invocation.RequestId, 1,
            ProtocolResult.Success(), State(1, "confirmed")), store.GetSnapshot("one"), state => Commit(store, "one", state));

        Assert.True(response.Accepted);
        Assert.True((await reservation.Completion).Accepted);
        Assert.Equal("confirmed", store.GetSnapshot("one")!.State!.Components[0].Text);
        Assert.Equal(0, tracker.OutstandingCount);
        Assert.Equal(0, tracker.BusyCount);
    }

    [Fact]
    public async Task TimeoutReleasesBusyAtFiveSecondsButLateSuccessStillCommitsWithoutAnotherHint()
    {
        var store = Ready("one");
        var clock = new ManualClock();
        var tracker = new ActionRequestTracker(clock);
        var reserved = tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1),
            new(ActionParameterKind.Number, Number: 8));
        Assert.Equal("initial", store.GetSnapshot("one")!.State!.Components[0].Text);
        Assert.Equal(8, tracker.GetPending("one")[0].Preview!.Number);
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        tracker.Tick();
        Assert.Equal(1, tracker.BusyCount);
        clock.Advance(TimeSpan.FromTicks(1));
        tracker.Tick();
        Assert.True(reserved.Completion.IsCompleted);
        Assert.Equal("ActionTimedOut", (await reserved.Completion).Code);
        Assert.Equal(0, tracker.BusyCount);
        Assert.Equal(1, tracker.OutstandingCount);
        var hint = tracker.GetLastErrorHint("one");
        Assert.NotNull(hint);
        tracker.Tick();
        Assert.Same(hint, tracker.GetLastErrorHint("one"));
        Assert.True(tracker.Receive("one", "session", new(reserved.Invocation!.RequestId, reserved.Invocation.Sequence,
            ProtocolResult.Success(), State(1, "late")), store.GetSnapshot("one"), state => Commit(store, "one", state)).Accepted);
        Assert.Equal("late", store.GetSnapshot("one")!.State!.Components[0].Text);
        Assert.Equal("ActionTimedOut", (await reserved.Completion).Code);
        Assert.Same(hint, tracker.GetLastErrorHint("one"));
        Assert.Equal(0, tracker.OutstandingCount);
    }

    [Fact]
    public async Task PerApplicationCapacityIncludesCancelledAndTimedOutRequestsWithoutStarvingAnotherApplication()
    {
        var store = Ready("one", "two");
        var clock = new ManualClock();
        var tracker = new ActionRequestTracker(clock);
        var pending = Enumerable.Range(0, 4).Select(_ => tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1))).ToArray();
        Assert.All(pending, item => Assert.True(item.Accepted));
        Assert.True(tracker.Cancel("one", "session", pending[0].Invocation!.RequestId));
        Assert.Equal("ActionCancelled", (await pending[0].Completion).Code);
        clock.Advance(TimeSpan.FromSeconds(5));
        tracker.Tick();
        Assert.Equal(0, tracker.BusyCount);
        Assert.Equal(4, tracker.OutstandingCount);
        Assert.Equal("Busy", tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1)).Result.Code);
        Assert.True(tracker.Begin(store.GetSnapshot("two")!, Slot("two"), new(ActionParameterKind.Number, Number: 1)).Accepted);
        tracker.FailDispatch("one", "session", pending[0].Invocation!.RequestId, ProtocolResult.Reject("NotSent", "未发送"));
        Assert.True(tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1)).Accepted);
    }

    [Fact]
    public void UnknownCrossApplicationAndWrongTypedParametersAreNotAvailable()
    {
        var store = Ready("one", "two");
        var tracker = new ActionRequestTracker();
        Assert.Equal("ActionNotAvailable", tracker.Begin(store.GetSnapshot("one")!, Slot("two"), new(ActionParameterKind.Number, Number: 1)).Result.Code);
        Assert.Equal("ActionNotAvailable", tracker.Begin(store.GetSnapshot("one")!, Slot("one") with { ActionSlotId = "unknown" }, new(ActionParameterKind.Number, Number: 1)).Result.Code);
        foreach (var parameter in new[] { new ActionParameter(), new(ActionParameterKind.Number, Number: double.NaN),
            new(ActionParameterKind.Number, Number: 1, Text: "extra"), new(ActionParameterKind.Text, Text: new string('x', 1025)) })
            Assert.Equal("ActionNotAvailable", tracker.Begin(store.GetSnapshot("one")!, Slot("one"), parameter).Result.Code);
        Assert.Equal(0, tracker.OutstandingCount);
    }

    [Fact]
    public async Task FailureOrRejectedSuccessStatePreservesConfirmedValueAndCreatesOnlyOneErrorHint()
    {
        var store = Ready("one");
        var tracker = new ActionRequestTracker();
        var first = tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        var failure = new ActionCompletion(first.Invocation!.RequestId, first.Invocation.Sequence, ProtocolResult.Reject("Rejected", "业务失败"), State(1, "must ignore"));
        Assert.False(tracker.Receive("one", "session", failure, store.GetSnapshot("one"), _ => throw new InvalidOperationException("Failure must not commit")).Accepted);
        Assert.Equal("Rejected", (await first.Completion).Code);
        var hint = tracker.GetLastErrorHint("one");
        Assert.NotNull(hint);
        Assert.False(tracker.Receive("one", "session", failure, store.GetSnapshot("one"), _ => throw new InvalidOperationException("Duplicate must not commit")).Accepted);
        Assert.Same(hint, tracker.GetLastErrorHint("one"));

        var second = tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        var invalidState = new ApplicationState(1, [new("main", "foreign", "invalid")]);
        var result = tracker.Receive("one", "session", new(second.Invocation!.RequestId, second.Invocation.Sequence,
            ProtocolResult.Success(), invalidState), store.GetSnapshot("one"), state => Commit(store, "one", state));
        Assert.False(result.Accepted);
        Assert.Equal(result.Code, (await second.Completion).Code);
        Assert.Equal("initial", store.GetSnapshot("one")!.State!.Components[0].Text);
        Assert.Equal(0, tracker.OutstandingCount);
        Assert.NotSame(hint, tracker.GetLastErrorHint("one"));
    }

    [Fact]
    public void MalformedOrWrongSequenceCompletionCannotCommitAndDoesNotConsumeMatchingReservation()
    {
        var store = Ready("one");
        var tracker = new ActionRequestTracker();
        var reservation = tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        var completion = new ActionCompletion(reservation.Invocation!.RequestId, reservation.Invocation.Sequence, ProtocolResult.Success(), State(1));
        var malformed = new[]
        {
            completion with { Sequence = 999 }, completion with { Result = new(true, new string('c', 257), "bad") },
            completion with { Result = new(true, "code", new string('m', 1025)) },
            completion with { Result = new(true, "code", "bad", new string('p', 1025)) }, completion with { Result = null! }
        };
        foreach (var result in malformed)
            Assert.False(tracker.Receive("one", "session", result, store.GetSnapshot("one"), _ => throw new InvalidOperationException("Must not commit")).Accepted);
        Assert.False(reservation.Completion.IsCompleted);
        Assert.Equal(1, tracker.OutstandingCount);
    }

    [Fact]
    public async Task NewSessionTerminatesOldReservationsAndLateCleanupCannotCancelNewRequest()
    {
        var oldStore = Ready("one");
        var tracker = new ActionRequestTracker();
        var old = tracker.Begin(oldStore.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        var next = oldStore.GetSnapshot("one")! with { SessionId = "new-session" };
        var fresh = tracker.Begin(next, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        Assert.True(old.Completion.IsCompleted);
        Assert.Equal("ActionCancelled", (await old.Completion).Code);
        tracker.EndSession("one", "session");
        Assert.False(fresh.Completion.IsCompleted);
        Assert.False(tracker.Receive("one", "session", new(old.Invocation!.RequestId, old.Invocation.Sequence, ProtocolResult.Success(), State(1)),
            next, _ => throw new InvalidOperationException("Old session must not commit")).Accepted);
        tracker.Shutdown(); tracker.Shutdown();
        Assert.True(fresh.Completion.IsCompleted);
        Assert.Equal(0, tracker.OutstandingCount);
        Assert.False(tracker.Begin(next, Slot("one"), new(ActionParameterKind.Number, Number: 1)).Accepted);
    }

    [Fact]
    public void StaleCallerSnapshotCannotAuthorizeACompletionAgainstCurrentSession()
    {
        var store = Ready("one");
        var tracker = new ActionRequestTracker();
        var pending = tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        var completion = new ActionCompletion(pending.Invocation!.RequestId, pending.Invocation.Sequence, ProtocolResult.Success(), State(1));
        Assert.False(tracker.Receive("one", "session", completion, store.GetSnapshot("one")! with { SessionId = "new-session" },
            _ => throw new InvalidOperationException("Wrong current session must not commit")).Accepted);
        Assert.Equal(1, tracker.OutstandingCount);
    }

    [Fact]
    public void GlobalCapacityIsSixtyFourAndPublicPendingSnapshotsAreReadOnly()
    {
        var names = Enumerable.Range(0, 16).Select(index => "app" + index).ToArray();
        var store = Ready(names);
        var tracker = new ActionRequestTracker();
        foreach (var app in names)
            for (var i = 0; i < 4; i++)
                Assert.True(tracker.Begin(store.GetSnapshot(app)!, Slot(app), new(ActionParameterKind.Number, Number: 1)).Accepted);
        Assert.Equal(64, tracker.OutstandingCount);
        Assert.Equal(64, tracker.BusyCount);
        Assert.Equal("Busy", tracker.Begin(store.GetSnapshot(names[0])!, Slot(names[0]), new(ActionParameterKind.Number, Number: 1)).Result.Code);
        Assert.Throws<NotSupportedException>(() => ((IList<PendingActionSnapshot>)tracker.GetPending(names[0])).Clear());
        tracker.EndSession(names[0], "session");
        Assert.Equal(60, tracker.OutstandingCount);
        tracker.Shutdown();
        Assert.Equal(0, tracker.OutstandingCount);
    }

    [Fact]
    public async Task DispatchFailureReleasesReservationAndSanitizesOversizedReason()
    {
        var store = Ready("one");
        var tracker = new ActionRequestTracker();
        var reserved = tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        tracker.FailDispatch("one", "session", reserved.Invocation!.RequestId, ProtocolResult.Reject("bad", new string('m', 1025)));
        Assert.Equal("ActionDispatchFailed", (await reserved.Completion).Code);
        Assert.Equal(0, tracker.OutstandingCount);
        var hint = tracker.GetLastErrorHint("one");
        tracker.FailDispatch("one", "session", reserved.Invocation.RequestId, ProtocolResult.Reject("bad", "重复"));
        Assert.Same(hint, tracker.GetLastErrorHint("one"));
    }

    [Fact]
    public async Task CancelledWaitCanAcceptOneSuccessWithoutShowingAnErrorHint()
    {
        var store = Ready("one");
        var tracker = new ActionRequestTracker();
        var reservation = tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        tracker.Cancel("one", "session", reservation.Invocation!.RequestId);
        var completion = new ActionCompletion(reservation.Invocation.RequestId, reservation.Invocation.Sequence, ProtocolResult.Success(), State(1, "after cancel"));
        Assert.True(tracker.Receive("one", "session", completion, store.GetSnapshot("one"), state => Commit(store, "one", state)).Accepted);
        Assert.Equal("ActionCancelled", (await reservation.Completion).Code);
        Assert.Null(tracker.GetLastErrorHint("one"));
        Assert.False(tracker.Receive("one", "session", completion, store.GetSnapshot("one"), _ => throw new InvalidOperationException()).Accepted);
        Assert.Equal("after cancel", store.GetSnapshot("one")!.State!.Components[0].Text);
    }

    [Fact]
    public async Task CommitFailureIsContainedAndDoesNotBlockOtherApplications()
    {
        var store = Ready("one", "two");
        var tracker = new ActionRequestTracker();
        var reserved = tracker.Begin(store.GetSnapshot("one")!, Slot("one"), new(ActionParameterKind.Number, Number: 1));
        var result = tracker.Receive("one", "session", new(reserved.Invocation!.RequestId, reserved.Invocation.Sequence, ProtocolResult.Success(), State(1)),
            store.GetSnapshot("one"), _ => throw new InvalidOperationException("private details"));
        Assert.Equal("ActionStateRejected", result.Code);
        Assert.DoesNotContain("private details", result.Message);
        Assert.Equal("ActionStateRejected", (await reserved.Completion).Code);
        Assert.True(tracker.Begin(store.GetSnapshot("two")!, Slot("two"), new(ActionParameterKind.Number, Number: 1)).Accepted);
    }

    private static ActionSlotReference Slot(string app) => new(app, "main", ActionEntryKind.Component, "counter", "increment");
    private static ApplicationState State(long revision, string text = "initial") => new(revision, [new("main", "counter", text)]);
    private static ProtocolResult Commit(BrokerStateStore store, string app, ApplicationState state, string session = "session") =>
        store.Handle(new() { ApplicationId = app, SessionId = session, Kind = MessageKind.State, State = state }).Result!;

    private static BrokerStateStore Ready(params string[] apps)
    {
        var store = new BrokerStateStore(apps);
        foreach (var app in apps)
        {
            store.Handle(new() { ApplicationId = app, SessionId = "session", Kind = MessageKind.Welcome });
            var result = store.Handle(new()
            {
                ApplicationId = app,
                SessionId = "session",
                Kind = MessageKind.Declare,
                Declaration = new(app, [new("main",
                    [new("counter", [new("increment", ActionParameterKind.Number)])],
                    [new("panel", [new("open")])])]),
                State = State(0)
            });
            Assert.True(result.Result!.Accepted);
        }
        return store;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan delta) => ticks += delta.Ticks;
    }
}

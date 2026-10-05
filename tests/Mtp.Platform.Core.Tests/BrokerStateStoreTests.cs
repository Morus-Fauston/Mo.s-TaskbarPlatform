using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class BrokerStateStoreTests
{
    [Fact]
    public void RegisteredSessionPublishesValidatedDeclarationAndInitialReadingTogether()
    {
        var store = new BrokerStateStore(["counter"]);
        Assert.True(store.Handle(Message(MessageKind.Welcome)).Result!.Accepted);
        Assert.Null(store.GetSnapshot("counter")!.State);

        var result = store.Handle(Message(MessageKind.Declare) with
        {
            Declaration = Declaration(),
            State = State(0, "42")
        });

        Assert.True(result.Result!.Accepted);
        Assert.Equal(MessageKind.Result, result.Kind);
        Assert.Equal(("counter", "session-1", "request-1"),
            (result.ApplicationId, result.SessionId, result.RequestId));
        var snapshot = store.GetSnapshot("counter")!;
        Assert.True(snapshot.IsConnected);
        Assert.True(snapshot.IsInteractive);
        Assert.Equal("counter", snapshot.Declaration!.Identity.Segments[0].Value);
        Assert.Equal("42", Assert.Single(snapshot.State!.Components).Text);
    }

    private static ProtocolMessage Message(MessageKind kind, string session = "session-1") => new()
    {
        Kind = kind,
        ApplicationId = "counter",
        SessionId = session,
        RequestId = "request-1"
    };

    private static ApplicationDeclaration Declaration(string application = "counter") => new(application,
        [new("controls", [new("reading", [new("increase")])], [new("panel", [new("open")])])]);

    private static ApplicationState State(long revision, string text = "42") =>
        new(revision, [new("controls", "reading", text, 42)]);

    [Fact]
    public void DeclarationAndInitialStateMustBothBeValidBeforePublicationAndCanBeResubmitted()
    {
        var store = new BrokerStateStore(["counter"]);
        store.Handle(Message(MessageKind.Welcome));
        var bad = store.Handle(Message(MessageKind.Declare) with
        {
            Declaration = Declaration(),
            State = new(0, [new("controls", "foreign", "invalid")])
        });
        Assert.False(bad.Result!.Accepted);
        Assert.Null(store.GetSnapshot("counter")!.Declaration);
        Assert.Null(store.GetSnapshot("counter")!.State);
        Assert.False(store.GetSnapshot("counter")!.IsInteractive);
        Assert.True(store.Handle(Message(MessageKind.Declare) with
        {
            Declaration = Declaration(),
            State = State(0)
        }).Result!.Accepted);
    }

    [Fact]
    public void NewerFullStateReplacesReadingsAndLateRevisionsCannotRollThemBack()
    {
        var store = Ready();
        Assert.True(store.Handle(Message(MessageKind.State) with { State = State(2, "new") }).Result!.Accepted);
        Assert.False(store.Handle(Message(MessageKind.State) with { State = State(1, "old") }).Result!.Accepted);
        Assert.False(store.Handle(Message(MessageKind.State) with { State = State(2, "duplicate") }).Result!.Accepted);
        Assert.Equal("new", Assert.Single(store.GetSnapshot("counter")!.State!.Components).Text);
        Assert.True(store.Handle(Message(MessageKind.State) with { State = new(3, []) }).Result!.Accepted);
        Assert.Empty(store.GetSnapshot("counter")!.State!.Components);
    }

    private static BrokerStateStore Ready()
    {
        var store = new BrokerStateStore(["counter"]);
        store.Handle(Message(MessageKind.Welcome));
        store.Handle(Message(MessageKind.Declare) with { Declaration = Declaration(), State = State(0) });
        return store;
    }

    [Fact]
    public void DisconnectPreservesReadingAndNewSessionRequiresFreshDeclarationWhileOldMessagesAreIsolated()
    {
        var store = Ready();
        Assert.True(store.Handle(Message(MessageKind.Disconnected)).Result!.Accepted);
        Assert.False(store.GetSnapshot("counter")!.IsConnected);
        Assert.False(store.GetSnapshot("counter")!.IsInteractive);
        Assert.Equal("42", store.GetSnapshot("counter")!.State!.Components[0].Text);
        Assert.False(store.Handle(Message(MessageKind.State) with { State = State(1) }).Result!.Accepted);
        store.Handle(Message(MessageKind.Welcome, "session-2"));
        Assert.False(store.Handle(Message(MessageKind.State, "session-2") with { State = State(1) }).Result!.Accepted);
        Assert.True(store.Handle(Message(MessageKind.Declare, "session-2") with
        {
            Declaration = Declaration(),
            State = State(0, "fresh")
        }).Result!.Accepted);
        Assert.False(store.Handle(Message(MessageKind.Disconnected)).Result!.Accepted);
        Assert.False(store.Handle(Message(MessageKind.State) with { State = State(99) }).Result!.Accepted);
        Assert.True(store.GetSnapshot("counter")!.IsInteractive);
        Assert.Equal("fresh", store.GetSnapshot("counter")!.State!.Components[0].Text);
    }

    [Fact]
    public void SameSessionDeclarationReplacementIsRejectedAndDisablesInteractionUntilNewSession()
    {
        var store = Ready();
        Assert.False(store.Handle(Message(MessageKind.Declare) with
        {
            Declaration = Declaration(),
            State = State(1, "replacement")
        }).Result!.Accepted);
        Assert.False(store.GetSnapshot("counter")!.IsInteractive);
        Assert.Equal("42", store.GetSnapshot("counter")!.State!.Components[0].Text);
        Assert.False(store.Handle(Message(MessageKind.State) with { State = State(2) }).Result!.Accepted);
    }

    [Fact]
    public void UnsupportedVersionAndMalformedEnvelopeCannotCreateOrReplaceSessions()
    {
        var store = Ready();
        Assert.False(store.Handle(Message(MessageKind.Welcome, "new") with { Version = 99 }).Result!.Accepted);
        Assert.False(store.Handle(Message(MessageKind.Welcome, "")).Result!.Accepted);
        Assert.False(store.Handle(Message(MessageKind.Welcome, new string('s', 257))).Result!.Accepted);
        Assert.False(store.Handle(Message(MessageKind.State) with { RequestId = new string('r', 257), State = State(1) }).Result!.Accepted);
        Assert.False(store.Handle(null!).Result!.Accepted);
        Assert.Equal("session-1", store.GetSnapshot("counter")!.SessionId);
        Assert.True(store.GetSnapshot("counter")!.IsInteractive);
    }

    [Fact]
    public void UnregisteredIdentityAndMismatchedDeclarationNeverPublish()
    {
        var store = new BrokerStateStore(["counter"]);
        Assert.False(store.Handle(Message(MessageKind.Welcome) with { ApplicationId = "unknown" }).Result!.Accepted);
        Assert.Empty(store.Snapshots);
        store.Handle(Message(MessageKind.Welcome));
        Assert.False(store.Handle(Message(MessageKind.State) with { State = State(0) }).Result!.Accepted);
        Assert.False(store.Handle(Message(MessageKind.Declare) with
        {
            Declaration = Declaration("foreign"),
            State = State(0)
        }).Result!.Accepted);
        Assert.Null(store.GetSnapshot("counter")!.Declaration);
    }

    public static IEnumerable<object[]> InvalidStates()
    {
        yield return [new ApplicationState(-1, [])];
        yield return [new ApplicationState(1, null!)];
        yield return [new ApplicationState(1, [null!])];
        yield return [new ApplicationState(1, [new("controls", "reading", null!)])];
        yield return [new ApplicationState(1, [new("controls", "reading", new string('x', 1025))])];
        yield return [new ApplicationState(1, [new("controls", "reading", "x", double.NaN)])];
        yield return [new ApplicationState(1, [new("controls", "reading", "x", double.PositiveInfinity)])];
        yield return [new ApplicationState(1, [new("foreign", "reading", "x")])];
        yield return [new ApplicationState(1, [new("controls", "reading", "one"), new("controls", "reading", "two")])];
        yield return [new ApplicationState(1, Enumerable.Repeat(new ComponentReading("controls", "reading", "x"), 129).ToArray())];
    }

    [Theory]
    [MemberData(nameof(InvalidStates))]
    public void InvalidStateRetainsEntireLastConfirmedSnapshot(ApplicationState state)
    {
        var store = Ready();
        var before = store.GetSnapshot("counter");
        Assert.False(store.Handle(Message(MessageKind.State) with { State = state }).Result!.Accepted);
        Assert.Same(before, store.GetSnapshot("counter"));
    }

    [Fact]
    public void InputAndPublishedCollectionsCannotMutateAcceptedState()
    {
        var store = Ready();
        var readings = new List<ComponentReading> { new("controls", "reading", "confirmed") };
        Assert.True(store.Handle(Message(MessageKind.State) with { State = new(1, readings) }).Result!.Accepted);
        readings.Clear();
        var snapshot = store.GetSnapshot("counter")!;
        Assert.Equal("confirmed", Assert.Single(snapshot.State!.Components).Text);
        Assert.Throws<NotSupportedException>(() => ((IList<ComponentReading>)snapshot.State.Components).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<BrokerApplicationSnapshot>)store.Snapshots).Clear());
    }

    [Fact]
    public void RegistrationBudgetIsBounded()
    {
        Assert.Throws<ArgumentException>(() => new BrokerStateStore(Enumerable.Range(0, 17).Select(i => $"app{i}").ToArray()));
        Assert.Throws<ArgumentException>(() => new BrokerStateStore([""]));
    }

    [Fact]
    public void InvalidNewSessionDeclarationRetainsLastReadingWithoutRevivingServiceInteraction()
    {
        var store = Ready();
        store.Handle(Message(MessageKind.Welcome, "session-2"));
        Assert.False(store.Handle(Message(MessageKind.Declare, "session-2") with
        {
            Declaration = new("counter", [new("controls", [], [])]),
            State = State(0, "bad")
        }).Result!.Accepted);
        var snapshot = store.GetSnapshot("counter")!;
        Assert.Equal("42", snapshot.State!.Components[0].Text);
        Assert.False(snapshot.IsInteractive);
        Assert.NotNull(snapshot.LastError);
        Assert.True(store.Handle(Message(MessageKind.Declare, "session-2") with
        {
            Declaration = Declaration(),
            State = State(0, "fresh")
        }).Result!.Accepted);
    }

    [Fact]
    public void ConcurrentStateUpdatesRemainMonotonicAndFailureInOneApplicationCannotDisableAnother()
    {
        var store = new BrokerStateStore(["counter", "other"]);
        foreach (var app in new[] { "counter", "other" })
        {
            store.Handle(Message(MessageKind.Welcome) with { ApplicationId = app });
            store.Handle(Message(MessageKind.Declare) with { ApplicationId = app, Declaration = Declaration(app), State = State(0) });
        }
        Parallel.For(1, 101, revision => store.Handle(Message(MessageKind.State) with { State = State(revision) }));
        Assert.Equal(100, store.GetSnapshot("counter")!.State!.Revision);
        store.Handle(Message(MessageKind.Disconnected));
        Assert.True(store.GetSnapshot("other")!.IsInteractive);
        Assert.True(store.Handle(Message(MessageKind.State) with { ApplicationId = "other", State = State(1, "unaffected") }).Result!.Accepted);
        Assert.Equal("unaffected", store.GetSnapshot("other")!.State!.Components[0].Text);
    }
}

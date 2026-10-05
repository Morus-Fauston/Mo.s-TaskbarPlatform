using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class MultiApplicationDisplayTests
{
    [Fact]
    public void BatchPreservesLocalComponentAndSeparatesIdenticallyNamedServiceComponents()
    {
        var display = Display();
        var states = Ready("one", "two");
        display.ApplyBrokerSnapshots(states.Snapshots);

        Assert.Equal(3, display.CurrentComponents.Count);
        Assert.Equal("MTP Host 组件", Find(display, "local").Text);
        Assert.Equal("one initial", Find(display, "one").Text);
        Assert.Equal("two initial", Find(display, "two").Text);
        Assert.All(display.CurrentComponents, component => Assert.False(component.IsVisible));
        Assert.True(display.SetVisibility(Find(display, "one").Identity, true).IsSuccess);
        Assert.True(Find(display, "one").IsVisible);
        Assert.False(Find(display, "two").IsVisible);
        Assert.False(Find(display, "local").IsVisible);
    }

    [Fact]
    public void UnchangedAcceptedSnapshotsDoNotRebuildTheCurrentPresentation()
    {
        var display = Display();
        var states = Ready("one", "two");
        display.ApplyBrokerSnapshots(states.Snapshots);
        var previous = display.CurrentComponents;
        display.ApplyBrokerSnapshots(states.Snapshots);
        Assert.Same(previous, display.CurrentComponents);
        display.ApplyBrokerSnapshot(states.GetSnapshot("one")!);
        Assert.Same(previous, display.CurrentComponents);
    }

    [Fact]
    public void OneServiceDisconnectOrInvalidDeclarationKeepsReadingsAndLeavesOtherApplicationsUsable()
    {
        var display = Display();
        var states = Ready("one", "two");
        display.ApplyBrokerSnapshots(states.Snapshots);
        Assert.True(display.SetVisibility(Find(display, "one").Identity, true).IsSuccess);
        states.Handle(Message("one", MessageKind.Disconnected));
        display.ApplyBrokerSnapshots(states.Snapshots);
        Assert.Equal("one initial", Find(display, "one").Text);
        Assert.Equal(CapabilityStatus.Unavailable, Find(display, "one").Status);
        Assert.True(Find(display, "one").IsVisible);
        Assert.Equal(CapabilityStatus.Available, Find(display, "two").Status);
        Assert.Equal(CapabilityStatus.Available, Find(display, "local").Status);

        states.Handle(Message("one", MessageKind.Welcome, "next"));
        Assert.False(states.Handle(Message("one", MessageKind.Declare, "next") with
        {
            Declaration = new("one", []),
            State = State(0, "invalid")
        }).Result!.Accepted);
        Assert.True(states.Handle(Message("two", MessageKind.State) with { State = State(1, "two new") }).Result!.Accepted);
        display.ApplyBrokerSnapshots(states.Snapshots);
        Assert.Equal("one initial", Find(display, "one").Text);
        Assert.Contains(states.GetSnapshot("one")!.LastError!.Message, Find(display, "one").StatusLabel);
        Assert.Equal("two new", Find(display, "two").Text);
        Assert.True(display.SetVisibility(Find(display, "local").Identity, true).IsSuccess);
    }

    [Fact]
    public void InvalidPayloadAndOldRevisionOrSessionCannotPartiallyReplaceEitherApplication()
    {
        var states = Ready("one", "two");
        var before = states.GetSnapshot("one")!;
        Assert.False(states.Handle(Message("one", MessageKind.State) with
        {
            State = new(1, [new("main", "counter", "would overwrite"), new("main", "not-declared", "invalid")])
        }).Result!.Accepted);
        Assert.Same(before, states.GetSnapshot("one"));
        Assert.True(states.Handle(Message("one", MessageKind.State) with { State = State(5, "five") }).Result!.Accepted);
        Assert.False(states.Handle(Message("one", MessageKind.State) with { State = State(4, "four") }).Result!.Accepted);
        Assert.False(states.Handle(Message("one", MessageKind.State) with { State = State(5, "duplicate") }).Result!.Accepted);
        states.Handle(Message("one", MessageKind.Welcome, "new-session"));
        Assert.True(states.Handle(Message("one", MessageKind.Declare, "new-session") with
        {
            Declaration = Declaration("one"),
            State = State(0, "new session")
        }).Result!.Accepted);
        Assert.False(states.Handle(Message("one", MessageKind.State) with { State = State(999, "old session") }).Result!.Accepted);
        Assert.False(states.Handle(Message("one", MessageKind.Disconnected)).Result!.Accepted);
        var display = Display();
        display.ApplyBrokerSnapshots(states.Snapshots);
        Assert.Equal("new session", Find(display, "one").Text);
        Assert.Equal(CapabilityStatus.Available, Find(display, "one").Status);
        Assert.Equal("two initial", Find(display, "two").Text);
    }

    [Fact]
    public void SlowPresentationConsumesOnlyLatestAcceptedApplicationState()
    {
        var states = Ready("one", "two");
        var display = Display();
        display.ApplyBrokerSnapshots(states.Snapshots);
        var oldPresentation = display.CurrentComponents;
        for (var revision = 1; revision <= 1000; revision++)
            Assert.True(states.Handle(Message("one", MessageKind.State) with { State = State(revision, "one " + revision) }).Result!.Accepted);
        Assert.True(states.Handle(Message("two", MessageKind.State) with { State = State(1, "two final") }).Result!.Accepted);
        Assert.Equal(2, states.Snapshots.Count);
        Assert.Equal("one initial", oldPresentation.Single(component => component.Identity.Segments[0].Value == "one").Text);
        display.ApplyBrokerSnapshots(states.Snapshots);
        Assert.Equal(3, display.CurrentComponents.Count);
        Assert.Equal("one 1000", Find(display, "one").Text);
        Assert.Equal("two final", Find(display, "two").Text);
        Assert.Throws<NotSupportedException>(() => ((IList<HostComponentDisplayModel>)display.CurrentComponents).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<BrokerApplicationSnapshot>)states.Snapshots).Clear());
    }

    [Fact]
    public void SnapshotBudgetAndDuplicateApplicationRejectWholePresentationBatch()
    {
        var states = Ready(Enumerable.Range(1, 16).Select(index => "app" + index).ToArray());
        var display = Display();
        display.ApplyBrokerSnapshots(states.Snapshots);
        Assert.Equal(17, display.CurrentComponents.Count);
        var before = display.CurrentComponents;
        Assert.Throws<ArgumentException>(() => display.ApplyBrokerSnapshots([states.Snapshots[0], states.Snapshots[0]]));
        Assert.Same(before, display.CurrentComponents);
        Assert.Throws<ArgumentException>(() => display.ApplyBrokerSnapshots([states.Snapshots[0], null!]));
        Assert.Same(before, display.CurrentComponents);
        var overflow = states.Snapshots.Concat(Ready("overflow").Snapshots).ToArray();
        Assert.Throws<ArgumentException>(() => display.ApplyBrokerSnapshots(overflow));
        Assert.Same(before, display.CurrentComponents);
    }

    [Fact]
    public void IndividualUpsertRetainsOtherApplicationsAndCompleteBatchReplacesOnlyServiceSet()
    {
        var states = Ready("one", "two");
        var display = Display();
        var input = states.Snapshots.ToList();
        display.ApplyBrokerSnapshots(input);
        input.Clear();
        Assert.Equal(3, display.CurrentComponents.Count);
        states.Handle(Message("one", MessageKind.State) with { State = State(1, "one new") });
        display.ApplyBrokerSnapshot(states.GetSnapshot("one")!);
        Assert.Equal(new[] { "local", "one", "two" }, display.CurrentComponents.Select(component => component.Identity.Segments[0].Value));
        Assert.Equal("one new", Find(display, "one").Text);
        Assert.Equal("two initial", Find(display, "two").Text);
        Assert.True(display.Load().Accepted);
        Assert.Equal(3, display.CurrentComponents.Count);
        display.ApplyBrokerSnapshots([states.GetSnapshot("two")!]);
        Assert.Equal(new[] { "local", "two" }, display.CurrentComponents.Select(component => component.Identity.Segments[0].Value));
        display.ApplyBrokerSnapshots([]);
        Assert.Equal("local", Assert.Single(display.CurrentComponents).Identity.Segments[0].Value);
    }

    [Fact]
    public void IdenticalFullIdentityUsesOneConfirmedServiceProjection()
    {
        var display = Display("one");
        var states = Ready("one");
        display.ApplyBrokerSnapshots(states.Snapshots);
        Assert.Equal("one initial", Assert.Single(display.CurrentComponents).Text);
    }

    private static HostDisplayController Display(string application = "local")
    {
        var display = new HostDisplayController(new DeclarationSource(JsonSerializer.Serialize(Declaration(application))), new PreferenceStore());
        Assert.True(display.Load().Accepted);
        return display;
    }

    private static BrokerStateStore Ready(params string[] applications)
    {
        var states = new BrokerStateStore(applications);
        foreach (var application in applications)
        {
            Assert.True(states.Handle(Message(application, MessageKind.Welcome)).Result!.Accepted);
            Assert.True(states.Handle(Message(application, MessageKind.Declare) with
            {
                Declaration = Declaration(application),
                State = State(0, application + " initial")
            }).Result!.Accepted);
        }
        return states;
    }

    private static ApplicationDeclaration Declaration(string application) => new(application,
        [new("main", [new("counter", [new("activate")])], [new("panel", [new("open")])])]);

    private static ApplicationState State(long revision, string text) => new(revision, [new("main", "counter", text)]);

    private static ProtocolMessage Message(string application, MessageKind kind, string session = "session") =>
        new() { ApplicationId = application, Kind = kind, SessionId = session };

    private static HostComponentDisplayModel Find(HostDisplayController display, string application) =>
        display.CurrentComponents.Single(component => component.Identity.Segments[0].Value == application);

    private sealed class DeclarationSource(string json) : IDeclarationSource
    {
        public CoreResult<string> Read() => CoreResult<string>.Success(json);
    }

    private sealed class PreferenceStore : IComponentDisplayPreferenceStore
    {
        private ComponentDisplayPreferences preferences = new();
        public ComponentDisplayPreferenceLoadResult Load() => new(preferences, null, ComponentDisplayPreferenceLoadState.Loaded);
        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool isVisible)
        {
            preferences = preferences.WithVisibility(identity, isVisible);
            return CoreResult<ComponentDisplayPreferences>.Success(preferences);
        }
    }
}

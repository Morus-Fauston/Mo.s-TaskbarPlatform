using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class ItemPresentationTests
{
    [Fact]
    public void SingleToggleIsScreenLocalAndDataRefreshPreservesTheSelectedPresentation()
    {
        var fixture = new Fixture();
        var controller = new ItemPresentationController(fixture.Store);
        Assert.True(controller.UpdateScreens(["left", "right"]).Accepted);
        var original = Assert.Single(controller.GetPresentation("left"));
        Assert.False(original.Expanded);
        Assert.True(controller.Toggle(original.Handle).Result.Accepted);
        Assert.True(Assert.Single(controller.GetPresentation("left")).Expanded);
        Assert.False(Assert.Single(controller.GetPresentation("right")).Expanded);
        fixture.Publish(1, "one");
        var refreshed = Assert.Single(controller.GetPresentation("left"));
        Assert.Equal(original.Handle, refreshed.Handle);
        Assert.True(refreshed.Expanded);
        Assert.Equal(WidthTier.Large, refreshed.Presentation.Width.Tier);
        Assert.False(original.Expanded);
    }

    [Fact]
    public void ReconnectedEquivalentStructureRetainsExpansionButChangesTheInteractionGeneration()
    {
        var fixture = new Fixture(); var controller = new ItemPresentationController(fixture.Store);
        controller.UpdateScreens(["screen"]);
        var old = Assert.Single(controller.GetPresentation("screen"));
        controller.Toggle(old.Handle);
        var copy = System.Text.Json.JsonSerializer.Deserialize<ApplicationDeclaration>(System.Text.Json.JsonSerializer.Serialize(fixture.Declaration))!;
        fixture.Reconnect(copy, "new", "one");
        var current = Assert.Single(controller.GetPresentation("screen"));
        Assert.True(current.Expanded);
        Assert.NotEqual(old.Handle, current.Handle);
        Assert.Null(controller.Resolve(old.Handle));
        Assert.False(controller.Toggle(old.Handle).Result.Accepted);
        Assert.NotNull(controller.Resolve(current.Handle));
        var structure = copy.FeatureGroups![0].Components![0].DynamicContent!.Structures[0];
        fixture.Reconnect(WithStructure(copy, structure with { Normal = structure.Normal with { Width = new(WidthTier.Medium) } }), "third", "one");
        Assert.False(Assert.Single(controller.GetPresentation("screen")).Expanded);
    }

    [Fact]
    public void DeclaredBatchOnlyTogglesCurrentTargetsAndNewItemsKeepTheirInitialState()
    {
        var fixture = new Fixture();
        var structure = fixture.Declaration.FeatureGroups![0].Components![0].DynamicContent!.Structures[0];
        fixture.Reconnect(WithStructure(fixture.Declaration, structure with { ExpandTargetStructureIds = ["row"] }), "batch", "one", "two");
        var controller = new ItemPresentationController(fixture.Store); controller.UpdateScreens(["screen"]);
        var first = controller.GetPresentation("screen")[0];
        controller.Toggle(first.Handle);
        Assert.True(controller.Toggle(first.Handle, declaredTargets: true).Result.Accepted);
        Assert.All(controller.GetPresentation("screen"), item => Assert.True(item.Expanded));
        fixture.Publish(1, "two", "three", "one");
        var added = controller.GetPresentation("screen");
        Assert.False(added.Single(item => item.Item.ItemId == "three").Expanded);
        Assert.True(added.Single(item => item.Item.ItemId == "one").Expanded);
        controller.Toggle(first.Handle, declaredTargets: true);
        Assert.All(controller.GetPresentation("screen"), item => Assert.True(item.Expanded));
        controller.Toggle(first.Handle, declaredTargets: true);
        Assert.All(controller.GetPresentation("screen"), item => Assert.False(item.Expanded));
    }

    [Fact]
    public void RemovalAndReappearanceBetweenReadsCannotReuseTheOldExpansionOrHandle()
    {
        var fixture = new Fixture(); var controller = new ItemPresentationController(fixture.Store);
        controller.UpdateScreens(["screen"]);
        var old = Assert.Single(controller.GetPresentation("screen"));
        controller.Toggle(old.Handle);
        fixture.Publish(1);
        fixture.Publish(2, "one");
        var appeared = Assert.Single(controller.GetPresentation("screen"));
        Assert.False(appeared.Expanded);
        Assert.NotEqual(old.Handle, appeared.Handle);
        Assert.False(controller.Toggle(old.Handle).Result.Accepted);
    }

    [Fact]
    public void UnobservedStructureChangesAndActionOmissionsAlsoInvalidateTheOldPresence()
    {
        var fixture = new Fixture(); var controller = new ItemPresentationController(fixture.Store);
        controller.UpdateScreens(["screen"]);
        var old = Assert.Single(controller.GetPresentation("screen")); controller.Toggle(old.Handle);
        var declaration = fixture.Declaration;
        var structure = declaration.FeatureGroups![0].Components![0].DynamicContent!.Structures[0];
        fixture.Reconnect(WithStructure(declaration, structure with { Overflow = TextOverflow.Scroll }), "changed", "one");
        fixture.Reconnect(declaration, "back", "one");
        var current = Assert.Single(controller.GetPresentation("screen"));
        Assert.False(current.Expanded); Assert.NotEqual(old.PresenceGeneration, current.PresenceGeneration);
        controller.Toggle(current.Handle);
        Assert.True(fixture.Store.ApplyActionState("app", fixture.Session, new(1, [], [])).Accepted);
        fixture.Publish(2, "one");
        Assert.False(Assert.Single(controller.GetPresentation("screen")).Expanded);
        Assert.False(controller.Toggle(current.Handle).Result.Accepted);
    }

    [Fact]
    public void EquivalentTargetAndControlSetsRetainStateWhileBindingChangesResetIt()
    {
        var fixture = new Fixture(); var original = fixture.Declaration.FeatureGroups![0].Components![0].DynamicContent!.Structures[0];
        var controls = new ItemControlActivation[] { new(ItemControlKind.PrimaryButton, new(ItemActivationKind.ToggleSelf)),
            new(ItemControlKind.SecondaryButton, new(ItemActivationKind.None)) };
        var structure = original with { ExpandTargetStructureIds = ["row"], PrimaryActivation = new(ItemActivationKind.ToggleSelf), ControlActivations = controls };
        fixture.Reconnect(WithStructure(fixture.Declaration, structure), "bindings", "one");
        var controller = new ItemPresentationController(fixture.Store); controller.UpdateScreens(["screen"]);
        var old = Assert.Single(controller.GetPresentation("screen")); controller.Toggle(old.Handle);
        fixture.Reconnect(WithStructure(fixture.Declaration, structure with
        {
            ExpandTargetStructureIds = new[] { "row" },
            ControlActivations = controls.Reverse().ToArray()
        }), "reordered", "one");
        var equal = Assert.Single(controller.GetPresentation("screen"));
        Assert.True(equal.Expanded); Assert.Equal(old.PresenceGeneration, equal.PresenceGeneration);
        fixture.Reconnect(WithStructure(fixture.Declaration, structure with { PrimaryActivation = new(ItemActivationKind.None) }), "different", "one");
        Assert.False(Assert.Single(controller.GetPresentation("screen")).Expanded);
    }

    [Fact]
    public void DisconnectRetainsLocalExpansionAndNewSessionInvalidatesClicksWithoutResettingState()
    {
        var fixture = new Fixture(); var controller = new ItemPresentationController(fixture.Store); controller.UpdateScreens(["screen"]);
        var old = Assert.Single(controller.GetPresentation("screen")); controller.Toggle(old.Handle);
        fixture.Store.Handle(fixture.Message(MessageKind.Disconnected));
        var offline = Assert.Single(controller.GetPresentation("screen"));
        Assert.True(offline.Expanded); Assert.False(offline.IsInteractive);
        Assert.True(controller.Toggle(offline.Handle).Result.Accepted);
        controller.Toggle(offline.Handle);
        fixture.BeginSession("recovering");
        var waiting = Assert.Single(controller.GetPresentation("screen"));
        Assert.True(waiting.Expanded); Assert.False(waiting.IsInteractive);
        Assert.False(controller.Toggle(offline.Handle).Result.Accepted);
        Assert.True(fixture.Store.Handle(fixture.Message(MessageKind.Declare) with { Declaration = fixture.Declaration, State = Fixture.State(0, ["one"]) }).Result!.Accepted);
        Assert.True(Assert.Single(controller.GetPresentation("screen")).Expanded);
    }

    [Fact]
    public void ScreenBudgetAndCleanupAreAtomicAndOldOwnersCannotMutateNewControllers()
    {
        var fixture = new Fixture(); var controller = new ItemPresentationController(fixture.Store);
        var screenIds = Enumerable.Range(0, 16).Select(index => "screen" + index).ToArray();
        Assert.True(controller.UpdateScreens(screenIds).Accepted);
        var old = Assert.Single(controller.GetPresentation("screen0")); controller.Toggle(old.Handle);
        Assert.False(controller.UpdateScreens([.. screenIds, "overflow"]).Accepted);
        Assert.False(controller.UpdateScreens(["screen0", "screen0"]).Accepted);
        Assert.False(controller.UpdateScreens([new string('s', 257)]).Accepted);
        Assert.False(controller.UpdateScreens([" "]).Accepted);
        Assert.True(Assert.Single(controller.GetPresentation("screen0")).Expanded);
        controller.UpdateScreens(screenIds.Skip(1).ToArray());
        controller.UpdateScreens(screenIds);
        Assert.False(Assert.Single(controller.GetPresentation("screen0")).Expanded);
        Assert.False(controller.Toggle(old.Handle).Result.Accepted);
        var newHandle = Assert.Single(controller.GetPresentation("screen0")).Handle;
        controller.Close(); controller.Close();
        Assert.Empty(controller.GetPresentation("screen0")); Assert.Null(controller.Resolve(newHandle));
        Assert.False(controller.UpdateScreens(["screen0"]).Accepted); Assert.False(controller.Toggle(newHandle).Result.Accepted);
        var restarted = new ItemPresentationController(fixture.Store); restarted.UpdateScreens(["screen0"]);
        Assert.False(restarted.Toggle(newHandle).Result.Accepted);
        Assert.False(Assert.Single(restarted.GetPresentation("screen0")).Expanded);
    }

    [Fact]
    public void RejectedStateKeepsFrozenOccurrenceMapAndAcceptedRemovalDropsAllReferences()
    {
        var fixture = new Fixture(); var controller = new ItemPresentationController(fixture.Store); controller.UpdateScreens(["screen"]);
        var old = Assert.Single(controller.GetPresentation("screen")); controller.Toggle(old.Handle);
        var map = fixture.Store.GetSnapshot("app")!.ItemOccurrences;
        Assert.Throws<NotSupportedException>(() => ((IDictionary<DynamicItemIdentity, long>)map).Clear());
        Assert.False(fixture.Store.Handle(fixture.Message(MessageKind.State) with { State = Fixture.State(1, ["one", "one"]) }).Result!.Accepted);
        Assert.True(Assert.Single(controller.GetPresentation("screen")).Expanded);
        Assert.Same(map, fixture.Store.GetSnapshot("app")!.ItemOccurrences);
        fixture.Publish(2);
        Assert.Empty(fixture.Store.GetSnapshot("app")!.ItemOccurrences);
        Assert.Empty(controller.GetPresentation("screen")); Assert.Single(map);
        Assert.Null(controller.Resolve(old.Handle));
    }

    [Fact]
    public void SameItemIdsInOtherApplicationsAndEntriesCannotJoinTheBatch()
    {
        var declaration = new Fixture().Declaration;
        var group = declaration.FeatureGroups![0]; var component = group.Components![0];
        component = component with
        {
            DynamicContent = component.DynamicContent! with
            {
                Structures =
            [component.DynamicContent!.Structures[0] with { ExpandTargetStructureIds = ["row"] }]
            }
        };
        declaration = declaration with { FeatureGroups = [group with { Components = [component, component with { ComponentId = "other" }] }] };
        var store = new BrokerStateStore(["app", "other-app"]);
        foreach (var app in new[] { "app", "other-app" })
        {
            store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = app, SessionId = "session" });
            var entry = Fixture.State(0, ["one"]).DynamicEntries![0];
            Assert.True(store.Handle(new()
            {
                Kind = MessageKind.Declare,
                ApplicationId = app,
                SessionId = "session",
                Declaration = declaration with { ApplicationId = app },
                State = new(0, [], [entry, entry with { ComponentId = "other" }])
            }).Result!.Accepted);
        }
        var controller = new ItemPresentationController(store); controller.UpdateScreens(["screen"]);
        var items = controller.GetPresentation("screen"); Assert.Equal(4, items.Count);
        var selected = items.Single(item => item.Handle.Item.ApplicationId == "app" && item.Handle.Item.ComponentId == "entry");
        Assert.True(controller.Toggle(selected.Handle, true).Result.Accepted);
        Assert.Single(controller.GetPresentation("screen"), item => item.Expanded);
    }

    [Fact]
    public void HiddenHeldItemsRetainExpansionButExpiryBeforeUnreadRecreationResetsIt()
    {
        var clock = new ManualClock(); var store = new BrokerStateStore(["app"], clock);
        var declaration = new Fixture().Declaration; var group = declaration.FeatureGroups![0]; var component = group.Components![0];
        declaration = declaration with
        {
            FeatureGroups = [group with { Components = [component with
            { DynamicContent = component.DynamicContent! with { Kind = DynamicContentKind.LiveIsland } }] }]
        };
        store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = "session" });
        Assert.True(store.Handle(new()
        {
            Kind = MessageKind.Declare,
            ApplicationId = "app",
            SessionId = "session",
            Declaration = declaration,
            State = new(0, [], [])
        }).Result!.Accepted);
        store.SetEntryDisplayAllowed("app", "main", "entry", true);
        ApplicationState State(long revision) => new(revision, [], [new("main", "entry", new(
            [new("activity", clock.Now.AddSeconds(5))], [new("one", "row", ["activity"], new(Status: new("active")))]))]);
        Assert.True(store.Handle(new() { Kind = MessageKind.State, ApplicationId = "app", SessionId = "session", State = State(1) }).Result!.Accepted);
        var controller = new ItemPresentationController(store); controller.UpdateScreens(["screen"]);
        var old = Assert.Single(controller.GetPresentation("screen")); controller.Toggle(old.Handle);
        store.SetEntryDisplayAllowed("app", "main", "entry", false);
        Assert.True(Assert.Single(controller.GetPresentation("screen")).Expanded);
        store.SetEntryDisplayAllowed("app", "main", "entry", true);
        clock.Now = clock.Now.AddSeconds(5);
        Assert.True(store.Handle(new() { Kind = MessageKind.State, ApplicationId = "app", SessionId = "session", State = State(2) }).Result!.Accepted);
        var reappeared = Assert.Single(controller.GetPresentation("screen"));
        Assert.False(reappeared.Expanded); Assert.NotEqual(old.PresenceGeneration, reappeared.PresenceGeneration);
        Assert.Null(controller.Resolve(old.Handle));
        clock.Now = clock.Now.AddSeconds(5);
        Assert.Empty(controller.GetPresentation("screen")); Assert.Empty(store.GetSnapshot("app")!.ItemOccurrences);
    }

    [Fact]
    public void MissingTargetsAndNonExpandableItemsRejectWithoutChangingOtherPresentations()
    {
        var fixture = new Fixture(); var controller = new ItemPresentationController(fixture.Store); controller.UpdateScreens(["screen"]);
        var current = Assert.Single(controller.GetPresentation("screen"));
        Assert.Equal("NoExpansionTargets", controller.Toggle(current.Handle, true).Result.Code);
        Assert.False(Assert.Single(controller.GetPresentation("screen")).Expanded);
        var structure = fixture.Declaration.FeatureGroups![0].Components![0].DynamicContent!.Structures[0];
        fixture.Reconnect(WithStructure(fixture.Declaration, structure with { Expanded = null }), "flat", "one");
        current = Assert.Single(controller.GetPresentation("screen"));
        Assert.False(controller.Toggle(current.Handle).Result.Accepted);
        Assert.Empty(controller.GetPresentation("unknown"));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ApplicationDeclaration WithStructure(ApplicationDeclaration declaration, ItemStructureDeclaration structure)
    {
        var group = declaration.FeatureGroups![0]; var component = group.Components![0];
        return declaration with
        {
            FeatureGroups = [group with { Components = [component with
            { DynamicContent = component.DynamicContent! with { Structures = [structure] } }] }]
        };
    }

    private sealed class Fixture
    {
        public BrokerStateStore Store { get; }
        public string Session { get; private set; } = "session";
        public ApplicationDeclaration Declaration { get; private set; }
        public Fixture()
        {
            Store = new(["app"]);
            Declaration = new("app", [new("main", [new("entry", [new("activate")], new(DynamicContentKind.OrdinaryItems,
                [new("row", true, new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small)),
                    new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Large))) ]))],
                [new("details", [new("activate")])])]);
            Reconnect(Declaration, "session", "one");
        }
        public void Reconnect(ApplicationDeclaration declaration, string session, params string[] items)
        {
            Declaration = declaration; BeginSession(session);
            Assert.True(Store.Handle(Message(MessageKind.Declare) with { Declaration = Declaration, State = State(0, items) }).Result!.Accepted);
        }
        public void BeginSession(string session) { Session = session; Assert.True(Store.Handle(Message(MessageKind.Welcome)).Result!.Accepted); }
        public void Publish(long revision, params string[] items) => Assert.True(Store.Handle(Message(MessageKind.State) with { State = State(revision, items) }).Result!.Accepted);
        public ProtocolMessage Message(MessageKind kind) => new() { Kind = kind, ApplicationId = "app", SessionId = Session };
        public static ApplicationState State(long revision, string[] items) => new(revision, [],
            [new("main", "entry", new([], items.Select(id => new DynamicItemState(id, "row", [], new(Status: new("value")))).ToArray()))]);
    }
}

using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class HostGroupPresentationTests
{
    [Fact]
    public void StaticComponentsUseHostWidthsAndInputOrderWithoutSpaceBasedHiding()
    {
        var left = Model("left", template: true);
        var hidden = Model("hidden") with { IsVisible = false };
        var right = Model("right");
        var result = HostGroupPresentation.Build([left, hidden, right], null, null, "screen", availableWidthDip: 50);
        Assert.Equal(736, result.Layout.WidthDip);
        Assert.True(result.Layout.Overflows);
        Assert.Equal(new[] { left, right }, result.Components);
        Assert.Equal(new double[] { 480, 240 }, result.Layout.Components.Select(value => value.Bounds.Width));
        Assert.Empty(result.Items);
        Assert.Same(left, result.ComponentsByKey[new("app", "group", "left")]);
    }

    [Fact]
    public void DynamicWidthsUseCurrentPresentationAndReadingChangesKeepGeometry()
    {
        var fixture = new Fixture();
        var first = fixture.Build();
        Assert.Equal(604, first.Layout.WidthDip);
        Assert.Equal(new[] { "left", "dynamic", "right" }, first.Components.Select(value => value.Identity.LocalId.Value));
        Assert.Equal(new[] { "one", "two" }, first.Items.Select(value => value.Item.ItemId));
        Assert.Equal(new double[] { 32, 64 }, first.Layout.Items.Select(value => value.Bounds.Width));
        fixture.Publish(1, new string('x', 100));
        var updated = fixture.Build();
        Assert.Equal(first.Layout.Items, updated.Layout.Items);
        Assert.Equal(new string('x', 100), updated.Items[0].Item.Fields.Status!.Text);
        Assert.True(fixture.Presentations.Toggle(updated.Items[0].Handle).Result.Accepted);
        var expanded = fixture.Build();
        Assert.Equal(668, expanded.Layout.WidthDip);
        Assert.Equal(96, expanded.Layout.Items[0].Bounds.Width);
        Assert.Equal(first.Layout.Components[^1].Bounds, expanded.Layout.Components[^1].Bounds);
        Assert.Equal(first.Layout.Components[0].Bounds.X - 64, expanded.Layout.Components[0].Bounds.X);
        Assert.Same(expanded.Items[0], expanded.ItemsByKey[expanded.Layout.Items[0].Key]);
    }

    [Fact]
    public void CompactDensityIsExplicitAndInsufficientSpaceNeverSelectsItAutomatically()
    {
        var fixture = new Fixture();
        var compact = fixture.Build(compact: true);
        Assert.Equal(new double[] { 28, 56 }, compact.Layout.Items.Select(value => value.Bounds.Width));
        Assert.Equal(532, compact.Layout.WidthDip);
        Assert.Equal(new double[] { 210, 88, 210 }, compact.Layout.Components.Select(value => value.Bounds.Width));
        Assert.Equal(32, compact.Layout.HeightDip);
        var large = HostGroupPresentation.Build(fixture.Components, fixture.States, fixture.Presentations, "screen",
            environment: new HostPresentationEnvironment(HostPresentationDensity.Normal, 2.25));
        Assert.Equal(32, large.Layout.HeightDip);
        var overflow = HostGroupPresentation.Build(fixture.Components, fixture.States, fixture.Presentations, "screen", availableWidthDip: 1);
        Assert.True(overflow.Layout.Overflows); Assert.Equal(604, overflow.Layout.WidthDip);
        Assert.Equal(2, overflow.Items.Count); Assert.Equal(3, overflow.Components.Count);
    }

    [Fact]
    public void ReconnectionRetainsAnimationIdentityWhileInteractionHandleChanges()
    {
        var fixture = new Fixture();
        var initial = fixture.Build();
        fixture.Reconnect();
        var reconnected = fixture.Build();
        Assert.Equal(initial.Layout.Items.Select(value => value.Key), reconnected.Layout.Items.Select(value => value.Key));
        Assert.NotEqual(initial.Items[0].Handle, reconnected.Items[0].Handle);
        Assert.Equal("replacement", reconnected.Items[0].SessionId);
    }

    [Fact]
    public void RemoveAndReappearBetweenTwoBuildsGetsANewAnimationIdentity()
    {
        var fixture = new Fixture(); var initial = fixture.Build();
        fixture.Publish(1, "removed", empty: true);
        fixture.Publish(2, "returned");
        var returned = fixture.Build();
        Assert.Equal(initial.Layout.WidthDip, returned.Layout.WidthDip);
        Assert.NotEqual(initial.Layout.Items[0].Key, returned.Layout.Items[0].Key);
        Assert.Equal(initial.Layout.Items[0].Key.Component, returned.Layout.Items[0].Key.Component);
        Assert.Equal(initial.Layout.Items[0].Key.ItemId, returned.Layout.Items[0].Key.ItemId);
    }

    [Fact]
    public void DisconnectRetainsGeometryButDisablesAllDynamicHitTargets()
    {
        var fixture = new Fixture(); var initial = fixture.Build();
        Assert.True(fixture.States.Handle(new() { Kind = MessageKind.Disconnected, ApplicationId = "app", SessionId = "session" }).Result!.Accepted);
        var disconnected = fixture.Build();
        Assert.Equal(initial.Layout.WidthDip, disconnected.Layout.WidthDip);
        Assert.Equal(initial.Layout.Items.Select(value => value.Bounds), disconnected.Layout.Items.Select(value => value.Bounds));
        Assert.All(disconnected.Layout.Items, value => Assert.False(value.IsInteractive));
        Assert.All(disconnected.Items, value => Assert.False(value.IsInteractive));
    }

    [Fact]
    public void HiddenAndEmptyEntriesUseNoGeometryAndProjectionDoesNotOwnScreenRegistration()
    {
        var fixture = new Fixture();
        var hidden = fixture.Components.Select(value => value with { IsVisible = false }).ToArray();
        var result = HostGroupPresentation.Build(hidden, fixture.States, fixture.Presentations, "screen");
        Assert.Equal(0, result.Layout.WidthDip); Assert.Empty(result.Components); Assert.Empty(result.Items);
        var unknownScreen = HostGroupPresentation.Build(fixture.Components, fixture.States, fixture.Presentations, "not-registered");
        Assert.Empty(unknownScreen.Items); Assert.Equal(496, unknownScreen.Layout.WidthDip);
        Assert.Empty(fixture.Presentations.GetPresentation("not-registered"));
        Assert.Equal(2, fixture.Presentations.GetPresentation("screen").Count);
    }

    [Fact]
    public void SnapshotCollectionsAreImmutableAndKeysNeverUseListPositions()
    {
        var fixture = new Fixture(); var initial = fixture.Build();
        var reversed = HostGroupPresentation.Build(fixture.Components.Reverse().ToArray(), fixture.States, fixture.Presentations, "screen");
        Assert.Equal(initial.Layout.Items.Select(value => value.Key), reversed.Layout.Items.Select(value => value.Key));
        Assert.Throws<NotSupportedException>(() => ((IList<HostComponentDisplayModel>)initial.Components).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<TaskbarItemKey, HostItemPresentation>)initial.ItemsByKey).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<HostItemPresentation>)initial.Items).Clear());
    }

    [Fact]
    public void UnknownBrokerComponentsAndMalformedInputNeverBecomeFallbackText()
    {
        var fixture = new Fixture();
        var result = HostGroupPresentation.Build([Model("removed")], fixture.States, fixture.Presentations, "screen");
        Assert.Empty(result.Components); Assert.Equal(0, result.Layout.WidthDip);
        Assert.Throws<ArgumentException>(() => HostGroupPresentation.Build([Model("first"), Model("first")], null, null, "screen"));
        Assert.Throws<ArgumentException>(() => HostGroupPresentation.Build([], null, null, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => HostGroupPresentation.Build([], null, null, "screen", availableWidthDip: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => HostGroupPresentation.Build(
            new HostComponentDisplayModel[TaskbarGroupLayout.MaximumComponents + 1], null, null, "screen"));
    }

    private sealed class Fixture
    {
        public BrokerStateStore States { get; } = new(["app"]);
        public ItemPresentationController Presentations { get; }
        public HostComponentDisplayModel[] Components { get; } = [Model("left"), Model("dynamic"), Model("empty"), Model("right")];
        private readonly ApplicationDeclaration declaration;
        public Fixture()
        {
            States.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = "session" });
            var small = new ItemPresentation(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small));
            var dynamicDeclaration = new DynamicContentDeclaration(DynamicContentKind.OrdinaryItems,
                [new("small", true, small, small with { Width = new(WidthTier.Large) }),
                 new("medium", true, small with { Width = new(WidthTier.Medium) })]);
            declaration = new("app", [new("group", [new("left", [new("activate")]), new("dynamic", [new("activate")], dynamicDeclaration),
                    new("empty", [new("activate")], dynamicDeclaration), new("right", [new("activate")])], [new("panel", [new("open")])])]);
            Assert.True(States.Handle(new()
            {
                Kind = MessageKind.Declare,
                ApplicationId = "app",
                SessionId = "session",
                Declaration = declaration,
                State = State(0, "ready")
            }).Result!.Accepted);
            Presentations = new(States);
            Assert.True(Presentations.UpdateScreens(["screen"]).Accepted);
        }
        public HostGroupPresentationSnapshot Build(bool compact = false) => HostGroupPresentation.Build(Components, States, Presentations, "screen", compact);
        public void Publish(long revision, string text, bool empty = false) => Assert.True(States.Handle(new()
        { Kind = MessageKind.State, ApplicationId = "app", SessionId = "session", State = State(revision, text, empty) }).Result!.Accepted);
        public void Reconnect()
        {
            Assert.True(States.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = "replacement" }).Result!.Accepted);
            Assert.True(States.Handle(new()
            { Kind = MessageKind.Declare, ApplicationId = "app", SessionId = "replacement", Declaration = declaration, State = State(0, "restored") }).Result!.Accepted);
        }
        private static ApplicationState State(long revision, string text, bool empty = false) => new(revision, [],
            [new("group", "dynamic", new([], empty ? [] : [new("one", "small", [], new(Status: new(text))), new("two", "medium", [], new(Status: new(text)))])),
             new("group", "empty", new([], []))]);
    }

    private static HostComponentDisplayModel Model(string id, bool template = false) =>
        new(new StableIdentity(new StableId("app")).CreateChild(new StableId("group")).CreateChild(new StableId(id)),
            id, CapabilityStatus.Available, "available", true, HasTemplate: template);
}

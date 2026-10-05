using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class ItemDisplayIntegrationTests
{
    [Fact]
    public void DisplayRefreshPreservesLocalExpansionWhileReplacingTheHostStoreClosesOldHandles()
    {
        using var display = new HostDisplayController(new Source(), new Preferences());
        var first = Ready();
        display.BindActivityPermissions(first);
        display.ApplyBrokerSnapshots(first.Snapshots);
        var items = display.ItemPresentations!;
        Assert.True(items.UpdateScreens(["screen"]).Accepted);
        var handle = Assert.Single(items.GetPresentation("screen")).Handle;
        Assert.True(display.ItemActivations!.Activate(handle, ItemActivationSource.BlankPrimary).Result.Accepted);
        Assert.True(Assert.Single(items.GetPresentation("screen")).Expanded);
        display.BindActivityPermissions(first);
        display.ApplyBrokerSnapshots(first.Snapshots);
        Assert.Same(items, display.ItemPresentations);
        Assert.True(Assert.Single(items.GetPresentation("screen")).Expanded);

        var next = Ready();
        display.BindActivityPermissions(next);
        display.ApplyBrokerSnapshots(next.Snapshots);
        Assert.False(items.Toggle(handle).Result.Accepted);
        Assert.True(display.ItemPresentations!.UpdateScreens(["screen"]).Accepted);
        Assert.False(Assert.Single(display.ItemPresentations.GetPresentation("screen")).Expanded);
        Assert.False(display.ItemActivations!.Activate(handle, ItemActivationSource.BlankPrimary).Result.Accepted);
        var current = display.ItemPresentations;
        var latest = Assert.Single(current.GetPresentation("screen")).Handle;
        display.Dispose();
        display.Dispose();
        Assert.False(current.Toggle(latest).Result.Accepted);
        Assert.False(display.SetVisibility(Assert.Single(display.CurrentComponents).Identity, true).IsSuccess);
        Assert.Throws<ObjectDisposedException>(() => display.BindActivityPermissions(next));
    }

    private static BrokerStateStore Ready()
    {
        var store = new BrokerStateStore(["app"]);
        store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = "session" });
        var normal = new ItemPresentation(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small));
        var structure = new ItemStructureDeclaration("status", true, normal, normal with { Width = new(WidthTier.Large) },
            PrimaryActivation: new(ItemActivationKind.ToggleSelf));
        Assert.True(store.Handle(new()
        {
            Kind = MessageKind.Declare,
            ApplicationId = "app",
            SessionId = "session",
            Declaration = new("app", [new("main", [new("ordinary", [new("activate")], new(DynamicContentKind.OrdinaryItems, [structure]))], [new("details", [new("open")])])]),
            State = new(0, [new("main", "ordinary", "ready")], [new("main", "ordinary", new([], [new("one", "status", [], new(Status: new("ready")))]))])
        }).Result!.Accepted);
        return store;
    }

    private sealed class Source : IDeclarationSource
    {
        public CoreResult<string> Read() => throw new InvalidOperationException("Broker-only fixture");
    }
    private sealed class Preferences : IComponentDisplayPreferenceStore
    {
        public ComponentDisplayPreferenceLoadResult Load() => new(new(), null, ComponentDisplayPreferenceLoadState.Loaded);
        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool visible) =>
            CoreResult<ComponentDisplayPreferences>.Success(new ComponentDisplayPreferences().WithVisibility(identity, visible));
    }
}

using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class BrokerDynamicStateTests
{
    [Fact]
    public void DynamicStateIsValidatedAtomicallyAndDeepFrozenWithOrdinaryReadings()
    {
        var store = new BrokerStateStore(["app"]);
        store.Handle(Message(MessageKind.Welcome));
        Assert.True(store.Handle(Message(MessageKind.Declare) with
        { Declaration = Declaration(), State = new(0, []) }).Result!.Accepted);
        Assert.True(store.SetEntryDisplayAllowed("app", "main", "island", true).Accepted);
        var activities = new List<ActivityState> { new("activity", DateTimeOffset.UtcNow.AddHours(1)) };
        var references = new List<string> { "activity" };
        var items = new List<DynamicItemState> { new("item", "status", references, new(Status: new("running"))) };
        var initial = new ApplicationState(1, [new("main", "island", "initial")],
            [new("main", "island", new(activities, items))]);
        var declared = store.Handle(Message(MessageKind.State) with { State = initial });
        Assert.True(declared.Result!.Accepted, declared.Result.Code);
        var frozen = store.GetSnapshot("app")!.State!;
        references.Clear();
        activities.Clear();
        items.Clear();
        Assert.Single(frozen.DynamicEntries!);
        Assert.Equal("activity", Assert.Single(Assert.Single(frozen.DynamicEntries!).Content.Items).ActivityIds[0]);

        var rejected = store.Handle(Message(MessageKind.State) with
        {
            State = new(2, [new("main", "island", "poison")],
                [new("main", "island", new([], [new("item", "foreign", [], new(Status: new("poison")))]))])
        });
        Assert.False(rejected.Result!.Accepted);
        Assert.Same(frozen, store.GetSnapshot("app")!.State);
        Assert.Equal("initial", frozen.Components[0].Text);
        var removed = store.Handle(Message(MessageKind.State) with { State = new(3, [], []) });
        Assert.True(removed.Result!.Accepted);
        Assert.Empty(store.GetSnapshot("app")!.State!.DynamicEntries!);
    }

    internal static ApplicationDeclaration Declaration() => new("app",
        [new("main", [new("island", [new("activate")], new(DynamicContentKind.LiveIsland,
            [new("status", true, new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small)))]))],
            [new("panel", [new("open")])])]);

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void ActivityBudgetSpansAllDynamicEntriesInApplication(int count, bool accepted)
    {
        var original = Declaration();
        var group = original.FeatureGroups![0];
        var component = group.Components![0];
        var store = new BrokerStateStore(["app"]);
        store.Handle(Message(MessageKind.Welcome));
        var declaration = original with
        {
            FeatureGroups = [group with { Components = [component, component with { ComponentId = "other" }] }]
        };
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var left = Enumerable.Range(0, 32).Select(i => new ActivityState("left-" + i, expires)).ToArray();
        var right = Enumerable.Range(0, count - 32).Select(i => new ActivityState("right-" + i, expires)).ToArray();
        var result = store.Handle(Message(MessageKind.Declare) with
        {
            Declaration = declaration,
            State = new(0, [], [new("main", "island", new(left, [])), new("main", "other", new(right, []))])
        });
        Assert.Equal(accepted, result.Result!.Accepted);
        Assert.Equal(accepted, store.GetSnapshot("app")!.IsInteractive);
        if (!accepted) Assert.Null(store.GetSnapshot("app")!.State);
    }

    [Fact]
    public void SameActivityTextInAnotherEntryDoesNotAuthorizeCrossEntryReference()
    {
        var original = Declaration();
        var group = original.FeatureGroups![0];
        var component = group.Components![0];
        var store = new BrokerStateStore(["app"]);
        store.Handle(Message(MessageKind.Welcome));
        var result = store.Handle(Message(MessageKind.Declare) with
        {
            Declaration = original with
            {
                FeatureGroups = [group with { Components = [component, component with { ComponentId = "other" }] }]
            },
            State = new(0, [],
                [new("main", "island", new([new("activity", DateTimeOffset.UtcNow.AddHours(1))], [])),
                 new("main", "other", new([], [new("item", "status", ["activity"], new(Status: new("bad")))]))])
        });
        Assert.False(result.Result!.Accepted);
        Assert.Null(store.GetSnapshot("app")!.State);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void ItemBudgetSpansOrdinaryAndLiveEntryCollections(int count, bool accepted)
    {
        var original = Declaration();
        var group = original.FeatureGroups![0];
        var live = group.Components![0];
        var ordinary = live with
        {
            ComponentId = "ordinary",
            DynamicContent = live.DynamicContent! with { Kind = DynamicContentKind.OrdinaryItems }
        };
        var store = new BrokerStateStore(["app"]);
        store.Handle(Message(MessageKind.Welcome));
        var liveItems = Enumerable.Range(0, 64).Select(i => new DynamicItemState("live-" + i, "status", ["activity"], new(Status: new("live")))).ToArray();
        var ordinaryItems = Enumerable.Range(0, count - 64).Select(i => new DynamicItemState("ordinary-" + i, "status", [], new(Status: new("ordinary")))).ToArray();
        var result = store.Handle(Message(MessageKind.Declare) with
        {
            Declaration = original with { FeatureGroups = [group with { Components = [live, ordinary] }] },
            State = new(0, [],
                [new("main", "island", new([new("activity", DateTimeOffset.UtcNow.AddHours(1))], liveItems)),
                 new("main", "ordinary", new([], ordinaryItems))])
        });
        Assert.Equal(accepted, result.Result!.Accepted);
        if (!accepted) Assert.Null(store.GetSnapshot("app")!.State);
    }

    internal static ProtocolMessage Message(MessageKind kind) => new()
    {
        Kind = kind,
        ApplicationId = "app",
        SessionId = "session",
        RequestId = "request"
    };
}

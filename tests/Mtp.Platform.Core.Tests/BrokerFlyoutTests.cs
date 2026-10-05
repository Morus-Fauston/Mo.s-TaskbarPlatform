using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class BrokerFlyoutTests
{
    [Fact]
    public void CompleteDeclarationRegistersAllEntryKindsAndDisablesBeforeFirstRequest()
    {
        var store = new BrokerStateStore(["app"]);
        store.Handle(Message(MessageKind.Welcome));
        Assert.True(store.Handle(Message(MessageKind.Declare) with
        {
            Declaration = Declaration(),
            State = new(0, [])
        }).Result!.Accepted);
        var entries = store.GetSnapshot("app")!.Declaration!.FlyoutEntries;
        Assert.Equal(4, entries.Count);
        var entry = entries.Single(value => value.Kind == FlyoutKind.InteractiveHint);
        Assert.True(store.FlyoutRequests.SetEntryEnabled(entry, false).Accepted);
        var request = Message(MessageKind.FlyoutRequest) with
        {
            Flyout = new("original-id", 1, "main", "interactive", FlyoutKind.InteractiveHint, 0)
        };
        Assert.Equal("EntryDisabled", store.Handle(request).Result!.Code);
        Assert.True(store.FlyoutRequests.SetEntryEnabled(entry, true).Accepted);
        Assert.Equal("DuplicateRequest", store.Handle(request).Result!.Code);
        Assert.Equal("Received", store.Handle(request with { Flyout = request.Flyout with { RequestSequence = 2 } }).Result!.Code);
        Assert.Equal("original-id", store.FlyoutRequests.GetLastResult("app")!.RequestId);
    }

    [Theory]
    [InlineData("component")]
    [InlineData("panel")]
    [InlineData("event")]
    public void HintIdentityCannotCollideWithAnyEntryKind(string identity)
    {
        var declaration = Declaration();
        var group = declaration.FeatureGroups![0];
        var result = new DeclarationValidator().Validate(declaration with
        {
            FeatureGroups = [group with { Hints = [new(identity, FlyoutKind.ShortHint)] }]
        });
        Assert.False(result.IsSuccess);
    }

    private static ApplicationDeclaration Declaration() => new("app",
        [new("main", [new("component", [new("activate")])], [new("panel", [new("open")])],
            [new("hint", FlyoutKind.ShortHint), new("interactive", FlyoutKind.InteractiveHint)],
            [new("event", EventClosePolicy.Persistent)])]);

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void HintCollectionsAreBoundedAndCopiedIntoValidatedEntries(int count, bool accepted)
    {
        var declaration = Declaration();
        var group = declaration.FeatureGroups![0];
        var hints = Enumerable.Range(0, count).Select(index => new HintEntryDeclaration("hint-" + index, FlyoutKind.ShortHint)).ToList();
        var result = new DeclarationValidator().Validate(declaration with
        {
            FeatureGroups = [group with { Hints = hints }]
        });
        Assert.Equal(accepted, result.IsSuccess);
        if (accepted)
        {
            hints.Clear();
            Assert.Equal(130, result.Value!.FlyoutEntries.Count);
        }
    }

    [Fact]
    public void EventChannelRequiresExplicitClosePolicyAndCannotUseHintType()
    {
        var declaration = Declaration();
        var group = declaration.FeatureGroups![0];
        Assert.False(new DeclarationValidator().Validate(declaration with
        {
            FeatureGroups = [group with { EventChannels = [new("event", null)] }]
        }).IsSuccess);
        Assert.False(new DeclarationValidator().Validate(declaration with
        {
            FeatureGroups = [group with { Hints = [new("injected", FlyoutKind.EventGroup)] }]
        }).IsSuccess);
    }

    private static ProtocolMessage Message(MessageKind kind) => new()
    {
        Kind = kind,
        ApplicationId = "app",
        SessionId = "session",
        RequestId = "correlation"
    };
}

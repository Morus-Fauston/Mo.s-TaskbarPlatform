using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class EventContractTests
{
    [Fact]
    public void Event_channel_declares_business_slots_as_its_own_complete_identity()
    {
        var result = new DeclarationValidator().Validate(Declaration());
        Assert.True(result.IsSuccess, result.Error?.ToString());
        var slot = Assert.Single(result.Value!.ActionSlots, value => value.Reference.EntryKind == ActionEntryKind.EventChannel);
        Assert.Equal(new("app", "main", ActionEntryKind.EventChannel, "updates", "confirm"), slot.Reference);
        Assert.Equal(ActionParameterKind.None, slot.ParameterKind);
        var entry = Assert.Single(result.Value.FlyoutEntries, value => value.Kind == FlyoutKind.EventGroup);
        Assert.Equal(EventClosePolicy.AutoClose, entry.ClosePolicy);
        Assert.Null(entry.Expansion);
    }

    [Fact]
    public async Task Event_controller_dispatches_business_identity_and_keeps_navigation_in_the_same_entry()
    {
        var store = Ready();
        ActionSlotReference? captured = null;
        TemplateNavigationIntent? navigation = null;
        using var controller = new TemplateInteractionController(store, "app", new("main", TemplateEntryKind.EventChannel, "updates"),
            (slot, parameter, session, _) =>
            {
                captured = slot; Assert.Equal("session", session); Assert.Equal(ActionParameterKind.None, parameter.Kind);
                return Task.FromResult(ProtocolResult.Reject("DemoEventFailure", "Rejected"));
            }, navigate: intent => { navigation = intent; return ProtocolResult.Success(); });
        Assert.Equal("DemoEventFailure", (await controller.ActivateAsync("confirm")).Code);
        Assert.Equal(new("app", "main", ActionEntryKind.EventChannel, "updates", "confirm"), captured);
        Assert.Equal("DemoEventFailure", controller.GetSnapshot()!.Nodes.Single(value => value.NodeId == "confirm").Error);
        Assert.True((await controller.ActivateAsync("open")).Accepted);
        Assert.NotNull(navigation);
        Assert.Equal(new(TemplateActionKind.OpenPanel, "child"), navigation.Action);
        Assert.Equal(new("main", TemplateEntryKind.EventChannel, "updates"), navigation.Entry);
        Assert.True(controller.IsCurrentNavigation(navigation));
        Assert.Equal("InvalidControlValue", (await controller.ActivateAsync("open", new(TemplateValueKind.Text, Text: "outside"))).Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("duplicate")]
    [InlineData("invalid-kind")]
    [InlineData("missing")]
    [InlineData("wrong-parameter")]
    [InlineData("over-budget")]
    [InlineData("expand-hint")]
    public void Malformed_event_business_or_navigation_declarations_are_rejected(string scenario)
    {
        var declaration = Declaration();
        var group = declaration.FeatureGroups![0];
        var channel = group.EventChannels![0];
        channel = scenario switch
        {
            "null" => channel with { ActionSlots = [null!] },
            "duplicate" => channel with { ActionSlots = [new("confirm"), new("confirm")] },
            "invalid-kind" => channel with { ActionSlots = [new("confirm", (ActionParameterKind)99)] },
            "missing" => channel with { ActionSlots = null },
            "wrong-parameter" => channel with { ActionSlots = [new("confirm", ActionParameterKind.Number)] },
            "over-budget" => channel with { ActionSlots = Enumerable.Range(0, 33).Select(value => new ActionSlotDeclaration("action" + value)).ToArray() },
            "expand-hint" => channel with { Template = new("main", [new("main", Button("expand", new(TemplateActionKind.ExpandHint)))], []) },
            _ => channel
        };
        Assert.False(new DeclarationValidator().Validate(declaration with { FeatureGroups = [group with { EventChannels = [channel] }] }).IsSuccess);
    }

    [Fact]
    public async Task Reconnection_invalidates_event_navigation_and_late_business_completion()
    {
        var store = Ready();
        var response = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        TemplateNavigationIntent? navigation = null;
        using var controller = new TemplateInteractionController(store, "app", new("main", TemplateEntryKind.EventChannel, "updates"),
            (_, _, _, _) => response.Task, navigate: value => { navigation = value; return ProtocolResult.Success(); });
        await controller.ActivateAsync("open");
        var pending = controller.ActivateAsync("confirm");
        Connect(store, "replacement"); controller.GetSnapshot();
        Assert.Equal("StaleTemplate", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Code);
        Assert.False(controller.IsCurrentNavigation(navigation!));
        response.SetResult(ProtocolResult.Reject("OldFailure", "old"));
        Assert.All(controller.GetSnapshot()!.Nodes, value => { Assert.False(value.Busy); Assert.Null(value.Error); });
    }

    private static ApplicationDeclaration Declaration() => new("app", [new("main",
        [new("component", [new("activate")])], [new("panel", [new("activate")])],
        EventChannels: [new("updates", EventClosePolicy.AutoClose,
            new("main", [new("main", new("root", TemplateNodeKind.Vertical, Children:
                [Button("confirm", new(TemplateActionKind.Business, "confirm")), Button("open", new(TemplateActionKind.OpenPanel, "child"))])),
                new("child", Button("back", new(TemplateActionKind.Back)))], [], Panels: [new("child")]), [new("confirm")])])]);
    private static TemplateNode Button(string id, TemplateAction action) => new(id, TemplateNodeKind.Button,
        Value: new(Literal: new(TemplateValueKind.Text, Text: id)), AccessibleName: id, Action: action);
    private static BrokerStateStore Ready() { var store = new BrokerStateStore(["app"]); Connect(store, "session"); return store; }
    private static void Connect(BrokerStateStore store, string session)
    {
        store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = session });
        var result = store.Handle(new() { Kind = MessageKind.Declare, ApplicationId = "app", SessionId = session,
            Declaration = Declaration(), State = new(0, [new("main", "component", "events")],
                TemplateEntries: [new(new("main", TemplateEntryKind.EventChannel, "updates"), [])]) }).Result!;
        Assert.True(result.Accepted, result.Message);
    }
}

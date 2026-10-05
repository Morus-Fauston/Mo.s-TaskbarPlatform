using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class InteractiveHintContractTests
{
    [Fact]
    public void Interactive_hint_declares_its_own_business_slots_without_impersonating_a_taskbar_entry()
    {
        const string json = """
            {"ApplicationId":"app","FeatureGroups":[{"FeatureGroupId":"main",
              "Components":[{"ComponentId":"component","ActionSlots":[{"ActionSlotId":"activate"}]}],
              "TaskbarFlyouts":[{"TaskbarFlyoutId":"panel","ActionSlots":[{"ActionSlotId":"activate"}]}],
              "Hints":[{"EntryId":"hint","Kind":2,"ActionSlots":[{"ActionSlotId":"level","ParameterKind":2}]}]}]}
            """;
        var result = new DeclarationValidator().ValidateJson(json);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        var slot = Assert.Single(result.Value!.ActionSlots, slot => slot.Reference.EntryId == "hint");
        Assert.Equal("Hint", slot.Reference.EntryKind.ToString());
        Assert.Equal("app", slot.Reference.ApplicationId);
        Assert.Equal("main", slot.Reference.FeatureGroupId);
        Assert.Equal("level", slot.Reference.ActionSlotId);
        Assert.Equal(ActionParameterKind.Number, slot.ParameterKind);
    }

    [Fact]
    public void Interactive_hint_expansion_is_a_controlled_action_to_an_existing_panel()
    {
        var hint = new HintEntryDeclaration("hint", FlyoutKind.InteractiveHint,
            new("hint-main", [new("hint-main", Button("expand", new(TemplateActionKind.ExpandHint)))], []),
            Expansion: new("panel", "details"));
        var result = new DeclarationValidator().Validate(Declaration(hint));
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(result.Value!.Templates, value => value.Entry.Kind == TemplateEntryKind.Hint);
        Assert.Equal(hint.Expansion, Assert.Single(result.Value.FlyoutEntries, value => value.Kind == FlyoutKind.InteractiveHint).Expansion);
    }

    [Fact]
    public async Task Hint_controller_dispatches_typed_business_identity_and_validated_expansion_intent()
    {
        var store = Ready();
        ActionSlotReference? sent = null;
        ActionParameter? parameter = null;
        TemplateNavigationIntent? intent = null;
        using var controller = new TemplateInteractionController(store, "app", new("main", TemplateEntryKind.Hint, "hint"),
            (slot, value, session, _) => { sent = slot; parameter = value; Assert.Equal("session", session); return Task.FromResult(ProtocolResult.Success()); },
            navigate: value => { intent = value; return ProtocolResult.Success("Expanded"); });
        Assert.True((await controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 37))).Accepted);
        Assert.Equal(new("app", "main", ActionEntryKind.Hint, "hint", "level"), sent);
        Assert.Equal(new(ActionParameterKind.Number, Number: 35), parameter);
        Assert.Equal("Expanded", (await controller.ActivateAsync("expand")).Code);
        Assert.NotNull(intent);
        Assert.Equal(new("main", TemplateEntryKind.Hint, "hint"), intent.Entry);
        Assert.Equal(new(TemplateActionKind.ExpandHint), intent.Action);
        Assert.True(controller.IsCurrentNavigation(intent));
    }

    [Theory]
    [InlineData("ordinary-slots")]
    [InlineData("ordinary-expansion")]
    [InlineData("ordinary-template-action")]
    [InlineData("unknown-entry")]
    [InlineData("unknown-panel")]
    [InlineData("unlisted-template")]
    [InlineData("target-without-template")]
    [InlineData("cross-group")]
    [InlineData("missing-expansion")]
    [InlineData("free-target")]
    [InlineData("duplicate-slot")]
    [InlineData("invalid-slot-kind")]
    [InlineData("missing-slot")]
    [InlineData("wrong-parameter")]
    [InlineData("null-slot")]
    public void Invalid_or_uncontrolled_hint_contracts_are_rejected_atomically(string scenario)
    {
        var hint = Interactive();
        hint = scenario switch
        {
            "ordinary-slots" => hint with { Kind = FlyoutKind.ShortHint, Template = null, Expansion = null },
            "ordinary-expansion" => hint with { Kind = FlyoutKind.ShortHint, Template = null, ActionSlots = null },
            "ordinary-template-action" => hint with { Kind = FlyoutKind.ShortHint, ActionSlots = null, Expansion = null,
                Template = new("main", [new("main", Button("back", new(TemplateActionKind.Back)))], []) },
            "unknown-entry" or "cross-group" => hint with { Expansion = new("other-panel") },
            "unknown-panel" => hint with { Expansion = new("panel", "missing") },
            "unlisted-template" => hint with { Expansion = new("panel", "panel-main") },
            "missing-expansion" => hint with { Expansion = null },
            "free-target" => hint with { Template = new("main", [new("main", Button("expand", new(TemplateActionKind.ExpandHint, "panel")))], []) },
            "duplicate-slot" => hint with { Template = null, ActionSlots = [new("same"), new("same")] },
            "invalid-slot-kind" => hint with { Template = null, ActionSlots = [new("bad", (ActionParameterKind)99)] },
            "missing-slot" => hint with { ActionSlots = null },
            "wrong-parameter" => hint with { ActionSlots = [new("level", ActionParameterKind.Boolean), new("enabled", ActionParameterKind.Boolean), new("fail")] },
            "null-slot" => hint with { Template = null, ActionSlots = [null!] },
            _ => hint
        };
        var declaration = Declaration(hint);
        if (scenario == "target-without-template")
            declaration = declaration with { FeatureGroups = [declaration.FeatureGroups![0] with
                { TaskbarFlyouts = [new("panel", [new("activate")])] }] };
        if (scenario == "cross-group")
            declaration = declaration with { FeatureGroups = [declaration.FeatureGroups![0],
                new("other", [new("component", [new("activate")])], [new("other-panel", [new("activate")],
                    new("main", [new("main", Text("other"))], []))])] };
        var result = new DeclarationValidator().Validate(declaration);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Hint_slots_keep_the_existing_per_entry_budget_and_do_not_retain_a_mutable_list()
    {
        var slots = Enumerable.Range(0, DeclarationValidator.MaximumActionSlots).Select(index => new ActionSlotDeclaration("a" + index)).ToList();
        var declaration = Declaration(Interactive() with { Template = null, ActionSlots = slots });
        var result = new DeclarationValidator().Validate(declaration);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        slots.Add(new("overflow"));
        Assert.Equal(32, result.Value!.ActionSlots.Count(slot => slot.Reference.EntryKind == ActionEntryKind.Hint));
        Assert.False(new DeclarationValidator().Validate(declaration).IsSuccess);
    }

    [Theory]
    [InlineData(TemplateEntryKind.Component)]
    [InlineData(TemplateEntryKind.TaskbarFlyout)]
    [InlineData(TemplateEntryKind.EventChannel)]
    public void ExpandHint_cannot_be_injected_into_other_template_entry_kinds(TemplateEntryKind kind)
    {
        int budget = 4096;
        var result = new TemplateValidator().Validate(new("main", kind, "entry"),
            new("main", [new("main", Button("expand", new(TemplateActionKind.ExpandHint)))], []), [], [], ref budget,
            allowHintActions: true, allowHintExpansion: true);
        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("back")]
    [InlineData("open")]
    [InlineData("panels")]
    public void Hint_surface_cannot_declare_internal_panel_navigation(string scenario)
    {
        var template = new EntryTemplateDeclaration("main",
            [new("main", scenario == "back" ? Button("back", new(TemplateActionKind.Back)) :
                scenario == "open" ? Button("open", new(TemplateActionKind.OpenPanel, "child")) : Text("text")),
             new("child", Text("child"))], [], Panels: scenario == "back" ? null : [new("child")]);
        var result = new DeclarationValidator().Validate(Declaration(Interactive() with { Template = template }));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Hint_slider_keeps_only_latest_pending_value_and_rolls_back_to_newest_confirmation()
    {
        var store = Ready();
        var first = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<double>();
        using var controller = new TemplateInteractionController(store, "app", new("main", TemplateEntryKind.Hint, "hint"),
            (slot, value, _, _) =>
            {
                Assert.Equal(ActionEntryKind.Hint, slot.EntryKind);
                lock (sent) sent.Add(value.Number!.Value);
                if (value.Number == 35) return first.Task;
                started.SetResult(); return second.Task;
            });
        long generation = controller.GetSnapshot()!.Generation;
        Assert.True(controller.Preview("slider", new(TemplateValueKind.Number, Number: 37), generation).Accepted);
        Assert.Equal(20, Slider(controller).Confirmed!.Number);
        var current = controller.ActivateAsync("slider");
        var superseded = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 60));
        var latest = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 80));
        Assert.Equal("Superseded", (await superseded).Code);
        Assert.Single(sent);
        first.SetResult(ProtocolResult.Success());
        Assert.True((await current).Accepted);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { 35d, 80d }, sent);
        Assert.True(store.Handle(new() { Kind = MessageKind.State, ApplicationId = "app", SessionId = "session", State = State(1, 45) }).Result!.Accepted);
        second.SetResult(ProtocolResult.Reject("DemoFailure", "rejected"));
        Assert.Equal("DemoFailure", (await latest).Code);
        Assert.Equal(generation, controller.GetSnapshot()!.Generation);
        var node = Slider(controller);
        Assert.Equal(45, node.Confirmed!.Number);
        Assert.Null(node.Preview);
        Assert.False(node.Busy);
        Assert.Equal("DemoFailure", node.Error);
    }

    [Fact]
    public async Task Hint_reconnection_cancels_inflight_and_pending_and_invalidates_expansion_intent()
    {
        var store = Ready();
        var late = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        TemplateNavigationIntent? captured = null;
        using var controller = new TemplateInteractionController(store, "app", new("main", TemplateEntryKind.Hint, "hint"),
            (_, _, _, _) => late.Task, navigate: value => { captured = value; return ProtocolResult.Success(); });
        Assert.True((await controller.ActivateAsync("expand")).Accepted);
        Assert.NotNull(captured);
        long generation = controller.GetSnapshot()!.Generation;
        var current = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 50));
        var pending = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 80));
        Connect(store, "replacement");
        controller.GetSnapshot();
        Assert.Equal("StaleTemplate", (await current.WaitAsync(TimeSpan.FromSeconds(2))).Code);
        Assert.Equal("StaleTemplate", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Code);
        Assert.False(controller.IsCurrentNavigation(captured));
        Assert.Equal("StaleTemplate", (await controller.ActivateAsync("expand", expectedGeneration: generation)).Code);
        late.SetResult(ProtocolResult.Reject("LateFailure", "old"));
        Assert.All(controller.GetSnapshot()!.Nodes, node => { Assert.Null(node.Error); Assert.Null(node.Preview); Assert.False(node.Busy); });
    }

    [Fact]
    public async Task Expansion_rejects_parameters_and_requires_a_host_navigation_owner()
    {
        var store = Ready(); int navigations = 0; int actions = 0;
        using var owned = new TemplateInteractionController(store, "app", new("main", TemplateEntryKind.Hint, "hint"),
            (_, _, _, _) => { actions++; return Task.FromResult(ProtocolResult.Success()); },
            navigate: _ => { navigations++; return ProtocolResult.Success(); });
        Assert.Equal("InvalidControlValue", (await owned.ActivateAsync("expand", new(TemplateValueKind.Text, Text: "free target"))).Code);
        Assert.Equal(0, navigations); Assert.Equal(0, actions);
        using var unowned = new TemplateInteractionController(store, "app", new("main", TemplateEntryKind.Hint, "hint"),
            (_, _, _, _) => Task.FromResult(ProtocolResult.Success()));
        Assert.Equal("NavigationUnavailable", (await unowned.ActivateAsync("expand")).Code);
        Assert.Equal("InvalidControlValue", (await unowned.ActivateAsync("expand", new(TemplateValueKind.Number, Number: 1))).Code);
    }

    private static TemplateNodeSnapshot Slider(TemplateInteractionController controller) =>
        controller.GetSnapshot()!.Nodes.Single(value => value.NodeId == "slider");

    private static HintEntryDeclaration Interactive() => new("hint", FlyoutKind.InteractiveHint,
        new("hint-main", [new("hint-main", new("root", TemplateNodeKind.Vertical, Children:
        [new("slider", TemplateNodeKind.Slider, Value: new(StateFieldId: "level"), AccessibleName: "level",
            Action: new(TemplateActionKind.Business, "level"), Minimum: 0, Maximum: 100, Step: 5),
         new("toggle", TemplateNodeKind.Toggle, Value: new(StateFieldId: "enabled"), AccessibleName: "enabled",
            Action: new(TemplateActionKind.Business, "enabled")),
         Button("fail", new(TemplateActionKind.Business, "fail")), Button("expand", new(TemplateActionKind.ExpandHint))]))],
            [new("level", TemplateValueKind.Number), new("enabled", TemplateValueKind.Boolean)]),
        [new("level", ActionParameterKind.Number), new("enabled", ActionParameterKind.Boolean), new("fail")], new("panel", "details"));

    private static BrokerStateStore Ready()
    {
        var store = new BrokerStateStore(["app"]);
        Connect(store, "session");
        return store;
    }
    private static void Connect(BrokerStateStore store, string session)
    {
        store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = session });
        var result = store.Handle(new() { Kind = MessageKind.Declare, ApplicationId = "app", SessionId = session,
            Declaration = Declaration(Interactive()), State = State(0, 20) }).Result;
        Assert.True(result?.Accepted, result?.ToString());
    }
    private static ApplicationState State(long revision, double level) => new(revision, [new("main", "component", "value")],
        TemplateEntries: [new(new("main", TemplateEntryKind.TaskbarFlyout, "panel"), []),
            new(new("main", TemplateEntryKind.Hint, "hint"), [new("level", new(TemplateValueKind.Number, Number: level)),
                new("enabled", new(TemplateValueKind.Boolean, Boolean: false))])]);

    private static ApplicationDeclaration Declaration(HintEntryDeclaration hint) => new("app", [new("main",
        [new("component", [new("activate")])],
        [new("panel", [new("activate")], new("panel-main",
            [new("panel-main", Text("title")), new("details", Text("details-title"))], [], Panels: [new("details")]))],
        Hints: [hint])]);
    private static TemplateNode Text(string id) => new(id, TemplateNodeKind.Text,
        Value: new(Literal: new(TemplateValueKind.Text, Text: id)));
    private static TemplateNode Button(string id, TemplateAction action) => new(id, TemplateNodeKind.Button,
        AccessibleName: id, Value: new(Literal: new(TemplateValueKind.Text, Text: id)), Action: action);
}

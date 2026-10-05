using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class TemplateValidationTests
{
    [Fact]
    public void Template_nodes_are_published_as_a_deeply_frozen_declared_tree()
    {
        var children = new[] { new TemplateNode("label", TemplateNodeKind.Text, Value: new(new(TemplateValueKind.Text, Text: "confirmed"))) };
        var templates = new[] { new HostTemplate("main", new("root", TemplateNodeKind.Horizontal, Children: children)) };
        var result = new DeclarationValidator().Validate(Declaration(new("main", templates, [])));
        Assert.True(result.IsSuccess, result.Error?.Message);
        var frozen = Assert.Single(result.Value!.Templates);
        Assert.Equal(new TemplateEntryReference("group", TemplateEntryKind.Component, "component"), frozen.Entry);
        children[0] = new("changed", TemplateNodeKind.Separator);
        templates[0] = new("other", new("other", TemplateNodeKind.Separator));
        Assert.Equal("confirmed", frozen.Declaration.Templates[0].Root.Children![0].Value!.Literal!.Text);
    }

    [Fact]
    public void Invalid_trees_are_rejected_before_publication_or_recursive_allocation()
    {
        var leaf = new TemplateNode("leaf", TemplateNodeKind.Text, Value: new(new(TemplateValueKind.Text, Text: "text")));
        var deep = leaf;
        for (int level = 0; level < 8; level++) deep = new("level" + level, TemplateNodeKind.Vertical, Children: [deep]);
        foreach (var root in new TemplateNode[]
        {
            leaf with { Kind = (TemplateNodeKind)99 }, leaf with { NodeId = " " },
            leaf with { Children = [new("child", TemplateNodeKind.Separator)] },
            new("parent", TemplateNodeKind.Horizontal, Children: [leaf, leaf]), deep,
            new("parent", TemplateNodeKind.Vertical, Children: Enumerable.Range(0,65).Select(i => new TemplateNode("n"+i, TemplateNodeKind.Separator)).ToArray()),
            leaf with { Minimum = 0 }, leaf with { Icon = HostIcon.Info },
            new("separator", TemplateNodeKind.Separator, Action: new(TemplateActionKind.Back))
        }) Assert.False(new DeclarationValidator().Validate(Declaration(new("main", [new("main", root)], []))).IsSuccess);
    }

    [Fact]
    public void Bindings_are_typed_and_controls_only_reference_matching_declared_action_parameters()
    {
        var fields = new[] { new TemplateFieldDeclaration("on", TemplateValueKind.Boolean), new("number", TemplateValueKind.Number) };
        var valid = new TemplateNode("toggle", TemplateNodeKind.Toggle, Value: new(StateFieldId: "on"), AccessibleName: "Enabled", Action: new(TemplateActionKind.Business, "toggle"));
        Assert.True(new DeclarationValidator().Validate(Declaration(new("main", [new("main", valid)], fields))).IsSuccess);
        foreach (var invalid in new[]
        {
            valid with { Value = new(StateFieldId: "number") }, valid with { Value = new(StateFieldId: "missing") },
            valid with { Value = new(new(TemplateValueKind.Boolean, Boolean: true)) },
            valid with { Value = new(new(TemplateValueKind.Boolean, Boolean: true), "on") },
            valid with { Action = new(TemplateActionKind.Business, "slide") }, valid with { Action = new(TemplateActionKind.Business, "unknown") },
            valid with { Action = new(TemplateActionKind.Back) }, valid with { AccessibleName = "" },
            new("text", TemplateNodeKind.Text, Value: new(new(TemplateValueKind.Text, Text: "x", Boolean: true))),
            new("slider", TemplateNodeKind.Slider, Value: new(StateFieldId: "number"), AccessibleName: "Value", Action: new(TemplateActionKind.Business, "slide"), Minimum: 1, Maximum: 1, Step: 0),
            valid with { Enabled = new(StateFieldId: "number") }
        }) Assert.False(new DeclarationValidator().Validate(Declaration(new("main", [new("main", invalid)], fields))).IsSuccess);
        Assert.False(new DeclarationValidator().Validate(Declaration(new("main", [new("main", valid)],
            [new("on", TemplateValueKind.Boolean, Optional: true)]))).IsSuccess);
    }

    [Fact]
    public void Whole_template_state_fills_defaults_freezes_fields_and_rejects_invalid_updates_atomically()
    {
        var template = new EntryTemplateDeclaration("main", [new("main", new("label", TemplateNodeKind.Text, Value: new(StateFieldId: "name")))],
            [new("name", TemplateValueKind.Text), new("on", TemplateValueKind.Boolean, true, new(TemplateValueKind.Boolean, Boolean: false))]);
        var store = new BrokerStateStore(["app"]);
        ProtocolMessage Message(MessageKind kind) => new() { Kind = kind, ApplicationId = "app", SessionId = "session" };
        var reference = new TemplateEntryReference("group", TemplateEntryKind.Component, "component");
        var fields = new[] { new TemplateFieldValue("name", new(TemplateValueKind.Text, Text: "original")) };
        var state = new ApplicationState(0, [], TemplateEntries: [new(reference, fields)]);
        Assert.True(store.Handle(Message(MessageKind.Welcome)).Result!.Accepted);
        Assert.True(store.Handle(Message(MessageKind.Declare) with { Declaration = Declaration(template), State = state }).Result!.Accepted);
        var original = store.GetSnapshot("app")!.State!;
        var frozen = Assert.Single(original.TemplateEntries!);
        Assert.Equal(2, frozen.Fields.Count);
        Assert.False(frozen.Fields.Single(value => value.FieldId == "on").Value.Boolean);
        fields[0] = new("name", new(TemplateValueKind.Text, Text: "mutated"));
        Assert.Equal("original", frozen.Fields[0].Value.Text);
        foreach (var invalid in new TemplateEntryState[][]
        {
            [], [new(reference, [])], [new(reference, [new("name", new(TemplateValueKind.Number, Number: 1))])],
            [new(reference, [new("name", new(TemplateValueKind.Text, Text: "ok")), new("unknown", new(TemplateValueKind.Boolean, Boolean: true))])],
            [new(reference, fields), new(reference, fields)]
        })
        {
            Assert.False(store.Handle(Message(MessageKind.State) with { State = new(1, [], TemplateEntries: invalid) }).Result!.Accepted);
            Assert.Same(original, store.GetSnapshot("app")!.State);
        }
    }

    [Fact]
    public void Image_registry_is_bounded_unique_frozen_and_only_local_registered_references_are_accepted()
    {
        var template = new EntryTemplateDeclaration("main", [new("main", new("image", TemplateNodeKind.Image,
            Value: new(new(TemplateValueKind.Resource, ResourceId: "photo")), AccessibleName: "Photo"))], []);
        var images = new[] { new ImageResourceDeclaration("photo", ImageResourceFormat.Png) };
        var result = new DeclarationValidator().Validate(Declaration(template) with { Images = images });
        Assert.True(result.IsSuccess, result.Error?.Message);
        images[0] = new("other", ImageResourceFormat.Jpeg);
        Assert.Equal("photo", Assert.Single(result.Value!.Images).ResourceId);
        foreach (var invalid in new ImageResourceDeclaration[][]
        {
            [new("photo", (ImageResourceFormat)99)], [new("photo", ImageResourceFormat.Png), new("photo", ImageResourceFormat.Png)],
            Enumerable.Range(0,65).Select(i => new ImageResourceDeclaration("image"+i, ImageResourceFormat.Png)).ToArray(), [null!]
        }) Assert.False(new DeclarationValidator().Validate(Declaration(template) with { Images = invalid }).IsSuccess);
        Assert.False(new DeclarationValidator().Validate(Declaration(template)).IsSuccess);
    }

    [Fact]
    public void Panel_navigation_is_owned_by_the_entry_and_rejects_cycles_and_unknown_targets()
    {
        TemplateNode Open(string target) => new("open", TemplateNodeKind.Button, AccessibleName: "Open", Action: new(TemplateActionKind.OpenPanel, target));
        var template = new EntryTemplateDeclaration("main", [new("main", Open("details")),
            new("details", new("back", TemplateNodeKind.Button, AccessibleName: "Back", Action: new(TemplateActionKind.Back)))], [], Panels: [new("details")]);
        Assert.True(new DeclarationValidator().Validate(Declaration(template)).IsSuccess);
        foreach (var invalid in new[]
        {
            template with { Panels = [] }, template with { Panels = [new("missing")] },
            template with { Panels = [new("details"), new("details")] },
            template with { Panels = [new("main")] }, template with { Panels = [new("details", (PanelPresentation)99)] },
            template with { Templates = [template.Templates[0], new("details", Open("details"))] },
            new EntryTemplateDeclaration("main", [new("main", Open("a")), new("a", Open("b")), new("b", Open("a"))], [], Panels: [new("a"), new("b")])
        }) Assert.False(new DeclarationValidator().Validate(Declaration(invalid)).IsSuccess);
        var children = new List<TemplateNode>();
        var cyclic = new TemplateNode("cycle", TemplateNodeKind.Horizontal, Children: children);
        children.Add(cyclic);
        Assert.False(new DeclarationValidator().Validate(Declaration(new("main", [new("main", cyclic)], []))).IsSuccess);
    }

    [Fact]
    public void Every_declared_entry_kind_uses_the_same_template_gate_without_inventing_hint_business_slots()
    {
        var template = new EntryTemplateDeclaration("main", [new("main", new("label", TemplateNodeKind.Text, Value: new(new(TemplateValueKind.Text, Text: "text"))))], []);
        var declaration = Declaration(template);
        var group = declaration.FeatureGroups![0];
        declaration = declaration with
        {
            FeatureGroups = [group with {
            TaskbarFlyouts = [group.TaskbarFlyouts![0] with { Template = template }],
            Hints = [new("hint", FlyoutKind.ShortHint, template)],
            EventChannels = [new("event", EventClosePolicy.Persistent, template)] }]
        };
        var result = new DeclarationValidator().Validate(declaration);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(4, result.Value!.Templates.Count);
        var business = template with { Templates = [new("main", new("button", TemplateNodeKind.Button, AccessibleName: "Run", Action: new(TemplateActionKind.Business, "run")))] };
        Assert.False(new DeclarationValidator().Validate(declaration with { FeatureGroups = [group with { Hints = [new("hint", FlyoutKind.InteractiveHint, business)] }] }).IsSuccess);
        Assert.False(new DeclarationValidator().Validate(declaration with { FeatureGroups = [group with { EventChannels = [new("event", EventClosePolicy.Persistent, business)] }] }).IsSuccess);
        var dynamic = new DynamicContentDeclaration(DynamicContentKind.OrdinaryItems, [new("row", true, new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small)))]);
        Assert.False(new DeclarationValidator().Validate(declaration with
        {
            FeatureGroups = [group with {
            Components = [group.Components![0] with { DynamicContent = dynamic }] }]
        }).IsSuccess);
    }

    [Fact]
    public void Slider_confirmed_values_are_in_range_and_step_aligned_with_decimal_tolerance()
    {
        var template = new EntryTemplateDeclaration("main", [new("main", new("slider", TemplateNodeKind.Slider,
            Value: new(StateFieldId: "value"), AccessibleName: "Volume", Action: new(TemplateActionKind.Business, "slide"), Minimum: 0, Maximum: 1, Step: 0.1))],
            [new("value", TemplateValueKind.Number)]);
        var declaration = new DeclarationValidator().Validate(Declaration(template)).Value!;
        var validator = new TemplateValidator();
        IReadOnlyList<TemplateEntryState> State(double value) => [new(new("group", TemplateEntryKind.Component, "component"), [new("value", new(TemplateValueKind.Number, Number: value))])];
        Assert.True(validator.ValidateState(declaration, State(0.3)).IsSuccess);
        Assert.True(validator.ValidateState(declaration, State(0.1 + 0.2)).IsSuccess);
        foreach (var invalid in new[] { -0.1, 1.1, 0.35, double.NaN, double.PositiveInfinity })
            Assert.False(validator.ValidateState(declaration, State(invalid)).IsSuccess);
        var irregular = template with { Templates = [new("main", template.Templates[0].Root with { Maximum = 12, Step = 5 })] };
        var irregularSchema = new DeclarationValidator().Validate(Declaration(irregular)).Value!;
        Assert.True(validator.ValidateState(irregularSchema, State(10)).IsSuccess);
        Assert.False(validator.ValidateState(irregularSchema, State(12)).IsSuccess);
        Assert.False(new DeclarationValidator().Validate(Declaration(template with
        {
            Fields = [new("value", TemplateValueKind.Number, true, new(TemplateValueKind.Number, Number: 0.35))]
        })).IsSuccess);
    }

    [Fact]
    public void Template_limits_accept_the_boundary_and_shared_references_cannot_bypass_the_application_budget()
    {
        TemplateNode Tree(int leaves) => new("root", TemplateNodeKind.Vertical, Children: Enumerable.Range(0, 4).Select(group =>
            new TemplateNode("group" + group, TemplateNodeKind.Horizontal, Children: Enumerable.Range(group * 64, Math.Clamp(leaves - group * 64, 0, 64))
                .Select(index => new TemplateNode("leaf" + index, TemplateNodeKind.Separator)).ToArray())).ToArray());
        var validator = new DeclarationValidator();
        var exact = new EntryTemplateDeclaration("main", [new("main", Tree(251))], []);
        Assert.True(validator.Validate(Declaration(exact)).IsSuccess); // root + 4 groups + 251 leaves = 256
        Assert.False(validator.Validate(Declaration(exact with { Templates = [new("main", Tree(252))] })).IsSuccess);
        var reused = Enumerable.Range(0, 16).Select(i => new HostTemplate(i == 0 ? "main" : "template" + i, exact.Templates[0].Root)).ToArray();
        var sharedBudget = validator.Validate(Declaration(exact with { Templates = reused }));
        Assert.False(sharedBudget.IsSuccess);
        Assert.Equal("template_budget_exceeded", sharedBudget.Error!.Code);
        var minimal = new HostTemplate("main", new("only", TemplateNodeKind.Separator));
        Assert.True(validator.Validate(Declaration(exact with { Templates = [minimal], Fields = Enumerable.Range(0, 128).Select(i => new TemplateFieldDeclaration("field" + i, TemplateValueKind.Text)).ToArray() })).IsSuccess);
        Assert.False(validator.Validate(Declaration(exact with { Templates = [minimal], Fields = Enumerable.Range(0, 129).Select(i => new TemplateFieldDeclaration("field" + i, TemplateValueKind.Text)).ToArray() })).IsSuccess);
        Assert.False(validator.Validate(Declaration(exact with { Templates = Enumerable.Range(0, 17).Select(i => minimal with { TemplateId = i == 0 ? "main" : "t" + i }).ToArray() })).IsSuccess);
        var atDepth = new TemplateNode("leaf", TemplateNodeKind.Separator);
        for (int i = 0; i < 7; i++) atDepth = new("depth" + i, TemplateNodeKind.Vertical, Children: [atDepth]);
        Assert.True(validator.Validate(Declaration(exact with { Templates = [new("main", atDepth)] })).IsSuccess);
    }

    [Fact]
    public void Unknown_json_properties_and_malformed_typed_values_are_never_silently_accepted()
    {
        var template = new EntryTemplateDeclaration("main", [new("main", new("label", TemplateNodeKind.Text,
            Value: new(new(TemplateValueKind.Text, Text: "text"))))], []);
        var validator = new DeclarationValidator();
        string json = System.Text.Json.JsonSerializer.Serialize(Declaration(template));
        Assert.True(validator.ValidateJson(json).IsSuccess);
        Assert.False(validator.ValidateJson(json.Replace("\"NodeId\":\"label\"", "\"NodeId\":\"label\",\"FontFamily\":\"Injected\"")).IsSuccess);
        foreach (var value in new[] { new TemplateValue((TemplateValueKind)99), new(TemplateValueKind.Text),
            new(TemplateValueKind.Text, Text: new string('x', 1025)), new(TemplateValueKind.Text, Text: "x", ResourceId: "photo") })
            Assert.False(validator.Validate(Declaration(template with { Templates = [new("main", template.Templates[0].Root with { Value = new(value) })] })).IsSuccess);
    }

    [Fact]
    public void Invalid_action_completion_cannot_replace_confirmed_template_or_ordinary_values()
    {
        var template = new EntryTemplateDeclaration("main", [new("main", new("label", TemplateNodeKind.Text, Value: new(StateFieldId: "name")))], [new("name", TemplateValueKind.Text)]);
        var store = new BrokerStateStore(["app"]);
        var reference = new TemplateEntryReference("group", TemplateEntryKind.Component, "component");
        var state = new ApplicationState(0, [new("group", "component", "confirmed", 7)], TemplateEntries: [new(reference, [new("name", new(TemplateValueKind.Text, Text: "old"))])]);
        ProtocolMessage Message(MessageKind kind) => new() { Kind = kind, ApplicationId = "app", SessionId = "session" };
        store.Handle(Message(MessageKind.Welcome));
        Assert.True(store.Handle(Message(MessageKind.Declare) with { Declaration = Declaration(template), State = state }).Result!.Accepted);
        var old = store.GetSnapshot("app")!.State!;
        var rejected = store.ApplyActionState("app", "session", new(1, [new("group", "component", "bad", 999)], TemplateEntries: [new(reference, [])]));
        Assert.False(rejected.Accepted);
        Assert.Same(old, store.GetSnapshot("app")!.State);
        Assert.Equal(7, store.GetSnapshot("app")!.State!.Components[0].Number);
        Assert.True(store.ApplyActionState("app", "session", state with { Revision = 1 }).Accepted);
        Assert.False(store.ApplyActionState("app", "old-session", state with { Revision = 2 }).Accepted);
    }

    [Fact]
    public async Task Large_offset_slider_normalization_round_trips_through_confirmation_but_adjacent_values_do_not()
    {
        var template = new EntryTemplateDeclaration("main", [new("main", new("slider", TemplateNodeKind.Slider,
            Value: new(StateFieldId: "value"), AccessibleName: "Value", Action: new(TemplateActionKind.Business, "slide"),
            Minimum: 1_000_000_000, Maximum: 1_000_000_001, Step: 0.1))], [new("value", TemplateValueKind.Number)]);
        var store = new BrokerStateStore(["app"]);
        var entry = new TemplateEntryReference("group", TemplateEntryKind.Component, "component");
        ApplicationState State(long revision, double value) => new(revision, [], TemplateEntries:
            [new(entry, [new("value", new(TemplateValueKind.Number, Number: value))])]);
        ProtocolMessage Message(MessageKind kind) => new() { Kind = kind, ApplicationId = "app", SessionId = "session" };
        store.Handle(Message(MessageKind.Welcome));
        Assert.True(store.Handle(Message(MessageKind.Declare) with { Declaration = Declaration(template), State = State(0, 1_000_000_000) }).Result!.Accepted);
        double? sent = null;
        using var controller = new TemplateInteractionController(store, "app", entry, (_, parameter, session, _) =>
        {
            sent = parameter.Number;
            return Task.FromResult(store.ApplyActionState("app", session, State(1, parameter.Number!.Value)));
        });
        var result = await controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 1_000_000_000.1));
        Assert.True(result.Accepted, result.Code);
        Assert.Equal(1_000_000_000.1, sent);
        Assert.Equal(1_000_000_000.1, Assert.Single(controller.GetSnapshot()!.Nodes).Confirmed!.Number);
        var confirmed = store.GetSnapshot("app")!.State!;
        foreach (var invalid in new[] { Math.BitIncrement(sent!.Value), Math.BitDecrement(sent.Value), 1_000_000_000.15 })
        {
            Assert.False(store.ApplyActionState("app", "session", State(2, invalid)).Accepted);
            Assert.Same(confirmed, store.GetSnapshot("app")!.State);
        }
    }

    private static ApplicationDeclaration Declaration(EntryTemplateDeclaration template) => new("app",
        [new("group", [new("component", [new("run"), new("toggle", ActionParameterKind.Boolean), new("slide", ActionParameterKind.Number)], Template: template)],
            [new("panel", [new("open")])])]);
}

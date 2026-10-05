using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class ItemActivationTests
{
    [Theory]
    [InlineData((ItemActivationKind)99, null)]
    [InlineData(ItemActivationKind.None, "activate")]
    [InlineData(ItemActivationKind.ToggleSelf, "activate")]
    [InlineData(ItemActivationKind.ToggleDeclaredTargets, "")]
    [InlineData(ItemActivationKind.BusinessAction, null)]
    [InlineData(ItemActivationKind.TaskbarFlyout, " ")]
    public void A_binding_has_one_supported_outcome_and_only_its_required_target(ItemActivationKind kind, string? target)
    {
        var result = new DeclarationValidator().Validate(Declaration(new(kind, target)));
        Assert.False(result.IsSuccess);
        Assert.Equal("dynamic_structure_invalid", result.Error!.Code);
    }

    [Fact]
    public void Business_binding_cannot_target_an_undeclared_component_action()
    {
        var declaration = Declaration(new(ItemActivationKind.BusinessAction, "not-declared"));
        var result = new DeclarationValidator().Validate(declaration);
        Assert.False(result.IsSuccess);
        Assert.Equal("dynamic_reference_invalid", result.Error!.Code);
    }

    [Fact]
    public void Controls_are_bounded_unique_valid_and_frozen_within_shared_node_budget()
    {
        ItemControlActivation control = new(ItemControlKind.PrimaryButton, new(ItemActivationKind.ToggleSelf));
        foreach (var invalid in new ItemControlActivation[][]
        {
            [control, control], [control, control, control], [new((ItemControlKind)99, control.Binding)],
            [new(ItemControlKind.PrimaryButton, null!)], [null!],
            [new(ItemControlKind.PrimaryButton, new(ItemActivationKind.ToggleSelf, "details"))]
        }) Assert.False(new DeclarationValidator().Validate(Declaration(controls: invalid)).IsSuccess);

        var controls = new[] { control };
        var declaration = Declaration(new(ItemActivationKind.None), controls);
        var result = new DeclarationValidator().Validate(declaration);
        Assert.True(result.IsSuccess);
        controls[0] = new(ItemControlKind.SecondaryButton, new(ItemActivationKind.BusinessAction, "activate"));
        var frozen = Assert.Single(result.Value!.DynamicContents).Declaration;
        Assert.Equal(ItemControlKind.PrimaryButton, Assert.Single(frozen.Structures[0].ControlActivations!).Control);
        int budget = 7; // Existing declaration without binding nodes already needs seven nodes.
        var tooSmall = new DynamicContentValidator().ValidateDeclaration(frozen,
            result.Value.DynamicContents[0].ComponentIdentity, ref budget);
        Assert.False(tooSmall.IsSuccess);
        Assert.Equal("dynamic_budget_exceeded", tooSmall.Error!.Code);
        Assert.Equal(7, budget);
    }

    [Fact]
    public void Local_activation_toggles_immediately_without_dispatching_business_or_flyout()
    {
        var fixture = new Fixture(Declaration(new(ItemActivationKind.ToggleSelf)));
        var result = fixture.Router.Activate(fixture.Handle, ItemActivationSource.BlankPrimary);
        Assert.True(result.Result.Accepted);
        Assert.True(result.Handled);
        Assert.True(Assert.Single(result.Items!).Expanded);
        Assert.Null(result.Action);
        Assert.Null(result.TaskbarFlyoutId);
        Assert.False(Assert.Single(fixture.Router.Activate(fixture.Handle, ItemActivationSource.BlankPrimary).Items!).Expanded);
    }

    [Fact]
    public void Internal_business_control_returns_its_own_typed_slot_and_never_bubbles_to_primary_toggle()
    {
        var fixture = new Fixture(Declaration(new(ItemActivationKind.ToggleSelf),
            [new(ItemControlKind.PrimaryButton, new(ItemActivationKind.BusinessAction, "activate"))]));
        var result = fixture.Router.Activate(fixture.Handle, ItemActivationSource.PrimaryButton);
        Assert.True(result.Result.Accepted);
        Assert.Equal(new ActionSlotReference("app", "main", ActionEntryKind.Component, "component", "activate"), result.Action);
        Assert.Equal("session", result.SessionId);
        Assert.Equal(fixture.Handle, result.Origin);
        Assert.Equal(ActionParameterKind.Number, fixture.Store.GetSnapshot("app")!.Declaration!.ActionSlots.First().ParameterKind);
        Assert.Null(result.Items);
        Assert.Null(result.TaskbarFlyoutId);
        Assert.True(result.Handled);
        var outer = fixture.Router.Activate(fixture.Handle, ItemActivationSource.BlankPrimary, result.Handled);
        Assert.True(outer.Handled);
        Assert.Null(outer.Action);
        Assert.False(Assert.Single(fixture.Items.GetPresentation("screen")).Expanded);
        fixture.Store.Handle(fixture.Message(MessageKind.Disconnected));
        Assert.False(fixture.Router.Activate(fixture.Handle, ItemActivationSource.PrimaryButton).Result.Accepted);
    }

    [Fact]
    public void Flyout_binding_returns_only_its_declared_same_group_panel()
    {
        var fixture = new Fixture(Declaration(new(ItemActivationKind.TaskbarFlyout, "details")));
        var result = fixture.Router.Activate(fixture.Handle, ItemActivationSource.BlankPrimary);
        Assert.True(result.Result.Accepted);
        Assert.Equal("details", result.TaskbarFlyoutId);
        Assert.Equal("session", result.SessionId);
        Assert.Equal(fixture.Handle, result.Origin);
        Assert.Null(result.Action);
        Assert.Null(result.Items);
        var invalid = new DeclarationValidator().Validate(Declaration(new(ItemActivationKind.TaskbarFlyout, "other-group-panel")));
        Assert.False(invalid.IsSuccess);
        Assert.Equal("dynamic_reference_invalid", invalid.Error!.Code);
    }

    [Fact]
    public void Unknown_source_is_rejected_but_consumed_and_missing_or_none_controls_never_bubble()
    {
        var fixture = new Fixture(Declaration(new(ItemActivationKind.ToggleSelf),
            [new(ItemControlKind.SecondaryButton, new(ItemActivationKind.None))]));
        var unknown = fixture.Router.Activate(fixture.Handle, (ItemActivationSource)99);
        Assert.False(unknown.Result.Accepted);
        Assert.Equal("InvalidActivationSource", unknown.Result.Code);
        Assert.True(unknown.Handled);
        foreach (var source in new[] { ItemActivationSource.PrimaryButton, ItemActivationSource.SecondaryButton })
        {
            var inner = fixture.Router.Activate(fixture.Handle, source);
            Assert.True(inner.Handled);
            Assert.True(inner.Result.Accepted);
            Assert.Null(inner.Action);
            Assert.Null(inner.TaskbarFlyoutId);
            Assert.Null(inner.Items);
            fixture.Router.Activate(fixture.Handle, ItemActivationSource.BlankPrimary, inner.Handled);
            Assert.False(Assert.Single(fixture.Items.GetPresentation("screen")).Expanded);
        }
        var handled = fixture.Router.Activate(null!, (ItemActivationSource)99, true);
        Assert.True(handled.Result.Accepted);
        Assert.True(handled.Handled);
    }

    [Fact]
    public void Batch_binding_uses_declared_targets_and_stale_handles_cannot_dispatch()
    {
        var declaration = Declaration(new(ItemActivationKind.ToggleDeclaredTargets));
        var fixture = new Fixture(declaration);
        Assert.True(Assert.Single(fixture.Router.Activate(fixture.Handle, ItemActivationSource.BlankPrimary).Items!).Expanded);
        Assert.False(Assert.Single(fixture.Router.Activate(fixture.Handle, ItemActivationSource.BlankPrimary).Items!).Expanded);
        var old = fixture.Handle;
        fixture.Store.Handle(fixture.Message(MessageKind.Welcome) with { SessionId = "next" });
        var stale = fixture.Router.Activate(old, ItemActivationSource.BlankPrimary);
        Assert.False(stale.Result.Accepted);
        Assert.Equal("StaleItem", stale.Result.Code);
        Assert.True(stale.Handled);
        Assert.Null(stale.Action);
        fixture.Items.Close();
        Assert.False(fixture.Router.Activate(fixture.HandleOr(old), ItemActivationSource.BlankPrimary).Result.Accepted);
    }

    [Theory]
    [InlineData(ItemActivationKind.BusinessAction, "remote-action")]
    [InlineData(ItemActivationKind.TaskbarFlyout, "remote-panel")]
    public void Bindings_cannot_escape_the_component_or_feature_group(ItemActivationKind kind, string target)
    {
        var declaration = Declaration(new(kind, target));
        var main = declaration.FeatureGroups![0];
        declaration = declaration with
        {
            FeatureGroups = [main with { Components = [.. main.Components!,
            new("other-component", [new("remote-action")])] },
            new("other-group", [new("other", [new("remote-action")])], [new("remote-panel", [new("open")])])]
        };
        var result = new DeclarationValidator().Validate(declaration);
        Assert.False(result.IsSuccess);
        Assert.Equal("dynamic_reference_invalid", result.Error!.Code);
    }

    [Fact]
    public void Business_activation_requires_a_ready_current_declaration()
    {
        var fixture = new Fixture(Declaration(new(ItemActivationKind.BusinessAction, "activate")));
        fixture.Store.RequireSessionReady("app");
        var pending = fixture.Router.Activate(fixture.Handle, ItemActivationSource.BlankPrimary);
        Assert.False(pending.Result.Accepted);
        Assert.Null(pending.Action);
        Assert.Equal("ActionNotAvailable", pending.Result.Code);
    }

    private sealed class Fixture
    {
        public BrokerStateStore Store { get; } = new(["app"]);
        public ItemPresentationController Items { get; }
        public ItemActivationRouter Router { get; }
        public ItemInteractionHandle Handle => Items.GetPresentation("screen")[0].Handle;
        public ItemInteractionHandle HandleOr(ItemInteractionHandle fallback) => Items.GetPresentation("screen").FirstOrDefault()?.Handle ?? fallback;
        public Fixture(ApplicationDeclaration declaration)
        {
            Assert.True(Store.Handle(Message(MessageKind.Welcome)).Result!.Accepted);
            Assert.True(Store.Handle(Message(MessageKind.Declare) with
            {
                Declaration = declaration,
                State = new(0, [], [new("main", "component", new([], [new("one", "row", [], new(Status: new("value")))]))])
            }).Result!.Accepted);
            Items = new(Store);
            Items.UpdateScreens(["screen"]);
            Router = new(Items, Store);
        }
        public ProtocolMessage Message(MessageKind kind) => new() { Kind = kind, ApplicationId = "app", SessionId = "session" };
    }

    private static ApplicationDeclaration Declaration(ItemActivationBinding? primary = null,
        IReadOnlyList<ItemControlActivation>? controls = null)
    {
        var normal = new ItemPresentation(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small));
        var expanded = normal with { Width = new(WidthTier.Large) };
        var structure = new ItemStructureDeclaration("row", true, normal, expanded,
            ExpandTargetStructureIds: ["row"], PrimaryActivation: primary, ControlActivations: controls);
        return new("app", [new("main", [new("component", [new("activate", ActionParameterKind.Number)],
            new(DynamicContentKind.OrdinaryItems, [structure]))], [new("details", [new("open")])])]);
    }
}

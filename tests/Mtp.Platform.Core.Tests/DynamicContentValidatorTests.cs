using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class DynamicContentValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly StableIdentity Entry = new StableIdentity(new StableId("app"))
        .CreateChild(new("main")).CreateChild(new("widget"));

    [Fact]
    public void OrdinaryRepeatedProgressHasValidatedStructureAndIndependentStableItems()
    {
        var validator = new DynamicContentValidator();
        var budget = 100;
        var declaration = validator.ValidateDeclaration(Ordinary(), Entry, ref budget);
        Assert.True(declaration.IsSuccess);
        Assert.Equal(Entry, declaration.Value!.ComponentIdentity);
        Assert.Equal(96, budget);
        var state = validator.ValidateState(declaration.Value, new([], [ProgressItem("left"), ProgressItem("right")]), Now);
        Assert.True(state.IsSuccess);
        Assert.Equal(new[] { "left", "right" }, state.Value!.Content.Items.Select(item => item.ItemId));
    }

    private static DynamicContentDeclaration Ordinary() => new(DynamicContentKind.OrdinaryItems,
        [new("progress", true, new(PresetTemplate.Progress, ContentFields.Progress, new(WidthTier.Small)))]);

    [Theory]
    [InlineData(PresetTemplate.Counter, PresetVariant.Text, ContentFields.Counter, true)]
    [InlineData(PresetTemplate.Counter, PresetVariant.Ring, ContentFields.Counter | ContentFields.Progress, true)]
    [InlineData(PresetTemplate.Counter, PresetVariant.Bar, ContentFields.Counter | ContentFields.Progress, true)]
    [InlineData(PresetTemplate.Counter, PresetVariant.Ring, ContentFields.Counter, false)]
    [InlineData(PresetTemplate.Counter, PresetVariant.Default, ContentFields.Counter | ContentFields.Progress, false)]
    [InlineData(PresetTemplate.Progress, PresetVariant.Bar, ContentFields.Progress, true)]
    [InlineData(PresetTemplate.Progress, PresetVariant.Text, ContentFields.Progress, false)]
    [InlineData(PresetTemplate.Status, PresetVariant.Text, ContentFields.Status, true)]
    [InlineData(PresetTemplate.Status, PresetVariant.Ring, ContentFields.Status, false)]
    [InlineData(PresetTemplate.Timer, PresetVariant.Ring, ContentFields.Timer, true)]
    [InlineData(PresetTemplate.Timer, PresetVariant.Text, ContentFields.Timer, true)]
    [InlineData(PresetTemplate.Timer, PresetVariant.Bar, ContentFields.Timer, false)]
    [InlineData(PresetTemplate.Composite, PresetVariant.Text, ContentFields.Counter | ContentFields.Status, true)]
    [InlineData(PresetTemplate.Composite, PresetVariant.Ring, ContentFields.Counter | ContentFields.Progress, true)]
    [InlineData(PresetTemplate.Composite, PresetVariant.Bar, ContentFields.Timer | ContentFields.Progress | ContentFields.Status, true)]
    [InlineData(PresetTemplate.Composite, PresetVariant.Bar, ContentFields.Counter | ContentFields.Status, false)]
    [InlineData(PresetTemplate.Composite, PresetVariant.Default, ContentFields.Status, false)]
    [InlineData(PresetTemplate.Counter, (PresetVariant)99, ContentFields.Counter, false)]
    public void PresetVariantsOnlyAdmitExplicitFieldSemantics(PresetTemplate template, PresetVariant variant, ContentFields fields, bool accepted)
    {
        var budget = 100;
        var result = new DynamicContentValidator().ValidateDeclaration(new(DynamicContentKind.OrdinaryItems,
            [new("variant", false, new(template, fields, new(WidthTier.Large), variant))]), Entry, ref budget);
        Assert.Equal(accepted, result.IsSuccess);
        if (!accepted) Assert.Equal(100, budget);
    }

    private static DynamicItemState ProgressItem(string id) => new(id, "progress", [],
        new(Progress: new(ProgressMode.Determinate, 2, 5)));

    [Fact]
    public void DeclarationValidatesExpansionTargetsAndFreezesTheirCollections()
    {
        var targets = new List<string> { "progress" };
        var structures = new List<ItemStructureDeclaration>
        {
            Ordinary().Structures[0] with
            {
                Expanded = new(PresetTemplate.Progress, ContentFields.Progress, new(Slots: 8)),
                InitiallyExpanded = true, ExpandTargetStructureIds = targets
            }
        };
        var remaining = 7;
        var result = new DynamicContentValidator().ValidateDeclaration(new(DynamicContentKind.OrdinaryItems, structures), Entry, ref remaining);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, remaining);
        targets.Clear(); structures.Clear();
        var structure = Assert.Single(result.Value!.Declaration.Structures);
        Assert.Equal("progress", Assert.Single(structure.ExpandTargetStructureIds!));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)structure.ExpandTargetStructureIds!).Clear());
    }

    public static IEnumerable<object[]> InvalidDeclarations()
    {
        var valid = Ordinary();
        var item = valid.Structures[0];
        yield return [valid with { Kind = (DynamicContentKind)90 }];
        yield return [valid with { Grouping = (DynamicGrouping)90 }];
        yield return [valid with { Structures = [] }];
        yield return [valid with { Structures = [item, item] }];
        yield return [valid with { Structures = [item with { StructureId = " " }] }];
        yield return [valid with { Structures = [item with { Normal = item.Normal with { Template = (PresetTemplate)99 } }] }];
        yield return [valid with { Structures = [item with { Normal = item.Normal with { Fields = ContentFields.Counter } }] }];
        yield return [valid with { Structures = [item with { Normal = item.Normal with { Width = new() } }] }];
        yield return [valid with { Structures = [item with { Normal = item.Normal with { Width = new(WidthTier.Small, 2) } }] }];
        yield return [valid with { Structures = [item with { Normal = item.Normal with { Width = new(Slots: 9) } }] }];
        yield return [valid with { Structures = [item with { InitiallyExpanded = true }] }];
        yield return [valid with { Structures = [item with { ExpandTargetStructureIds = ["unknown"] }] }];
        yield return [valid with { Structures = [item with { ExpandTargetStructureIds = ["progress"] }] }];
        yield return [valid with { Structures = [item with { Animation = (SemanticAnimation)99 }] }];
        yield return [valid with { Structures = [item with { Overflow = (TextOverflow)99 }] }];
        yield return [valid with { Structures = [item with { Normal = new(PresetTemplate.Composite, ContentFields.Status, new(WidthTier.Small)) }] }];
    }

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public void InvalidStructureDoesNotConsumeSharedNodeBudget(DynamicContentDeclaration declaration)
    {
        var remaining = 4096;
        var result = new DynamicContentValidator().ValidateDeclaration(declaration, Entry, ref remaining);
        Assert.False(result.IsSuccess);
        Assert.Equal(4096, remaining);
        Assert.NotNull(result.Error!.Path);
    }

    [Fact]
    public void StructureCountAndSharedNodeBudgetIncludeEndpoint()
    {
        var validator = new DynamicContentValidator();
        var structures = Enumerable.Range(0, 16).Select(i => Ordinary().Structures[0] with { StructureId = "s" + i }).ToArray();
        var exact = 49;
        Assert.True(validator.ValidateDeclaration(new(DynamicContentKind.OrdinaryItems, structures), Entry, ref exact).IsSuccess);
        Assert.Equal(0, exact);
        var insufficient = 48;
        Assert.False(validator.ValidateDeclaration(new(DynamicContentKind.OrdinaryItems, structures), Entry, ref insufficient).IsSuccess);
        Assert.Equal(48, insufficient);
        var budget = 4096;
        Assert.False(validator.ValidateDeclaration(new(DynamicContentKind.OrdinaryItems, structures.Append(structures[0] with { StructureId = "extra" }).ToArray()), Entry, ref budget).IsSuccess);
    }

    private static ValidatedDynamicContentDeclaration Validated(bool live = true)
    {
        var budget = 4096;
        var declaration = live ? Ordinary() with { Kind = DynamicContentKind.LiveIsland } : Ordinary();
        return new DynamicContentValidator().ValidateDeclaration(declaration, Entry, ref budget).Value!;
    }

    private static DynamicContentState LiveState() => new(
        [new("activity", Now.AddHours(1))], [ProgressItem("item") with { ActivityIds = ["activity"] }]);

    [Fact]
    public void ActivitiesAndItemsHaveSeparateIdentitiesAndFrozenReferences()
    {
        var activityIds = new List<string> { "same", "other" };
        var activities = new List<ActivityState> { new("same", Now.AddHours(24)), new("other", Now.AddSeconds(1)) };
        var items = new List<DynamicItemState> { ProgressItem("same") with { ActivityIds = activityIds } };
        var result = new DynamicContentValidator().ValidateState(Validated(), new(activities, items), Now);
        Assert.True(result.IsSuccess);
        activities.Clear(); items.Clear(); activityIds.Clear();
        Assert.Equal(2, result.Value!.Content.Activities.Count);
        Assert.Equal(new[] { "same", "other" }, Assert.Single(result.Value.Content.Items).ActivityIds);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Value.Content.Items[0].ActivityIds).Clear());
    }

    public static IEnumerable<object[]> InvalidLiveStates()
    {
        var valid = LiveState();
        var item = valid.Items[0];
        yield return [valid with { Activities = [new("activity", Now)] }, "dynamic_expiry_invalid"];
        yield return [valid with { Activities = [new("activity", Now.AddHours(24).AddTicks(1))] }, "dynamic_expiry_invalid"];
        yield return [valid with { Activities = [valid.Activities[0], valid.Activities[0]] }, "dynamic_duplicate_identity"];
        yield return [valid with { Activities = [new(" ", Now.AddHours(1))] }, "dynamic_identity_invalid"];
        yield return [valid with { Items = [item, item] }, "dynamic_duplicate_identity"];
        yield return [valid with { Items = [item with { ItemId = "" }] }, "dynamic_identity_invalid"];
        yield return [valid with { Items = [item with { StructureId = "not-declared" }] }, "dynamic_structure_invalid"];
        yield return [valid with { Items = [item with { ActivityIds = ["other-entry-activity"] }] }, "dynamic_reference_invalid"];
        yield return [valid with { Items = [item with { ActivityIds = ["activity", "activity"] }] }, "dynamic_reference_invalid"];
        yield return [valid with { Items = [item with { ActivityIds = [] }] }, "dynamic_reference_invalid"];
        yield return [valid with { Activities = [new("activity", Now.AddHours(1), Ended: true)] }, "dynamic_reference_invalid"];
    }

    [Theory]
    [MemberData(nameof(InvalidLiveStates))]
    public void InvalidActivityOrItemRejectsWholeEntry(DynamicContentState state, string code)
    {
        var result = new DynamicContentValidator().ValidateState(Validated(), state, Now);
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public void DynamicCollectionBudgetsAndRetentionIncludeTheirUpperEndpoints()
    {
        var validator = new DynamicContentValidator();
        var declaration = Validated();
        var activities = Enumerable.Range(0, 64).Select(i => new ActivityState("a" + i, Now.AddHours(24))).ToArray();
        var references = activities.Take(8).Select(a => a.ActivityId).ToArray();
        var items = Enumerable.Range(0, 128).Select(i => ProgressItem("i" + i) with { ActivityIds = references }).ToArray();
        Assert.True(validator.ValidateState(declaration, new(activities, items), Now).IsSuccess);
        Assert.Equal("dynamic_budget_exceeded", validator.ValidateState(declaration,
            new(activities.Append(new("extra", Now.AddHours(1))).ToArray(), items), Now).Error!.Code);
        Assert.Equal("dynamic_budget_exceeded", validator.ValidateState(declaration,
            new(activities, items.Append(ProgressItem("extra")).ToArray()), Now).Error!.Code);
        Assert.Equal("dynamic_budget_exceeded", validator.ValidateState(declaration,
            new(activities, [items[0] with { ActivityIds = activities.Take(9).Select(a => a.ActivityId).ToArray() }]), Now).Error!.Code);
    }

    [Fact]
    public void FixedIdentityMustComeFromStructureAndOrdinaryContentCannotSmuggleActivities()
    {
        var budget = 100;
        var validator = new DynamicContentValidator();
        var dto = Ordinary();
        var fixedDeclaration = validator.ValidateDeclaration(dto with
        {
            Structures = [dto.Structures[0] with { IsRepeated = false }]
        }, Entry, ref budget).Value!;
        Assert.True(validator.ValidateState(fixedDeclaration, new([], [ProgressItem("progress")]), Now).IsSuccess);
        Assert.Equal("dynamic_identity_invalid", validator.ValidateState(fixedDeclaration,
            new([], [ProgressItem("arbitrary")]), Now).Error!.Code);
        Assert.False(validator.ValidateState(Validated(false), LiveState(), Now).IsSuccess);
        Assert.True(validator.ValidateState(Validated(), new([new("ended", Now.AddSeconds(-1), Ended: true)], []), Now).IsSuccess);
    }

    private static ValidatedDynamicContentDeclaration Composite(ContentFields fields)
    {
        var budget = 4096;
        return new DynamicContentValidator().ValidateDeclaration(new(DynamicContentKind.OrdinaryItems,
            [new("mixed", true, new(PresetTemplate.Composite, fields, new(Slots: 2)))]), Entry, ref budget).Value!;
    }

    [Fact]
    public void CompositeKeepsTimerProgressCountAndStatusAsIndependentTypedValues()
    {
        var declaration = Composite(ContentFields.Timer | ContentFields.Progress | ContentFields.Counter | ContentFields.Status);
        var fields = new DynamicItemFields(new(TimerDirection.CountDown, Now, 5000, IsPaused: true, ShowOvertime: true),
            new(ProgressMode.Indeterminate), new(CounterSemantics.CurrentIndex, 4, 10), new("处理中", StatusMarker.Attention));
        var result = new DynamicContentValidator().ValidateState(declaration, new([], [new("item", "mixed", [], fields)]), Now);
        Assert.True(result.IsSuccess);
        var confirmed = Assert.Single(result.Value!.Content.Items).Fields;
        Assert.True(confirmed.Timer!.IsPaused);
        Assert.Equal(5000, confirmed.Timer.ValueMillisecondsAtReference);
        Assert.Null(confirmed.Progress!.Value);
        Assert.Equal(CounterSemantics.CurrentIndex, confirmed.Counter!.Semantics);
        Assert.Equal(4, confirmed.Counter.Value);
        var adjusted = fields with { Timer = new(TimerDirection.CountDown, Now.AddSeconds(-1), -1000, ShowOvertime: true) };
        Assert.True(new DynamicContentValidator().ValidateState(declaration,
            new([], [new("item", "mixed", [], adjusted)]), Now).IsSuccess);
    }

    public static IEnumerable<object[]> InvalidFields()
    {
        yield return [new DynamicItemFields(), ContentFields.Progress];
        yield return [new DynamicItemFields(Progress: new(ProgressMode.Determinate, 1, 0)), ContentFields.Progress];
        yield return [new DynamicItemFields(Progress: new(ProgressMode.Determinate, -1, 10)), ContentFields.Progress];
        yield return [new DynamicItemFields(Progress: new(ProgressMode.Determinate, 11, 10)), ContentFields.Progress];
        yield return [new DynamicItemFields(Progress: new(ProgressMode.Determinate, double.NaN, 10)), ContentFields.Progress];
        yield return [new DynamicItemFields(Progress: new(ProgressMode.Determinate, 1, double.PositiveInfinity)), ContentFields.Progress];
        yield return [new DynamicItemFields(Progress: new(ProgressMode.Indeterminate, 1, 10)), ContentFields.Progress];
        yield return [new DynamicItemFields(Progress: new((ProgressMode)99)), ContentFields.Progress];
        yield return [new DynamicItemFields(Progress: new(ProgressMode.Determinate, 1, 10), Status: new("extra")), ContentFields.Progress];
        yield return [new DynamicItemFields(Counter: new(CounterSemantics.CompletedCount, -1)), ContentFields.Counter];
        yield return [new DynamicItemFields(Counter: new(CounterSemantics.CurrentIndex, 5, 4)), ContentFields.Counter];
        yield return [new DynamicItemFields(Counter: new((CounterSemantics)99, 1)), ContentFields.Counter];
        yield return [new DynamicItemFields(Status: new(new string('x', 1025))), ContentFields.Status];
        yield return [new DynamicItemFields(Status: new(null!)), ContentFields.Status];
        yield return [new DynamicItemFields(Status: new("x", (StatusMarker)99)), ContentFields.Status];
        yield return [new DynamicItemFields(Timer: new(TimerDirection.CountUp, Now, double.NaN)), ContentFields.Timer];
        yield return [new DynamicItemFields(Timer: new(TimerDirection.CountUp, Now, -1)), ContentFields.Timer];
        yield return [new DynamicItemFields(Timer: new(TimerDirection.CountDown, Now, -1)), ContentFields.Timer];
        yield return [new DynamicItemFields(Timer: new(TimerDirection.CountUp, default, 1)), ContentFields.Timer];
        yield return [new DynamicItemFields(Timer: new((TimerDirection)99, Now, 1)), ContentFields.Timer];
        foreach (var duration in new[] { 0d, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
                     TimeSpan.MaxValue.TotalMilliseconds + 1 })
            yield return [new DynamicItemFields(Timer: new(TimerDirection.CountUp, Now, 1,
                ProgressDurationMilliseconds: duration)), ContentFields.Timer];
    }

    [Fact]
    public void ExplicitTimerProgressDurationAcceptsFinitePositiveEndpointsAndRemainsOptional()
    {
        var validator = new DynamicContentValidator();
        var budget = 100;
        var declaration = validator.ValidateDeclaration(new(DynamicContentKind.OrdinaryItems,
            [new("timer", false, new(PresetTemplate.Timer, ContentFields.Timer, new(WidthTier.Medium)))]),
            Entry, ref budget).Value!;
        foreach (double? duration in new double?[] { null, double.Epsilon, 5000, TimeSpan.MaxValue.TotalMilliseconds })
        {
            var basis = new TimerBasis(TimerDirection.CountDown, Now, 5000, ProgressDurationMilliseconds: duration);
            var result = validator.ValidateState(declaration, new([], [new("timer", "timer", [], new(Timer: basis))]), Now);
            Assert.True(result.IsSuccess);
            Assert.Equal(duration, Assert.Single(result.Value!.Content.Items).Fields.Timer!.ProgressDurationMilliseconds);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public void TypedFieldMismatchOrInvalidValueRejectsSnapshot(DynamicItemFields fields, ContentFields expected)
    {
        var template = expected switch
        {
            ContentFields.Timer => PresetTemplate.Timer,
            ContentFields.Counter => PresetTemplate.Counter,
            ContentFields.Status => PresetTemplate.Status,
            _ => PresetTemplate.Progress
        };
        var budget = 100;
        var validator = new DynamicContentValidator();
        var declaration = validator.ValidateDeclaration(new(DynamicContentKind.OrdinaryItems,
            [new("typed", false, new(template, expected, new(WidthTier.Small)))]), Entry, ref budget).Value!;
        var result = validator.ValidateState(declaration, new([], [new("typed", "typed", [], fields)]), Now);
        Assert.False(result.IsSuccess);
        Assert.Equal("dynamic_fields_invalid", result.Error!.Code);
    }

    [Fact]
    public void CurrentIndexWithoutIndependentProgressDoesNotAcquireRatio()
    {
        var result = new DynamicContentValidator().ValidateState(Composite(ContentFields.Counter | ContentFields.Status),
            new([], [new("i", "mixed", [], new(Counter: new(CounterSemantics.CurrentIndex, 3, 8), Status: new("第三项")))]), Now);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Content.Items[0].Fields.Progress);
    }

    [Theory]
    [InlineData("Xaml")]
    [InlineData("PixelWidth")]
    [InlineData("AnimationDuration")]
    public void ArbitraryUiCoordinateAndAnimationFieldsCannotEnterJsonDeclaration(string property)
    {
        var declaration = new ApplicationDeclaration("app", [new("main",
            [new("widget", [new("open")], Ordinary())], [new("panel", [new("open")])])]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(declaration))!;
        node["FeatureGroups"]![0]!["Components"]![0]!["DynamicContent"]!["Structures"]![0]![property] = "uncontrolled";
        var result = new DeclarationValidator().ValidateJson(node.ToJsonString());
        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported_structure", result.Error!.Code);
    }

    [Fact]
    public void NormalAndExpandedFieldsMustBothBeAvailableWithoutChangingStructure()
    {
        var budget = 100;
        var declaration = new DynamicContentValidator().ValidateDeclaration(new(DynamicContentKind.OrdinaryItems,
            [new("fixed", false, new(PresetTemplate.Counter, ContentFields.Counter, new(Slots: 1)),
                new(PresetTemplate.Composite, ContentFields.Counter | ContentFields.Status, new(Slots: 8)))]), Entry, ref budget).Value!;
        var state = new DynamicContentState([], [new("fixed", "fixed", [],
            new(Counter: new(CounterSemantics.CompletedCount, 0, 10), Status: new(new string('x', 1024))))]);
        Assert.True(new DynamicContentValidator().ValidateState(declaration, state, Now).IsSuccess);
        Assert.False(new DynamicContentValidator().ValidateState(declaration,
            state with { Items = [state.Items[0] with { Fields = new(Counter: new(CounterSemantics.CompletedCount, 0, 10)) }] }, Now).IsSuccess);
    }
}

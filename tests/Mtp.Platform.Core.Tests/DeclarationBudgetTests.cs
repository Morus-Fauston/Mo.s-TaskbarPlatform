using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class DeclarationBudgetTests
{
    [Fact]
    public void MaximumIdLengthIsAcceptedWithoutTruncatingIdentity()
    {
        var id = new string('界', 256);
        var dto = Declaration() with { ApplicationId = id };
        var validator = new DeclarationValidator();
        Assert.Equal(id, validator.Validate(dto).Value!.Identity.Segments[0].Value);
        Assert.Equal(id, validator.ValidateJson(JsonSerializer.Serialize(dto)).Value!.Identity.Segments[0].Value);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("feature")]
    [InlineData("component")]
    [InlineData("flyout")]
    [InlineData("componentSlot")]
    [InlineData("flyoutSlot")]
    public void BothEntriesRejectLongFieldsAndKeepLastSnapshot(string field)
    {
        var store = new DeclarationSnapshotStore();
        var accepted = store.Submit(Declaration());
        Assert.True(accepted.IsSuccess);
        var longId = new string('x', 257);
        var slots = new[] { new ActionSlotDeclaration(field == "componentSlot" ? longId : "go") };
        var flyoutSlots = new[] { new ActionSlotDeclaration(field == "flyoutSlot" ? longId : "open") };
        var dto = new ApplicationDeclaration(field == "application" ? longId : "app",
            [new(field == "feature" ? longId : "group",
                [new(field == "component" ? longId : "widget", slots)],
                [new(field == "flyout" ? longId : "panel", flyoutSlots)])]);
        Assert.Equal("declaration_budget_exceeded", store.Submit(dto).Error?.Code);
        Assert.Same(accepted.Value, store.Current);
        Assert.Equal("declaration_budget_exceeded", store.SubmitJson(JsonSerializer.Serialize(dto)).Error?.Code);
        Assert.Same(accepted.Value, store.Current);
    }

    [Theory]
    [InlineData("groups", 64)]
    [InlineData("components", 128)]
    [InlineData("flyouts", 128)]
    [InlineData("componentSlots", 32)]
    [InlineData("flyoutSlots", 32)]
    public void BothEntriesEnforceCollectionLimits(string collection, int limit)
    {
        ApplicationDeclaration Make(int count)
        {
            var basis = Declaration();
            var group = basis.FeatureGroups![0];
            var slots = Enumerable.Range(0, count).Select(i => new ActionSlotDeclaration($"s{i}")).ToArray();
            return collection switch
            {
                "groups" => basis with { FeatureGroups = Enumerable.Range(0, count).Select(i => group with { FeatureGroupId = $"g{i}" }).ToArray() },
                "components" => basis with { FeatureGroups = [group with { Components = Enumerable.Range(0, count).Select(i => new ComponentDeclaration($"c{i}", [new("go")])).ToArray() }] },
                "flyouts" => basis with { FeatureGroups = [group with { TaskbarFlyouts = Enumerable.Range(0, count).Select(i => new TaskbarFlyoutDeclaration($"f{i}", [new("go")])).ToArray() }] },
                "componentSlots" => basis with { FeatureGroups = [group with { Components = [new("widget", slots)] }] },
                _ => basis with { FeatureGroups = [group with { TaskbarFlyouts = [new("panel", slots)] }] },
            };
        }

        var validator = new DeclarationValidator();
        Assert.True(validator.Validate(Make(limit)).IsSuccess);
        Assert.True(validator.ValidateJson(JsonSerializer.Serialize(Make(limit))).IsSuccess);
        var invalid = Make(limit + 1);
        Assert.Equal("declaration_budget_exceeded", validator.Validate(invalid).Error?.Code);
        Assert.Equal("declaration_budget_exceeded", validator.ValidateJson(JsonSerializer.Serialize(invalid)).Error?.Code);
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void TotalNodeBudgetIncludesEveryLevel(int nodes, bool accepted)
    {
        // 1 application + 1 group + 124 * (1 component + 32 actions) + 1 flyout + 1 action = 4096.
        var components = Enumerable.Range(0, 124).Select(i => new ComponentDeclaration($"c{i}",
            Enumerable.Range(0, 32).Select(j => new ActionSlotDeclaration($"s{j}")).ToArray())).ToArray();
        var dto = new ApplicationDeclaration("app", [new("group", components,
            [new("panel", nodes == 4096 ? [new("open")] : [new("open"), new("other")])])]);
        var validator = new DeclarationValidator();
        Assert.Equal(accepted, validator.Validate(dto).IsSuccess);
        Assert.Equal(accepted, validator.ValidateJson(JsonSerializer.Serialize(dto)).IsSuccess);
    }

    [Fact]
    public void OversizedCollectionIsRejectedBeforeReadingElements()
    {
        var declaration = new ApplicationDeclaration("app", new OversizedGroups());
        Assert.Equal("declaration_budget_exceeded", new DeclarationValidator().Validate(declaration).Error?.Code);
    }

    private sealed class OversizedGroups : IReadOnlyList<FeatureGroupDeclaration>
    {
        public int Count => int.MaxValue;
        public FeatureGroupDeclaration this[int index] => throw new InvalidOperationException("Must check budget before reading.");
        public IEnumerator<FeatureGroupDeclaration> GetEnumerator() => throw new InvalidOperationException("Must not enumerate.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static ApplicationDeclaration Declaration() => new("app",
        [new("group", [new("widget", [new("go")])], [new("panel", [new("open")])])]);
}

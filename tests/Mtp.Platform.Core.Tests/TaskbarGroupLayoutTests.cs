using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class TaskbarGroupLayoutTests
{
    [Fact]
    public void HeterogeneousItemsKeepOrderAndOnlyPushLeftNeighborsWhenTheMiddleGrows()
    {
        var a = Component("a"); var b = Component("b"); var c = Component("c");
        var initial = TaskbarGroupLayout.Calculate([new(a, 48),
            new(b, Items: [new(Item(b, "one"), 32), new(Item(b, "two"), 64)]), new(c, 48)], 32, 100);
        Assert.Equal(220, initial.WidthDip);
        Assert.True(initial.Overflows);
        Assert.Equal(new[] { a, b, c }, initial.Components.Select(x => x.Key));
        Assert.Equal(-52, initial.Components[2].Bounds.X);
        Assert.Equal(-160, initial.Components[1].Bounds.X);
        Assert.Equal(-216, initial.Components[0].Bounds.X);
        Assert.Equal(-124, initial.Items[1].Bounds.X);
        var grown = TaskbarGroupLayout.Calculate([new(a, 48),
            new(b, Items: [new(Item(b, "one"), 64), new(Item(b, "two"), 64)]), new(c, 48)], 32, 100);
        Assert.Equal(252, grown.WidthDip);
        Assert.Equal(initial.Components[2].Bounds, grown.Components[2].Bounds);
        Assert.Equal(-192, grown.Components[1].Bounds.X);
        Assert.Equal(-248, grown.Components[0].Bounds.X);
    }

    [Theory]
    [InlineData(WidthTier.Small, false, 32)]
    [InlineData(WidthTier.Medium, false, 64)]
    [InlineData(WidthTier.Large, false, 96)]
    [InlineData(WidthTier.Small, true, 28)]
    [InlineData(WidthTier.Medium, true, 56)]
    [InlineData(WidthTier.Large, true, 84)]
    public void HostMapsDeclaredTiersWithoutReadingBusinessText(WidthTier tier, bool compact, double expected)
        => Assert.Equal(expected, DynamicWidthMetrics.Measure(new(tier), compact));

    [Fact]
    public void SlotWidthsAndInvalidDeclarationsRespectTheContract()
    {
        Assert.Equal(256, DynamicWidthMetrics.Measure(new(Slots: 8)));
        Assert.Equal(224, DynamicWidthMetrics.Measure(new(Slots: 8), true));
        Assert.Equal(32, DynamicWidthMetrics.Measure(new(Slots: 1)));
        Assert.Throws<ArgumentException>(() => DynamicWidthMetrics.Measure(new()));
        Assert.Throws<ArgumentException>(() => DynamicWidthMetrics.Measure(new(WidthTier.Small, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DynamicWidthMetrics.Measure(new(Slots: 9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DynamicWidthMetrics.Measure(new((WidthTier)99)));
    }

    [Fact]
    public void EmptyDynamicComponentsContributeNeitherPaddingNorGaps()
    {
        var empty = TaskbarGroupLayout.Calculate([new(Component("empty"), Items: [])], 32);
        Assert.Equal(0, empty.WidthDip);
        Assert.Empty(empty.Components);
        var mixed = TaskbarGroupLayout.Calculate([new(Component("a"), 240),
            new(Component("empty"), Items: []), new(Component("b"), 480)], 32);
        Assert.Equal(736, mixed.WidthDip);
        Assert.Equal(2, mixed.Components.Count);
    }

    [Fact]
    public void LayoutFreezesInputsAndKeepsTypedIdentitySegmentsDistinct()
    {
        var a = new TaskbarComponentKey("a/b", "c", "d");
        var b = new TaskbarComponentKey("a", "b/c", "d");
        var items = new[] { new TaskbarMeasuredItem(Item(a, "one"), 32) };
        var input = new[] { new TaskbarMeasuredComponent(a, Items: items), new TaskbarMeasuredComponent(b, 240) };
        var result = TaskbarGroupLayout.Calculate(input, 32);
        items[0] = new(Item(a, "replacement"), 256);
        input[0] = new(Component("replacement"), 480);
        Assert.Equal(288, result.WidthDip);
        Assert.Equal("one", Assert.Single(result.Items).Key.ItemId);
        Assert.Equal(a, result.Components[0].Key);
        Assert.Equal(b, result.Components[1].Key);
    }

    [Fact]
    public void AllAcceptedDeclarationNodesAndItemsFitWithoutSpatialTruncation()
    {
        var components = Enumerable.Range(0, TaskbarGroupLayout.MaximumComponents)
            .Select(i => new TaskbarMeasuredComponent(Component(i.ToString()), 240)).ToArray();
        var allComponents = TaskbarGroupLayout.Calculate(components, 32, 100);
        Assert.Equal(65536, allComponents.Components.Count);
        Assert.True(allComponents.Overflows);
        var key = Component("dynamic");
        var items = Enumerable.Range(0, 2048).Select(i => new TaskbarMeasuredItem(Item(key, i.ToString()), 32)).ToArray();
        var allItems = TaskbarGroupLayout.Calculate([new(key, Items: items)], 32, 100);
        Assert.Equal(2048, allItems.Items.Count);
        Assert.Equal(73732, allItems.WidthDip);
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskbarGroupLayout.Calculate(
            [new(key, Items: items.Append(new(Item(key, "extra"), 32)).ToArray())], 32));
    }

    [Fact]
    public void InvalidGeometryAndDuplicateIdentitiesAreRejectedBeforeLayout()
    {
        var key = Component("a");
        Assert.Throws<ArgumentException>(() => TaskbarGroupLayout.Calculate([new(key, double.NaN)], 32));
        Assert.Throws<ArgumentException>(() => TaskbarGroupLayout.Calculate([new(key, -1)], 32));
        Assert.Throws<ArgumentException>(() => TaskbarGroupLayout.Calculate([new(key, 32), new(key, 64)], 32));
        Assert.Throws<ArgumentException>(() => TaskbarGroupLayout.Calculate([new(key, 32, [])], 32));
        Assert.Throws<ArgumentException>(() => TaskbarGroupLayout.Calculate(
            [new(key, Items: [new(Item(Component("other"), "one"), 32)])], 32));
        Assert.Throws<ArgumentException>(() => TaskbarGroupLayout.Calculate(
            [new(key, Items: [new(Item(key, "one"), double.PositiveInfinity)])], 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskbarGroupLayout.Calculate([], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskbarGroupLayout.Calculate([], 32, -1));
    }

    [Fact]
    public void OneCurrentStableItemCannotHaveTwoSimultaneousPresenceGenerations()
    {
        var key = Component("a");
        Assert.Throws<ArgumentException>(() => TaskbarGroupLayout.Calculate(
            [new(key, Items: [new(Item(key, "one", 1), 32), new(Item(key, "one", 2), 32)])], 32));
    }

    internal static TaskbarComponentKey Component(string id) => new("app", "group", id);
    internal static TaskbarItemKey Item(TaskbarComponentKey component, string id, long generation = 1) => new(component, id, generation);
}

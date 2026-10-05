namespace Mtp.Platform.Core.Tests;

public sealed class TaskbarGroupAnimationTests
{
    [Fact]
    public void WidthItemsAndNeighborsShareTheSameMidpointAndCanReverseWithoutJumping()
    {
        var animation = new TaskbarGroupAnimation();
        var initial = Layout(32);
        animation.Retarget(initial, TimeSpan.Zero, true, animation.Generation);
        animation.Retarget(Layout(64), TimeSpan.Zero, false, animation.Generation);
        var frame = animation.Sample(TimeSpan.FromMilliseconds(110));
        Assert.Equal(148, frame.WidthDip);
        Assert.Equal(60, frame.Items[0].Bounds.Width);
        Assert.Equal(-92, frame.Items[0].Bounds.X);
        Assert.Equal(-144, frame.Components[0].Bounds.X);
        Assert.Equal(-24, frame.Components[2].Bounds.X);
        Assert.False(frame.IsComplete);
        animation.Retarget(initial, TimeSpan.FromMilliseconds(110), false, animation.Generation);
        var interrupted = animation.Sample(TimeSpan.FromMilliseconds(110));
        Assert.Equal(frame.WidthDip, interrupted.WidthDip);
        Assert.Equal(frame.Items[0].Bounds, interrupted.Items[0].Bounds);
        var final = animation.Sample(TimeSpan.FromMilliseconds(330));
        Assert.Equal(120, final.WidthDip);
        Assert.True(final.IsComplete);
    }

    [Fact]
    public void InsertionsEnterFromTheirRightEdgeAndRetiredItemsImmediatelyStopHitTesting()
    {
        var animation = new TaskbarGroupAnimation();
        animation.Retarget(Layout(32), TimeSpan.Zero, true, animation.Generation);
        animation.Retarget(Layout(64, "replacement"), TimeSpan.Zero, false, animation.Generation);
        var initial = animation.Sample(TimeSpan.Zero);
        var entering = Assert.Single(initial.Items, x => !x.IsExiting);
        var leaving = Assert.Single(initial.Items, x => x.IsExiting);
        Assert.Equal(-32, entering.Bounds.X);
        Assert.Equal(0, entering.Bounds.Width);
        Assert.Equal(0, entering.Opacity);
        Assert.False(entering.IsInteractive);
        Assert.False(leaving.IsInteractive);
        var midpoint = animation.Sample(TimeSpan.FromMilliseconds(110));
        entering = Assert.Single(midpoint.Items, x => !x.IsExiting);
        leaving = Assert.Single(midpoint.Items, x => x.IsExiting);
        Assert.Equal(56, entering.Bounds.Width);
        Assert.Equal(0.875, entering.Opacity);
        Assert.True(entering.IsInteractive);
        Assert.Equal(4, leaving.Bounds.Width);
        Assert.Equal(0.125, leaving.Opacity);
        Assert.False(leaving.IsInteractive);
        var complete = animation.Sample(TimeSpan.FromMilliseconds(220));
        Assert.Equal("replacement", Assert.Single(complete.Items).Key.ItemId);
        Assert.True(complete.IsComplete);
    }

    [Fact]
    public void ReducedMotionFinishesAnExistingTransitionAndReleasesAllExitVisuals()
    {
        var animation = new TaskbarGroupAnimation();
        animation.Retarget(Layout(32), TimeSpan.Zero, true, animation.Generation);
        var target = Layout(64, "replacement");
        animation.Retarget(target, TimeSpan.Zero, false, animation.Generation);
        animation.Retarget(target, TimeSpan.FromMilliseconds(30), true, animation.Generation);
        var frame = animation.Sample(TimeSpan.FromMilliseconds(30));
        Assert.True(frame.IsComplete);
        Assert.Equal(152, frame.WidthDip);
        var item = Assert.Single(frame.Items);
        Assert.Equal(64, item.Bounds.Width);
        Assert.Equal(1, item.Opacity);
        Assert.True(item.IsInteractive);
        Assert.False(item.IsExiting);
    }

    [Fact]
    public void RemovedAndReappearingItemsHaveSeparatePresenceAndOldOwnersCannotReviveThem()
    {
        var animation = new TaskbarGroupAnimation();
        var originalOwner = animation.Generation;
        animation.Retarget(Layout(32, generation: 1), TimeSpan.Zero, true, originalOwner);
        animation.Retarget(Layout(32, generation: 2), TimeSpan.Zero, false, originalOwner);
        var changing = animation.Sample(TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, changing.Items.Count);
        Assert.False(Assert.Single(changing.Items, x => x.Key.PresenceGeneration == 1).IsInteractive);
        Assert.True(Assert.Single(changing.Items, x => x.Key.PresenceGeneration == 2).IsInteractive);
        animation.Clear();
        Assert.True(animation.Generation > originalOwner);
        var cleared = animation.Sample(TimeSpan.Zero);
        Assert.True(cleared.IsComplete);
        Assert.Equal(0, cleared.WidthDip);
        Assert.Empty(cleared.Items);
        Assert.Empty(cleared.Components);
        Assert.False(animation.Retarget(Layout(32), TimeSpan.FromDays(10), false, originalOwner));
        animation.Retarget(Layout(64, generation: 3), TimeSpan.Zero, false, animation.Generation);
        var midpoint = animation.Sample(TimeSpan.FromMilliseconds(110));
        Assert.False(midpoint.IsComplete); // rejected callback did not poison the new owner's clock
        Assert.Equal(56, Assert.Single(midpoint.Items).Bounds.Width);
        animation.Clear();
        Assert.Empty(animation.Sample(TimeSpan.Zero).Items);
    }

    [Fact]
    public void IdenticalGeometryUpdatesInteractionWithoutExtendingTheAnimationDeadline()
    {
        var animation = new TaskbarGroupAnimation();
        animation.Retarget(Layout(32), TimeSpan.Zero, true, animation.Generation);
        var target = Layout(64);
        animation.Retarget(target, TimeSpan.Zero, false, animation.Generation);
        var component = TaskbarGroupLayoutTests.Component("middle");
        var disabled = TaskbarGroupLayout.Calculate([new(TaskbarGroupLayoutTests.Component("left"), 44),
            new(component, Items: [new(TaskbarGroupLayoutTests.Item(component, "item"), 64, false)]),
            new(TaskbarGroupLayoutTests.Component("right"), 20)], 32);
        animation.Retarget(disabled, TimeSpan.FromMilliseconds(110), false, animation.Generation);
        Assert.False(Assert.Single(animation.Sample(TimeSpan.FromMilliseconds(110)).Items).IsInteractive);
        Assert.True(animation.Sample(TimeSpan.FromMilliseconds(220)).IsComplete);
        animation.Retarget(target, TimeSpan.FromMilliseconds(230), false, animation.Generation);
        Assert.True(animation.Sample(TimeSpan.FromMilliseconds(230)).IsComplete);
        Assert.True(Assert.Single(animation.Sample(TimeSpan.FromMilliseconds(230)).Items).IsInteractive);
    }

    [Fact]
    public void HighChurnBoundsRetiredVisualsWithoutDroppingAnyCurrentAcceptedItem()
    {
        var animation = new TaskbarGroupAnimation();
        animation.Retarget(Batch("a"), TimeSpan.Zero, true, animation.Generation);
        animation.Retarget(Batch("b"), TimeSpan.Zero, false, animation.Generation);
        animation.Retarget(Batch("c"), TimeSpan.FromMilliseconds(110), false, animation.Generation);
        var frame = animation.Sample(TimeSpan.FromMilliseconds(110));
        Assert.Equal(2048, frame.Items.Count(x => !x.IsExiting));
        Assert.Equal(2048, frame.Items.Count(x => x.IsExiting));
        Assert.True(frame.AnimationBudgetReduced);
        Assert.All(frame.Items.Where(x => !x.IsExiting), x => Assert.StartsWith("c", x.Key.ItemId));
        Assert.All(frame.Items.Where(x => x.IsExiting), x => Assert.StartsWith("b", x.Key.ItemId));
        Assert.All(frame.Items.Where(x => x.IsExiting), x => Assert.False(x.IsInteractive));
        var final = animation.Sample(TimeSpan.FromMilliseconds(330));
        Assert.Equal(2048, final.Items.Count);
        Assert.DoesNotContain(final.Items, x => x.IsExiting);
        animation.Clear();
        var cleared = animation.Sample(TimeSpan.Zero);
        Assert.Empty(cleared.Items);
        Assert.False(cleared.AnimationBudgetReduced);
    }

    [Fact]
    public void RetiredComponentAncestorsDisappearAtCompletionAndEmptyGroupEndsAtZeroWidth()
    {
        var animation = new TaskbarGroupAnimation();
        animation.Retarget(Layout(32), TimeSpan.Zero, true, animation.Generation);
        animation.Retarget(TaskbarGroupLayout.Calculate([], 32), TimeSpan.Zero, false, animation.Generation);
        var middle = animation.Sample(TimeSpan.FromMilliseconds(110));
        Assert.Equal(15, middle.WidthDip);
        Assert.False(Assert.Single(middle.Items).IsInteractive);
        Assert.Single(middle.Components);
        var final = animation.Sample(TimeSpan.FromMilliseconds(220));
        Assert.Equal(0, final.WidthDip);
        Assert.Empty(final.Items);
        Assert.Empty(final.Components);
    }

    [Fact]
    public void DelayedSamplingCannotRewindAndPreviouslyReturnedFramesStayImmutable()
    {
        var animation = new TaskbarGroupAnimation();
        animation.Retarget(Layout(32), TimeSpan.Zero, true, animation.Generation);
        animation.Retarget(Layout(64), TimeSpan.Zero, false, animation.Generation);
        var frame = animation.Sample(TimeSpan.FromMilliseconds(110));
        Assert.Equal(frame.WidthDip, animation.Sample(TimeSpan.FromMilliseconds(10)).WidthDip);
        animation.Retarget(Layout(96), TimeSpan.FromMilliseconds(120), true, animation.Generation);
        Assert.Equal(60, Assert.Single(frame.Items).Bounds.Width);
        Assert.True(animation.Revision > frame.Revision);
        Assert.Throws<ArgumentOutOfRangeException>(() => animation.Sample(TimeSpan.FromTicks(-1)));
    }

    private static TaskbarGroupLayoutResult Batch(string prefix)
    {
        var component = TaskbarGroupLayoutTests.Component("batch");
        return TaskbarGroupLayout.Calculate([new(component, Items: Enumerable.Range(0, 2048)
            .Select(i => new TaskbarMeasuredItem(TaskbarGroupLayoutTests.Item(component, prefix + i), 32)).ToArray())], 32);
    }

    internal static TaskbarGroupLayoutResult Layout(double width, string id = "item", long generation = 1)
    {
        var middle = TaskbarGroupLayoutTests.Component("middle");
        return TaskbarGroupLayout.Calculate([new(TaskbarGroupLayoutTests.Component("left"), 44),
            new(middle, Items: [new(TaskbarGroupLayoutTests.Item(middle, id, generation), width)]),
            new(TaskbarGroupLayoutTests.Component("right"), 20)], 32);
    }
}

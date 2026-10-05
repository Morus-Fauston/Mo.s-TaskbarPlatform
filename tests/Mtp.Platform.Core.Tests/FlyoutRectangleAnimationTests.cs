namespace Mtp.Platform.Core.Tests;

public sealed class FlyoutRectangleAnimationTests
{
    [Fact]
    public void DelayedRetargetContinuesFromTheLastPresentedGeometryAndOpacity()
    {
        var animation = new FlyoutRectangleAnimation();
        animation.Retarget([new("main", new(100, 300, 320, 200))], TimeSpan.Zero, true, animation.Generation);
        animation.Retarget([new("main", new(200, 100, 240, 400), 0.2)], TimeSpan.Zero, false, animation.Generation);
        var presented = animation.Sample(TimeSpan.FromMilliseconds(50));
        // The UI stalls after applying this frame. Any later old-target samples were never displayed.
        animation.Sample(TimeSpan.FromMilliseconds(225));
        var replacement = new FlyoutRectangleTarget("main", new(50, 350, 400, 160), 0.9);
        Assert.True(animation.Retarget([replacement], TimeSpan.FromMilliseconds(230), false, animation.Generation,
            presentedFrame: presented));
        var resumed = animation.Sample(TimeSpan.FromMilliseconds(230));
        Assert.Equal(presented.Panels[0], Assert.Single(resumed.Panels));
        Assert.Equal(0, resumed.Progress);
        Assert.False(resumed.IsComplete);
        Assert.False(animation.Sample(TimeSpan.FromMilliseconds(449)).IsComplete);
        var completed = animation.Sample(TimeSpan.FromMilliseconds(450));
        Assert.True(completed.IsComplete);
        Assert.Equal(replacement, Assert.Single(completed.Panels));
    }

    [Fact]
    public void StalePresentedFrameCannotReplaceANewerTransitionOrAdvanceItsClock()
    {
        var animation = new FlyoutRectangleAnimation();
        FlyoutRectangleTarget[] first = [new("main", new(0, 0, 320, 200))];
        FlyoutRectangleTarget[] second = [new("main", new(0, 0, 640, 400))];
        animation.Retarget(first, TimeSpan.Zero, false, animation.Generation);
        var old = animation.Sample(TimeSpan.FromMilliseconds(50));
        animation.Retarget(second, TimeSpan.FromMilliseconds(100), false, animation.Generation, presentedFrame: old);
        var current = animation.Sample(TimeSpan.FromMilliseconds(100));
        Assert.False(animation.Retarget(first, TimeSpan.FromDays(10), false, animation.Generation, presentedFrame: old));
        var unchanged = animation.Sample(TimeSpan.FromMilliseconds(100));
        Assert.Equal(current.Revision, unchanged.Revision);
        Assert.Equal(current.Panels, unchanged.Panels);
        Assert.Equal(current.Progress, unchanged.Progress);
        animation.Clear();
        Assert.False(animation.Retarget(first, TimeSpan.FromDays(10), false, animation.Generation, presentedFrame: current));
        animation.Retarget(first, TimeSpan.Zero, false, animation.Generation);
        Assert.False(animation.Sample(TimeSpan.FromMilliseconds(50)).IsComplete);
    }

    [Fact]
    public void MalformedPresentedFramesAreRejectedBeforeChangingTheAnimation()
    {
        var animation = new FlyoutRectangleAnimation();
        FlyoutRectangleTarget[] target = [new("main", new(0, 0, 320, 200))];
        animation.Retarget(target, TimeSpan.Zero, false, animation.Generation);
        var current = animation.Sample(TimeSpan.FromMilliseconds(50));
        FlyoutRectangleFrame[] malformed =
        [
            current with { Progress = double.NaN },
            current with { Panels = null! },
            current with { Panels = Enumerable.Range(0, 17).Select(i => new FlyoutRectangleTarget(i.ToString(), new(0, 0, 10, 10))).ToArray() },
            current with { Panels = [current.Panels[0], current.Panels[0]] },
            current with { Panels = [current.Panels[0] with { Key = "unknown" }] },
            current with { Panels = [current.Panels[0] with { Opacity = double.NaN }] },
            current with { Panels = [current.Panels[0] with { Bounds = new(0, 0, -1, 1) }] }
        ];
        foreach (var frame in malformed)
        {
            Assert.Throws<ArgumentException>(() => animation.Retarget(target, TimeSpan.FromDays(10), false,
                animation.Generation, forceTransition: true, presentedFrame: frame));
            var unchanged = animation.Sample(TimeSpan.FromMilliseconds(50));
            Assert.Equal(current.Revision, unchanged.Revision);
            Assert.Equal(current.Panels, unchanged.Panels);
            Assert.Equal(current.Progress, unchanged.Progress);
        }
    }

    [Fact]
    public void ResizeAndOpacityUseSharedTimingAndInterruptFromTheCurrentFrame()
    {
        var animation = new FlyoutRectangleAnimation();
        animation.Retarget([new("main", new(100, 300, 320, 200))], TimeSpan.Zero, true, animation.Generation);
        animation.Retarget([new("main", new(100, 100, 320, 400), 0)], TimeSpan.Zero, false, animation.Generation);
        var middle = animation.Sample(TimeSpan.FromMilliseconds(110));
        Assert.Equal(new TaskbarDipRect(100, 125, 320, 375), Assert.Single(middle.Panels).Bounds);
        Assert.Equal(0.125, middle.Panels[0].Opacity);
        animation.Retarget([new("main", new(100, 300, 320, 200))], TimeSpan.FromMilliseconds(110), false, animation.Generation);
        Assert.Equal(middle.Panels[0], Assert.Single(animation.Sample(TimeSpan.FromMilliseconds(110)).Panels));
        Assert.True(animation.Sample(TimeSpan.FromMilliseconds(330)).IsComplete);
    }

    [Fact]
    public void SameGeometryDoesNotRestartButSemanticNavigationCanForceATransition()
    {
        var animation = new FlyoutRectangleAnimation();
        FlyoutRectangleTarget[] target = [new("main", new(0, 0, 320, 200))];
        animation.Retarget(target, TimeSpan.Zero, false, animation.Generation);
        animation.Retarget(target, TimeSpan.FromMilliseconds(110), false, animation.Generation);
        Assert.True(animation.Sample(TimeSpan.FromMilliseconds(220)).IsComplete);
        animation.Retarget(target, TimeSpan.FromMilliseconds(220), false, animation.Generation, true);
        Assert.False(animation.Sample(TimeSpan.FromMilliseconds(220)).IsComplete);
        animation.Retarget(target, TimeSpan.FromMilliseconds(240), true, animation.Generation);
        Assert.True(animation.Sample(TimeSpan.FromMilliseconds(240)).IsComplete);
    }

    [Fact]
    public void ClearReleasesEveryTrackAndRejectsOldGenerationsWithoutClockMutation()
    {
        var animation = new FlyoutRectangleAnimation(); var old = animation.Generation;
        FlyoutRectangleTarget[] target = [new("main", new(0, 0, 320, 200))];
        animation.Retarget(target, TimeSpan.Zero, false, old); animation.Clear();
        Assert.Empty(animation.Sample(TimeSpan.Zero).Panels);
        Assert.False(animation.Retarget(target, TimeSpan.FromDays(10), false, old));
        animation.Retarget(target, TimeSpan.Zero, false, animation.Generation);
        Assert.False(animation.Sample(TimeSpan.FromMilliseconds(110)).IsComplete);
        Assert.Throws<ArgumentOutOfRangeException>(() => animation.Retarget(
            Enumerable.Range(0, 17).Select(i => new FlyoutRectangleTarget(i.ToString(), new(0, 0, 320, 200))).ToArray(),
            TimeSpan.Zero, false, animation.Generation));
        animation.Clear(); Assert.Empty(animation.Sample(TimeSpan.Zero).Panels);
    }
}

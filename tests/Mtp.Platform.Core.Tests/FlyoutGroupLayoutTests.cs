namespace Mtp.Platform.Core.Tests;

public sealed class FlyoutGroupLayoutTests
{
    [Fact]
    public void ParallelPanelsFitInsideTheSafeWorkspaceAndNarrowSpaceKeepsTheCurrentPanelReachable()
    {
        FlyoutPanelMeasure[] panels = [new("main", 200), new("details", 300, true)];
        var wide = FlyoutGroupLayout.Calculate(new(0, 0, 1000, 800), new(800, 800, 40, 32), panels, "details");
        Assert.True(wide.IsSuccess);
        Assert.Equal(FlyoutGroupMode.Parallel, wide.Value!.Mode);
        Assert.Equal(new TaskbarDipRect(192, 484, 648, 300), wide.Value.Bounds);
        Assert.Equal(2, wide.Value.VisiblePanels.Count);
        var narrow = FlyoutGroupLayout.Calculate(new(0, 0, 500, 800), new(450, 800, 40, 32), panels, "details");
        Assert.True(narrow.IsSuccess);
        Assert.Equal(FlyoutGroupMode.Hierarchical, narrow.Value!.Mode);
        Assert.Equal("details", Assert.Single(narrow.Value.VisiblePanels).TemplateId);
        Assert.Contains("main", narrow.Value.CollapsedPanelIds);
    }

    [Fact]
    public void AllSixteenTemplatesCanAppearAndTinySpaceKeepsHeaderAndScrollableCurrentContent()
    {
        var panels = Enumerable.Range(0, 16).Select(i => new FlyoutPanelMeasure(i.ToString(), 200, i > 0)).ToArray();
        var full = FlyoutGroupLayout.Calculate(new(0, 0, 6000, 800), new(5800, 800, 40, 32), panels, "15");
        Assert.True(full.IsSuccess); Assert.Equal(16, full.Value!.VisiblePanels.Count);
        Assert.Equal(5240, full.Value.Bounds.Width);
        var small = FlyoutGroupLayout.Calculate(new(-300, 0, 250, 200), new(-100, 200, 40, 32), panels, "15");
        Assert.True(small.IsSuccess);
        Assert.Equal(FlyoutGroupMode.Collapsed, small.Value!.Mode);
        var current = Assert.Single(small.Value.VisiblePanels);
        Assert.Equal("15", current.TemplateId); Assert.True(current.Scrolls);
        Assert.Equal(15, small.Value.CollapsedPanelIds.Count);
        Assert.Equal(218, current.Bounds.Width); Assert.Equal(168, current.Bounds.Height);
        Assert.True(current.Bounds.Y >= 16 && current.Bounds.Bottom <= 184);
    }

    [Fact]
    public void ParallelWidthReservesEachPanelsRequiredNavigationArea()
    {
        var result = FlyoutGroupLayout.Calculate(new(0, 0, 300, 200), new(200, 200, 40, 32),
            [new("main", 1, WidthDip: 1), new("details", 1, true, 1)], "details");
        Assert.True(result.IsSuccess);
        Assert.Equal(FlyoutGroupMode.Parallel, result.Value!.Mode);
        Assert.Equal(200, result.Value.Bounds.Width);
        Assert.All(result.Value.VisiblePanels, panel => { Assert.Equal(96, panel.Bounds.Width); Assert.Equal(80, panel.Bounds.Height); });
        var narrow = FlyoutGroupLayout.Calculate(new(0, 0, 220, 200), new(150, 200, 40, 32),
            [new("main", 1, WidthDip: 1), new("details", 1, true, 1)], "details");
        Assert.True(narrow.IsSuccess);
        Assert.Equal(FlyoutGroupMode.Hierarchical, narrow.Value!.Mode);
    }

    [Fact]
    public void UnusableWorkspaceAndMalformedMeasurementAreExplicitFailures()
    {
        var work = new TaskbarDipRect(0, 0, 1000, 800); var anchor = new TaskbarDipRect(500, 800, 40, 32);
        Assert.Equal("NoUsableWorkArea", FlyoutGroupLayout.Calculate(new(0, 0, 100, 100), anchor, [new("main", 200)], "main").Error!.Code);
        Assert.False(FlyoutGroupLayout.Calculate(work, anchor, [new("main", double.NaN)], "main").IsSuccess);
        Assert.False(FlyoutGroupLayout.Calculate(work, anchor, [new("main", 200), new("main", 200)], "main").IsSuccess);
        Assert.False(FlyoutGroupLayout.Calculate(work, anchor, Enumerable.Range(0, 17).Select(i => new FlyoutPanelMeasure(i.ToString(), 100)).ToArray(), "0").IsSuccess);
        Assert.Equal("UnknownPanel", FlyoutGroupLayout.Calculate(work, anchor, [new("main", 200)], "missing").Error!.Code);
    }
}

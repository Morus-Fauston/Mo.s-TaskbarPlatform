using Mtp.Host.Flyouts;
using Mtp.Platform.Core;

namespace Mtp.Host.Windows.Tests;

public sealed class TaskbarFlyoutPlacementTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void FractionalAndNegativeCoordinatesRoundEdgesAfterScaling(int dpi)
    {
        var bounds = new TaskbarDipRect(-119.4, -33.3, 102.6, 80.7);
        var pixels = FlyoutNative.ToPixels(bounds, (uint)dpi);
        Assert.Equal((int)Math.Round(bounds.X * dpi / 96, MidpointRounding.AwayFromZero), pixels.X);
        Assert.Equal((int)Math.Round(bounds.Right * dpi / 96, MidpointRounding.AwayFromZero), pixels.Right);
        Assert.Equal((int)Math.Round(bounds.Bottom * dpi / 96, MidpointRounding.AwayFromZero), pixels.Bottom);
        var dip = FlyoutNative.ToDip(pixels, (uint)dpi);
        Assert.Equal(pixels, FlyoutNative.ToPixels(dip, (uint)dpi));
    }

    [Fact]
    public void InvalidOrOverflowingNativeExtentsFailBeforePositioning()
    {
        foreach (var bounds in new TaskbarDipRect[] { new(double.NaN, 0, 100, 100), new(0, 0, 0, 10),
            new(int.MaxValue, 0, 100, 100), new(int.MinValue, 0, (double)int.MaxValue + 2, 100), new(0, 0, double.PositiveInfinity, 100) })
            Assert.Throws<ArgumentOutOfRangeException>(() => FlyoutNative.ToPixels(bounds, 96));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlyoutNative.ToPixels(new(0, 0, 100, 100), 0));
    }

    [Fact]
    public void MessageTimesPreserveOrderAcrossTheWin32TickWrap()
    {
        Assert.True(FlyoutNative.IsAfter(1, uint.MaxValue));
        Assert.False(FlyoutNative.IsAfter(uint.MaxValue, 1));
        Assert.False(FlyoutNative.IsAfter(25, 25));
        Assert.True(FlyoutNative.IsAfter(26, 25));
    }

    [Fact]
    public void TriggerRectangleUsesExclusiveRightAndBottomEdges()
    {
        var rectangle = new PixelRect(-100, -100, 20, 20);
        Assert.True(FlyoutNative.Contains(rectangle, new() { X = -100, Y = -100 }));
        Assert.True(FlyoutNative.Contains(rectangle, new() { X = -81, Y = -81 }));
        Assert.False(FlyoutNative.Contains(rectangle, new() { X = -80, Y = -90 }));
        Assert.False(FlyoutNative.Contains(rectangle, new() { X = -90, Y = -80 }));
    }
}

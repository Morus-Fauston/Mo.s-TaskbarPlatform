using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host.Windows.Tests;

public sealed class TaskbarGroupPlacementTests
{
    [Fact]
    public void CompleteGroupExtendsLeftOfTheDisplayWithoutMovingItsNotificationAnchor()
    {
        var geometry = Geometry();
        var result = TaskbarGroupPlacement.Calculate(geometry, new(2400, 32), 8);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new PixelRect(-808, 1040, 2400, 32), result.Value);
        Assert.Equal(1592, result.Value.Right);
        Assert.False(TaskbarDockPlacement.Calculate(geometry, new(2400, 32), 8).IsSuccess);
    }

    [Fact]
    public void FractionalAnimationWidthsAreRoundedOnlyAfterDpiScalingAndKeepTheirRightEdge()
    {
        var before = TaskbarGroupPlacement.Calculate(Geometry(144), 100.1, 20.4, 8);
        var after = TaskbarGroupPlacement.Calculate(Geometry(144), 100.4, 20.4, 8);
        Assert.True(before.IsSuccess);
        Assert.True(after.IsSuccess);
        Assert.Equal(new PixelRect(1438, 1040, 150, 31), before.Value);
        Assert.Equal(new PixelRect(1437, 1040, 151, 31), after.Value);
        Assert.Equal(before.Value.Right, after.Value.Right);
        Assert.Equal(1588, after.Value.Right);
    }

    [Fact]
    public void PositiveHalfPixelsRoundAwayFromZeroAndDipSizeUsesTheSameImplementation()
    {
        var fractional = TaskbarGroupPlacement.Calculate(Geometry(144), 1d, 1d, 1);
        var integer = TaskbarGroupPlacement.Calculate(Geometry(144), new(1, 1), 1);
        Assert.True(fractional.IsSuccess);
        Assert.Equal(new PixelRect(1596, 1055, 2, 2), fractional.Value);
        Assert.Equal(fractional.Value, integer.Value);
    }

    [Fact]
    public void NativeLeftBoundaryIsAcceptedExactlyButOnePixelFurtherLeftIsRejected()
    {
        var geometry = Geometry() with
        {
            DisplayBounds = new(-1920, 0, 1920, 1080),
            TaskbarBounds = new(-1920, 1032, 1920, 48),
            NotificationBounds = new(-1, 1032, 1, 48)
        };
        var boundary = TaskbarGroupPlacement.Calculate(geometry, int.MaxValue, 32, 0);
        Assert.True(boundary.IsSuccess, boundary.Error?.Message);
        Assert.Equal(int.MinValue, boundary.Value.X);
        Assert.Equal(int.MaxValue, boundary.Value.Width);
        Assert.Equal(-1, boundary.Value.Right);
        var underflow = TaskbarGroupPlacement.Calculate(geometry, int.MaxValue, 32, 1);
        Assert.False(underflow.IsSuccess);
        Assert.Equal("dock_group_native_range_exceeded", underflow.Error!.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisplayCornersNearNativeCoordinateLimitsDoNotWrap(bool positive)
    {
        var displayX = positive ? int.MaxValue - 1920 : int.MinValue;
        var displayY = positive ? int.MaxValue - 1080 : int.MinValue;
        var geometry = new TaskbarDockGeometry("extreme", new(displayX, displayY, 1920, 1080),
            new(displayX, displayY + 1032, 1920, 48), new(displayX + 1600, displayY + 1032, 320, 48), 96);
        var result = TaskbarGroupPlacement.Calculate(geometry, 240, 32, 8);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal((long)displayX + 1352, result.Value.X);
        Assert.Equal((long)displayY + 1040, result.Value.Y);
        Assert.True(result.Value.IsValid);
    }

    [Theory]
    [InlineData(double.NaN, 32)]
    [InlineData(double.PositiveInfinity, 32)]
    [InlineData(double.NegativeInfinity, 32)]
    [InlineData(0, 32)]
    [InlineData(-1, 32)]
    [InlineData(240, double.NaN)]
    [InlineData(240, double.PositiveInfinity)]
    [InlineData(240, 0)]
    public void InvalidFloatingDimensionsAreRejectedBeforeConversion(double width, double height)
    {
        var result = TaskbarGroupPlacement.Calculate(Geometry(), width, height, 8);
        Assert.False(result.IsSuccess);
        Assert.Equal("dock_geometry_invalid", result.Error!.Code);
    }

    [Fact]
    public void PixelScalingRejectsNativeSizeOverflowAndSubpixelZeroWithoutThrowing()
    {
        foreach (var result in new[]
        {
            TaskbarGroupPlacement.Calculate(Geometry(), (double)int.MaxValue + 1, 32, 8),
            TaskbarGroupPlacement.Calculate(Geometry(192), int.MaxValue, 16, 8),
            TaskbarGroupPlacement.Calculate(Geometry(192), double.MaxValue, 16, 8),
            TaskbarGroupPlacement.Calculate(Geometry(), 240, double.MaxValue, 8),
            TaskbarGroupPlacement.Calculate(Geometry(uint.MaxValue), 240, 32, 64)
        })
        {
            Assert.False(result.IsSuccess);
            Assert.Equal("dock_group_native_range_exceeded", result.Error!.Code);
        }
        var tooSmall = TaskbarGroupPlacement.Calculate(Geometry(), 0.4, 32, 0);
        Assert.False(tooSmall.IsSuccess);
        Assert.Equal("dock_geometry_invalid", tooSmall.Error!.Code);
    }

    [Fact]
    public void HeightDpiAndGapRemainStrictEvenWhenHorizontalOverflowIsAllowed()
    {
        var height = TaskbarGroupPlacement.Calculate(Geometry(192), 100, 32, 8);
        Assert.False(height.IsSuccess);
        Assert.Equal("dock_group_height_insufficient", height.Error!.Code);
        Assert.Equal("dock_geometry_invalid", TaskbarGroupPlacement.Calculate(Geometry(0), 240, 32, 8).Error!.Code);
        Assert.Equal("dock_geometry_invalid", TaskbarGroupPlacement.Calculate(Geometry(), 240, 32, -1).Error!.Code);
        Assert.Equal("dock_geometry_invalid", TaskbarGroupPlacement.Calculate(Geometry(), 240, 32, 65).Error!.Code);
    }

    [Fact]
    public void InvalidDisplayTaskbarAndNotificationAnchorNeverUseAFallbackEdge()
    {
        Assert.Equal("dock_display_invalid", TaskbarGroupPlacement.Calculate(null, 240, 32, 8).Error!.Code);
        Assert.Equal("dock_display_invalid", TaskbarGroupPlacement.Calculate(Geometry() with { DisplayId = " " }, 240, 32, 8).Error!.Code);
        Assert.Equal("dock_display_invalid", TaskbarGroupPlacement.Calculate(Geometry() with
        { DisplayBounds = new(int.MaxValue, 0, 1, 1080) }, 240, 32, 8).Error!.Code);
        Assert.Equal("dock_taskbar_unsupported", TaskbarGroupPlacement.Calculate(Geometry() with
        { TaskbarBounds = new(0, 0, 1920, 48) }, 240, 32, 8).Error!.Code);
        Assert.Equal("dock_taskbar_unsupported", TaskbarGroupPlacement.Calculate(Geometry() with
        { TaskbarBounds = new(0, 0, 48, 1080) }, 240, 32, 8).Error!.Code);
        foreach (var anchor in new PixelRect?[] { null, new(0, 1032, 320, 48), new(1900, 1032, 320, 48), new(1600, 1000, 320, 48) })
            Assert.Equal("dock_anchor_unavailable", TaskbarGroupPlacement.Calculate(Geometry() with
            { NotificationBounds = anchor }, 240, 32, 8).Error!.Code);
    }

    private static TaskbarDockGeometry Geometry(uint dpi = 96) => new("screen",
        new(0, 0, 1920, 1080), new(0, 1032, 1920, 48), new(1600, 1032, 320, 48), dpi);
}

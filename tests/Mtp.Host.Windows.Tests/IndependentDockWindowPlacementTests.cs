using Mtp.Host;
using Windows.Graphics;

namespace Mtp.Platform.Core.Tests;

public sealed class IndependentDockWindowPlacementTests
{
    [Fact]
    public void PrimaryDisplayWithZeroOffsetProducesAnInBoundsBottomRightPlacement()
    {
        var result = IndependentDockWindowPlacement.TryCalculate(
            new RectInt32(0, 0, 1920, 1080),
            new RectInt32(0, 0, 1920, 1040),
            new SizeInt32(340, 84),
            8);

        Assert.True(result.IsSuccess);
        Assert.Equal(new RectInt32(1572, 948, 340, 84), result.Value);
    }

    [Fact]
    public void SecondaryDisplayOffsetIsAppliedExactlyOnce()
    {
        var result = IndependentDockWindowPlacement.TryCalculate(
            new RectInt32(2560, 367, 1707, 1067),
            new RectInt32(0, 0, 1707, 1019),
            new SizeInt32(340, 84),
            8);

        Assert.True(result.IsSuccess);
        Assert.Equal(new RectInt32(3919, 1294, 340, 84), result.Value);
        Assert.True(result.Value!.X >= 2560);
        Assert.True(result.Value!.Y >= 367);
        Assert.True(result.Value!.X + result.Value!.Width <= 4267);
        Assert.True(result.Value!.Y + result.Value!.Height <= 1386);
    }

    [Fact]
    public void SecondaryWorkAreaRelativeInsetIsAddedOnceToTheDisplayOffset()
    {
        var result = IndependentDockWindowPlacement.TryCalculate(
            new RectInt32(2560, 367, 1707, 1067),
            new RectInt32(10, 20, 1690, 999),
            new SizeInt32(340, 84),
            8);

        Assert.True(result.IsSuccess);
        Assert.Equal(new RectInt32(3912, 1294, 340, 84), result.Value);
    }

    [Fact]
    public void SecondaryWorkAreaAlreadyInScreenCoordinatesIsNotOffsetTwice()
    {
        var result = IndependentDockWindowPlacement.TryCalculate(
            new RectInt32(2560, 367, 1707, 1067),
            new RectInt32(2560, 367, 1707, 1019),
            new SizeInt32(340, 84),
            8);

        Assert.True(result.IsSuccess);
        Assert.Equal(new RectInt32(3919, 1294, 340, 84), result.Value);
    }

    [Fact]
    public void WindowLargerThanWorkAreaReturnsStructuredFailureInsteadOfEscaping()
    {
        var result = IndependentDockWindowPlacement.TryCalculate(
            new RectInt32(2560, 367, 1707, 1067),
            new RectInt32(0, 0, 260, 60),
            new SizeInt32(340, 84),
            8);

        Assert.False(result.IsSuccess);
        Assert.Equal("dock_window_too_large", result.Error!.Code);
    }

    [Fact]
    public void ExcessiveMarginIsClampedToASafeInBoundsPosition()
    {
        var result = IndependentDockWindowPlacement.TryCalculate(
            new RectInt32(0, 0, 800, 600),
            new RectInt32(0, 0, 800, 560),
            new SizeInt32(340, 84),
            1000);

        Assert.True(result.IsSuccess);
        Assert.Equal(new RectInt32(0, 0, 340, 84), result.Value);
    }

    [Fact]
    public void InvalidRelativeWorkAreaIsRejectedBeforePositioning()
    {
        var result = IndependentDockWindowPlacement.TryCalculate(
            new RectInt32(2560, 367, 1707, 1067),
            new RectInt32(4000, 1400, 1707, 1019),
            new SizeInt32(340, 84),
            8);

        Assert.False(result.IsSuccess);
        Assert.Equal("dock_work_area_invalid", result.Error!.Code);
    }
}

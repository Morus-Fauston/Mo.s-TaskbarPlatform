namespace Mtp.Platform.Core.Tests;

public sealed class HintLayoutTests
{
    [Theory]
    [InlineData(HintPosition.Default, 400, 600)]
    [InlineData(HintPosition.LowerCenter, 400, 600)]
    [InlineData(HintPosition.TopLeft, 16, 16)]
    [InlineData(HintPosition.TopCenter, 400, 16)]
    [InlineData(HintPosition.TopRight, 784, 16)]
    [InlineData(HintPosition.BottomLeft, 16, 704)]
    [InlineData(HintPosition.BottomCenter, 400, 704)]
    [InlineData(HintPosition.BottomRight, 784, 704)]
    [InlineData(HintPosition.Center, 400, 360)]
    public void IndependentHintsUseFinitePositionsAndSafeMargins(HintPosition position, double x, double y)
    {
        var result = HintLayout.Calculate(new(0, 0, 1000, 800), 200, 80, position);
        Assert.True(result.IsSuccess);
        Assert.Equal(new TaskbarDipRect(x, y, 200, 80), result.Value!.Bounds);
        Assert.Equal(HintPlacementMode.Independent, result.Value.Mode);
        Assert.Null(result.Value.GroupWorkArea);
    }

    [Fact]
    public void LowerCenterUsesTheActualWorkAreaAndClampsTheWholeHint()
    {
        var result = HintLayout.Calculate(new(-1000, -600, 1000, 800), 200, 700, HintPosition.LowerCenter);
        Assert.Equal(new TaskbarDipRect(-600, -516, 200, 700), result.Value!.Bounds);
    }

    [Fact]
    public void AssociatedHintsMatchTheirOwnerWidthAndPreferAboveThenBelow()
    {
        var work = new TaskbarDipRect(0, 0, 1000, 800);
        var above = HintLayout.Calculate(work, 200, 80, HintPosition.BottomRight, new(600, 500, 300, 200));
        Assert.True(above.IsSuccess);
        Assert.Equal(HintPlacementMode.Above, above.Value!.Mode);
        Assert.Equal(new TaskbarDipRect(600, 412, 300, 80), above.Value.Bounds);
        var below = HintLayout.Calculate(work, 200, 80, HintPosition.TopLeft, new(600, 16, 300, 100));
        Assert.Equal(HintPlacementMode.Below, below.Value!.Mode);
        Assert.Equal(new TaskbarDipRect(600, 124, 300, 80), below.Value.Bounds);
    }

    [Fact]
    public void InsufficientAboveAndBelowSpaceReservesOnlyTheOwnerGroupWorkArea()
    {
        var result = HintLayout.Calculate(new(0, 0, 1000, 800), 200, 80, owner: new(16, 16, 968, 768));
        Assert.True(result.IsSuccess);
        Assert.Equal(HintPlacementMode.GroupReflow, result.Value!.Mode);
        Assert.Equal(new TaskbarDipRect(0, 88, 1000, 712), result.Value.GroupWorkArea);
        Assert.Equal(new TaskbarDipRect(16, 16, 968, 80), result.Value.Bounds);
    }

    [Fact]
    public void MalformedOrUnusableInputIsRejectedWithoutInventingAnIndependentHint()
    {
        var work = new TaskbarDipRect(0, 0, 1000, 800);
        Assert.False(HintLayout.Calculate(work, double.NaN, 80).IsSuccess);
        Assert.False(HintLayout.Calculate(work, 200, 0).IsSuccess);
        Assert.False(HintLayout.Calculate(work, 200, 80, (HintPosition)99).IsSuccess);
        Assert.False(HintLayout.Calculate(new(0, 0, 32, 100), 1, 1).IsSuccess);
        Assert.False(HintLayout.Calculate(work, 969, 80).IsSuccess);
        Assert.False(HintLayout.Calculate(work, 200, 80, owner: new(-1, 100, 200, 100)).IsSuccess);
        Assert.False(HintLayout.Calculate(work, 200, 80, owner: new(16, 16, 985, 100)).IsSuccess);
        Assert.False(HintLayout.Calculate(new(0, 0, 500, 150), 200, 80, owner: new(16, 16, 468, 118)).IsSuccess);
    }
}

using Mtp.Host;
using Windows.Graphics;

namespace Mtp.Platform.Core.Tests;

public sealed class ExplorerTaskbarProbePlacementTests
{
    private static readonly SizeInt32 ProbeSize = new(240, 40);
    private const int Margin = 8;

    [Fact]
    public void HorizontalTaskbarWithTrayAnchorRightAlignsToTheAnchor()
    {
        var result = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(1920, 48), 1600, ProbeSize, 96, Margin);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExplorerTaskbarProbePlacement.TrayNotifyAnchor, result.Value!.AnchorKind);
        Assert.Equal(new RectInt32(1352, 4, 240, 40), result.Value.ClientRect);
        Assert.Equal(1600 - Margin, result.Value.ClientRect.X + result.Value.ClientRect.Width);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(2000)]
    public void MissingOrOutOfRangeAnchorFallsBackToFixedInset(int? trayLeftEdge)
    {
        var result = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(1920, 48), trayLeftEdge, ProbeSize, 96, Margin);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExplorerTaskbarProbePlacement.FixedInsetAnchor, result.Value!.AnchorKind);
        Assert.Equal(new RectInt32(1672, 4, 240, 40), result.Value.ClientRect);
    }

    [Fact]
    public void VerticalTaskbarIsRejected()
    {
        var result = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(48, 1080), null, ProbeSize, 96, Margin);

        Assert.False(result.IsSuccess);
        Assert.Equal("explorer_probe_taskbar_orientation_unsupported", result.Error!.Code);
    }

    [Theory]
    [InlineData(0, 48)]
    [InlineData(1920, 0)]
    [InlineData(-1, 48)]
    public void EmptyTaskbarRectangleIsRejected(int width, int height)
    {
        var result = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(width, height), null, ProbeSize, 96, Margin);

        Assert.False(result.IsSuccess);
        Assert.Equal("explorer_probe_taskbar_rect_invalid", result.Error!.Code);
    }

    [Fact]
    public void InvalidProbeSizeOrMarginIsRejected()
    {
        var zeroWidth = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(1920, 48), null, new SizeInt32(0, 40), 96, Margin);
        var negativeMargin = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(1920, 48), null, ProbeSize, 96, -1);

        Assert.Equal("explorer_probe_taskbar_rect_invalid", zeroWidth.Error!.Code);
        Assert.Equal("explorer_probe_taskbar_rect_invalid", negativeMargin.Error!.Code);
    }

    [Fact]
    public void ZeroDpiIsRejected()
    {
        var result = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(1920, 48), null, ProbeSize, 0, Margin);

        Assert.False(result.IsSuccess);
        Assert.Equal("explorer_probe_dpi_unavailable", result.Error!.Code);
    }

    [Theory]
    [InlineData(200, 48)]
    [InlineData(1920, 30)]
    public void TaskbarWithoutRoomIsRejected(int width, int height)
    {
        var result = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(width, height), null, ProbeSize, 96, Margin);

        Assert.False(result.IsSuccess);
        Assert.Equal("explorer_probe_taskbar_too_small", result.Error!.Code);
    }

    [Fact]
    public void HigherDpiScalesProbeSizeAndMargin()
    {
        var result = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(1920, 72), null, ProbeSize, 144, Margin);

        Assert.True(result.IsSuccess);
        Assert.Equal(new RectInt32(1548, 6, 360, 60), result.Value!.ClientRect);
    }

    [Theory]
    [InlineData(1920, 48, 96, 1600)]
    [InlineData(2560, 60, 120, 2100)]
    [InlineData(1366, 40, 96, null)]
    public void SuccessfulPlacementStaysInsideTaskbarClientArea(int width, int height, uint dpi, int? trayLeftEdge)
    {
        var result = ExplorerTaskbarProbePlacement.TryCalculate(new SizeInt32(width, height), trayLeftEdge, ProbeSize, dpi, Margin);

        Assert.True(result.IsSuccess);
        var rect = result.Value!.ClientRect;
        Assert.True(rect.X >= 0);
        Assert.True(rect.Y >= 0);
        Assert.True(rect.X + rect.Width <= width);
        Assert.True(rect.Y + rect.Height <= height);
    }
}

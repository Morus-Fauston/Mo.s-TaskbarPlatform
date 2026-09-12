using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class TaskbarDockPlacementTests
{
    [Fact]
    public void RightEdgeUsesNotificationAreaAndScalesDipOnNegativeOriginScreen()
    {
        var result = TaskbarDockPlacement.Calculate(
            new("display", new(-2560, 0, 2560, 1440), new(-2560, 1380, 2560, 60), new(-300, 1380, 300, 60), 144),
            new(220, 32), 8);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new PixelRect(-642, 1386, 330, 48), result.Value);
    }

    public static IEnumerable<object?[]> InvalidInputs()
    {
        var valid = new TaskbarDockGeometry("display", new(0, 0, 1920, 1080), new(0, 1032, 1920, 48), new(1700, 1032, 220, 48), 96);
        yield return [null, new DipSize(240, 32), 8, "dock_display_invalid"];
        yield return [valid with { DisplayId = "" }, new DipSize(240, 32), 8, "dock_display_invalid"];
        yield return [valid with { DisplayBounds = default }, new DipSize(240, 32), 8, "dock_display_invalid"];
        yield return [valid with { NotificationBounds = null }, new DipSize(240, 32), 8, "dock_anchor_unavailable"];
        yield return [valid with { NotificationBounds = new(0, 1032, 0, 48) }, new DipSize(240, 32), 8, "dock_anchor_unavailable"];
        yield return [valid with { NotificationBounds = new(1800, 1032, 220, 48) }, new DipSize(240, 32), 8, "dock_anchor_unavailable"];
        yield return [valid with { TaskbarBounds = new(0, 0, 1920, 48) }, new DipSize(240, 32), 8, "dock_taskbar_unsupported"];
        yield return [valid with { TaskbarBounds = new(0, 0, 48, 1080) }, new DipSize(240, 32), 8, "dock_taskbar_unsupported"];
        yield return [valid with { Dpi = 0 }, new DipSize(240, 32), 8, "dock_geometry_invalid"];
        yield return [valid, new DipSize(0, 32), 8, "dock_geometry_invalid"];
        yield return [valid, new DipSize(240, 32), -1, "dock_geometry_invalid"];
        yield return [valid, new DipSize(240, 32), 65, "dock_geometry_invalid"];
        yield return [valid, new DipSize(int.MaxValue, 32), 8, "dock_space_insufficient"];
        yield return [valid, new DipSize(240, 49), 8, "dock_space_insufficient"];
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void InvalidGeometryReturnsAnExplanatoryFailure(TaskbarDockGeometry? environment, DipSize size, int gap, string code)
    {
        var result = TaskbarDockPlacement.Calculate(environment, size, gap);
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error?.Code);
    }

    [Theory]
    [InlineData(0, 1460)]
    [InlineData(64, 1396)]
    public void BothAllowedGapEndpointsAreAccepted(int gap, int x)
    {
        var result = TaskbarDockPlacement.Calculate(new("display", new(0, 0, 1920, 1080), new(0, 1032, 1920, 48), new(1700, 1032, 220, 48), 96), new(240, 32), gap);
        Assert.True(result.IsSuccess);
        Assert.Equal(x, result.Value.X);
    }
}

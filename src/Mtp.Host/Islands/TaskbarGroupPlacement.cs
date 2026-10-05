using Mtp.Platform.Core;

namespace Mtp.Host.Islands;

/// <summary>Native coordinate boundary for a complete group anchored to the notification area.</summary>
public static class TaskbarGroupPlacement
{
    public static CoreResult<PixelRect> Calculate(TaskbarDockGeometry? environment, DipSize size, int rightGapDip)
        => Calculate(environment, size.Width, size.Height, rightGapDip);

    public static CoreResult<PixelRect> Calculate(TaskbarDockGeometry? environment,
        double widthDip, double heightDip, int rightGapDip)
    {
        if (environment is null || string.IsNullOrWhiteSpace(environment.DisplayId) || !environment.DisplayBounds.IsValid)
            return Failure("dock_display_invalid", "目标显示器不可用。");
        if (!double.IsFinite(widthDip) || !double.IsFinite(heightDip) || widthDip <= 0 || heightDip <= 0 ||
            rightGapDip is < 0 or > 64 || environment.Dpi == 0)
            return Failure("dock_geometry_invalid", "组尺寸、DPI 或右侧间距无效。");

        var bar = environment.TaskbarBounds;
        if (!environment.DisplayBounds.Contains(bar) || bar.Width <= bar.Height || bar.Bottom != environment.DisplayBounds.Bottom)
            return Failure("dock_taskbar_unsupported", "当前只支持目标显示器的底部任务栏。");
        if (environment.NotificationBounds is not PixelRect anchor || !bar.Contains(anchor) || anchor.X <= bar.X)
            return Failure("dock_anchor_unavailable", "无法可靠识别通知区域左缘。");

        var scale = environment.Dpi / 96d;
        var roundedWidth = Math.Round(widthDip * scale, MidpointRounding.AwayFromZero);
        var roundedHeight = Math.Round(heightDip * scale, MidpointRounding.AwayFromZero);
        var roundedGap = Math.Round(rightGapDip * scale, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(roundedWidth) || !double.IsFinite(roundedHeight) ||
            roundedWidth > int.MaxValue || roundedHeight > int.MaxValue)
            return Failure("dock_group_native_range_exceeded", "组尺寸超出原生窗口整数范围。");
        if (roundedWidth < 1 || roundedHeight < 1)
            return Failure("dock_geometry_invalid", "缩放后的组尺寸不足一个像素。");
        if (roundedHeight > bar.Height)
            return Failure("dock_group_height_insufficient", "组高度超过任务栏可用高度。");

        // Gap is at most 64 * uint.MaxValue / 96, so every subtraction below fits Int64.
        // Width/height become integers only after their finite Int32 ranges have been checked.
        var width = (long)roundedWidth;
        var height = (long)roundedHeight;
        var x = (long)anchor.X - (long)roundedGap - width;
        var y = (long)bar.Y + ((long)bar.Height - height) / 2;
        if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue ||
            x + width > int.MaxValue || y + height > int.MaxValue)
            return Failure("dock_group_native_range_exceeded", "组坐标超出原生窗口整数范围。");

        // Horizontal containment is intentionally absent: D-139 preserves the entire accepted group.
        return CoreResult<PixelRect>.Success(new((int)x, (int)y, (int)width, (int)height));
    }

    private static CoreResult<PixelRect> Failure(string code, string message) =>
        CoreResult<PixelRect>.Failure(new(code, message));
}

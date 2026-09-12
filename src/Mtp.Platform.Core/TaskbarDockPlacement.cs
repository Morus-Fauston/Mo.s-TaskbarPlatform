using System;

namespace Mtp.Platform.Core;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public long Right => (long)X + Width;
    public long Bottom => (long)Y + Height;
    public bool IsValid => Width > 0 && Height > 0 && Right <= int.MaxValue && Bottom <= int.MaxValue;
    public bool Contains(PixelRect other) => IsValid && other.IsValid &&
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
}

public readonly record struct DipSize(int Width, int Height);

public sealed record TaskbarDockGeometry(
    string DisplayId, PixelRect DisplayBounds, PixelRect TaskbarBounds, PixelRect? NotificationBounds, uint Dpi);

/// <summary>Pure screen-coordinate geometry. No fallback to the taskbar's right edge is allowed.</summary>
public static class TaskbarDockPlacement
{
    public static CoreResult<PixelRect> Calculate(TaskbarDockGeometry? environment, DipSize size, int rightGapDip)
    {
        if (environment is null || string.IsNullOrWhiteSpace(environment.DisplayId) || !environment.DisplayBounds.IsValid)
            return Failure("dock_display_invalid", "目标显示器不可用。");
        if (size.Width <= 0 || size.Height <= 0 || rightGapDip is < 0 or > 64 || environment.Dpi == 0)
            return Failure("dock_geometry_invalid", "尺寸、DPI 或右侧间距无效。");

        var bar = environment.TaskbarBounds;
        if (!environment.DisplayBounds.Contains(bar) || bar.Width <= bar.Height || bar.Bottom != environment.DisplayBounds.Bottom)
            return Failure("dock_taskbar_unsupported", "当前只支持目标显示器的底部任务栏。");
        if (environment.NotificationBounds is not PixelRect anchor || !bar.Contains(anchor) || anchor.X <= bar.X)
            return Failure("dock_anchor_unavailable", "无法可靠识别通知区域左缘。");

        var width = Math.Round(size.Width * (environment.Dpi / 96d), MidpointRounding.AwayFromZero);
        var height = Math.Round(size.Height * (environment.Dpi / 96d), MidpointRounding.AwayFromZero);
        var gap = Math.Round(rightGapDip * (environment.Dpi / 96d), MidpointRounding.AwayFromZero);
        var x = anchor.X - gap - width;
        if (width < 1 || height < 1 || width > int.MaxValue || height > bar.Height || x < bar.X || x > int.MaxValue)
            return Failure("dock_space_insufficient", "通知区域左侧没有足够空间容纳组件。");
        var rect = new PixelRect((int)x, bar.Y + (bar.Height - (int)height) / 2, (int)width, (int)height);
        return bar.Contains(rect)
            ? CoreResult<PixelRect>.Success(rect)
            : Failure("dock_geometry_invalid", "计算出的组件位置越过任务栏边界。");
    }

    private static CoreResult<PixelRect> Failure(string code, string message) =>
        CoreResult<PixelRect>.Failure(new(code, message));
}

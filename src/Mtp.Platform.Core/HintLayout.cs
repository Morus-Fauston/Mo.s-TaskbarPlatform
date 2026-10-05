using System;

namespace Mtp.Platform.Core;

public enum HintPosition { Default, TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight, Center, LowerCenter }
public enum HintPlacementMode { Independent, Above, Below, GroupReflow }
public sealed record HintLayoutResult(TaskbarDipRect Bounds, HintPlacementMode Mode, TaskbarDipRect? GroupWorkArea = null);

public static class HintLayout
{
    public const double SafetyMarginDip = 16;
    public const double GapDip = 8;
    public static CoreResult<HintLayoutResult> Calculate(TaskbarDipRect workArea, double widthDip, double heightDip,
        HintPosition position = HintPosition.Default, TaskbarDipRect? owner = null)
    {
        if (!FlyoutGroupLayout.ValidRect(workArea) || !ValidSize(widthDip) || !ValidSize(heightDip) || !Enum.IsDefined(position))
            return Failure("InvalidHintLayout", "提示尺寸、位置或工作区无效");
        var safe = new TaskbarDipRect(workArea.X + SafetyMarginDip, workArea.Y + SafetyMarginDip,
            workArea.Width - 2 * SafetyMarginDip, workArea.Height - 2 * SafetyMarginDip);
        if (safe.Width <= 0 || safe.Height <= 0 || heightDip > safe.Height)
            return Failure("NoUsableWorkArea", "工作区无法容纳完整提示");
        if (owner is { } group)
        {
            if (!FlyoutGroupLayout.ValidRect(group) || group.Width < FlyoutGroupLayout.MinimumWidthDip ||
                group.Height < FlyoutGroupLayout.MinimumHeightDip || group.X < safe.X || group.Y < safe.Y ||
                group.Right > safe.Right || group.Bottom > safe.Bottom)
                return Failure("InvalidHintOwner", "所属组矩形超出安全工作区或操作尺寸下限");
            if (group.Y - GapDip - heightDip >= safe.Y)
                return Success(new(group.X, group.Y - GapDip - heightDip, group.Width, heightDip), HintPlacementMode.Above);
            if (group.Bottom + GapDip + heightDip <= safe.Bottom)
                return Success(new(group.X, group.Bottom + GapDip, group.Width, heightDip), HintPlacementMode.Below);
            var reserve = heightDip + GapDip;
            var groupArea = workArea with { Y = workArea.Y + reserve, Height = workArea.Height - reserve };
            var availableHeight = safe.Height - reserve;
            if (availableHeight < FlyoutGroupLayout.MinimumHeightDip)
                return Failure("NoUsableWorkArea", "预留提示后无法保留所属组必要操作区域");
            var projectedHeight = Math.Min(group.Height, availableHeight);
            var projectedTop = Math.Clamp(group.Y, safe.Y + reserve, safe.Bottom - projectedHeight);
            return Success(new(group.X, projectedTop - reserve, group.Width, heightDip), HintPlacementMode.GroupReflow, groupArea);
        }
        if (widthDip > safe.Width) return Failure("NoUsableWorkArea", "工作区无法容纳完整提示宽度");
        var x = position switch
        {
            HintPosition.TopLeft or HintPosition.BottomLeft => safe.X,
            HintPosition.TopRight or HintPosition.BottomRight => safe.Right - widthDip,
            _ => workArea.X + (workArea.Width - widthDip) / 2
        };
        var y = position switch
        {
            HintPosition.TopLeft or HintPosition.TopCenter or HintPosition.TopRight => safe.Y,
            HintPosition.BottomLeft or HintPosition.BottomCenter or HintPosition.BottomRight => safe.Bottom - heightDip,
            HintPosition.Center => workArea.Y + (workArea.Height - heightDip) / 2,
            _ => workArea.Y + workArea.Height * 0.8 - heightDip / 2
        };
        return Success(new(Math.Clamp(x, safe.X, safe.Right - widthDip), Math.Clamp(y, safe.Y, safe.Bottom - heightDip), widthDip, heightDip),
            HintPlacementMode.Independent);
    }
    private static bool ValidSize(double value) => double.IsFinite(value) && value > 0 && value <= 1e9;
    private static CoreResult<HintLayoutResult> Success(TaskbarDipRect bounds, HintPlacementMode mode, TaskbarDipRect? groupWorkArea = null) =>
        CoreResult<HintLayoutResult>.Success(new(bounds, mode, groupWorkArea));
    private static CoreResult<HintLayoutResult> Failure(string code, string message) => CoreResult<HintLayoutResult>.Failure(new(code, message));
}

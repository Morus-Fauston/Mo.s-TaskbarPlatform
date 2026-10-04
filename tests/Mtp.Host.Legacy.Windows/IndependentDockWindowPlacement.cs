using System;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host;

/// <summary>
/// Calculates an absolute virtual-desktop rectangle for an independent dock window.
/// DisplayArea.WorkArea is documented as relative to DisplayArea.OuterBounds. Some
/// runtime combinations have returned an already screen-relative rectangle, so the
/// boundary explicitly classifies that representation before producing screen coordinates.
/// </summary>
public static class IndependentDockWindowPlacement
{
    public static CoreResult<RectInt32> TryCalculate(
        RectInt32 outerBounds,
        RectInt32 workArea,
        SizeInt32 windowSize,
        int margin)
    {
        if (outerBounds.Width <= 0 || outerBounds.Height <= 0)
        {
            return Failure("dock_display_area_invalid", "The target display bounds are invalid.");
        }

        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return Failure("dock_work_area_invalid", "The target display work area is invalid or outside its display bounds.");
        }

        if (windowSize.Width <= 0 || windowSize.Height <= 0 || margin < 0)
        {
            return Failure("dock_window_size_invalid", "The dock window size or margin is invalid.");
        }

        if (windowSize.Width > workArea.Width || windowSize.Height > workArea.Height)
        {
            return Failure("dock_window_too_large", "The dock window cannot fit inside the target work area.");
        }

        RectInt32 absoluteWorkArea;
        if (FitsWithinDisplayCoordinates(workArea, outerBounds.Width, outerBounds.Height))
        {
            absoluteWorkArea = new RectInt32(
                outerBounds.X + workArea.X,
                outerBounds.Y + workArea.Y,
                workArea.Width,
                workArea.Height);
        }
        else if (IsWithin(workArea, outerBounds))
        {
            absoluteWorkArea = workArea;
        }
        else
        {
            return Failure("dock_work_area_invalid", "The target display work area is invalid or outside its display bounds.");
        }
        var workRight = absoluteWorkArea.X + absoluteWorkArea.Width;
        var workBottom = absoluteWorkArea.Y + absoluteWorkArea.Height;
        var x = Math.Clamp(
            workRight - windowSize.Width - margin,
            absoluteWorkArea.X,
            workRight - windowSize.Width);
        var y = Math.Clamp(
            workBottom - windowSize.Height - margin,
            absoluteWorkArea.Y,
            workBottom - windowSize.Height);
        var placement = new RectInt32(x, y, windowSize.Width, windowSize.Height);

        return IsWithin(placement, absoluteWorkArea)
            ? CoreResult<RectInt32>.Success(placement)
            : Failure("dock_window_out_of_bounds", "The calculated dock window rectangle is outside the target work area.");
    }

    private static bool IsWithin(RectInt32 rectangle, RectInt32 bounds) =>
        rectangle.X >= bounds.X &&
        rectangle.Y >= bounds.Y &&
        rectangle.X + rectangle.Width <= bounds.X + bounds.Width &&
        rectangle.Y + rectangle.Height <= bounds.Y + bounds.Height;

    private static bool FitsWithinDisplayCoordinates(RectInt32 rectangle, int width, int height) =>
        rectangle.X >= 0 &&
        rectangle.Y >= 0 &&
        rectangle.X <= width - rectangle.Width &&
        rectangle.Y <= height - rectangle.Height;

    private static CoreResult<RectInt32> Failure(string code, string message) =>
        CoreResult<RectInt32>.Failure(new StructuredError(code, message));
}

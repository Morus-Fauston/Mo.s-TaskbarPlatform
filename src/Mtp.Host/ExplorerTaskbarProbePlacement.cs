using System;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host;

/// <summary>
/// The probe rectangle in taskbar client coordinates plus which anchor produced it.
/// </summary>
public sealed record ExplorerTaskbarProbePlacementResult(RectInt32 ClientRect, string AnchorKind);

/// <summary>
/// Calculates where the probe child window sits inside the taskbar client area.
/// A child window is positioned relative to its parent's client origin, so every value here
/// is a taskbar client coordinate; screen rectangles never enter this function.
/// </summary>
public static class ExplorerTaskbarProbePlacement
{
    public const string TrayNotifyAnchor = "tray_notify_window";

    public static CoreResult<ExplorerTaskbarProbePlacementResult> TryCalculate(
        SizeInt32 taskbarClientSize,
        int? trayLeftEdgeClientX,
        SizeInt32 probeSizeDip,
        uint dpi,
        int marginDip)
    {
        if (taskbarClientSize.Width <= 0 || taskbarClientSize.Height <= 0)
        {
            return Failure("explorer_probe_taskbar_rect_invalid", "The taskbar client rectangle is empty or invalid.");
        }

        if (dpi == 0)
        {
            return Failure("explorer_probe_dpi_unavailable", "The taskbar DPI could not be determined.");
        }

        if (probeSizeDip.Width <= 0 || probeSizeDip.Height <= 0 || marginDip < 0)
        {
            return Failure("explorer_probe_taskbar_rect_invalid", "The probe size or margin is invalid.");
        }

        if (taskbarClientSize.Width <= taskbarClientSize.Height)
        {
            return Failure("explorer_probe_taskbar_orientation_unsupported", "Only a horizontal taskbar is probed.");
        }

        var scale = dpi / 96.0;
        var width = ScaleToPixels(probeSizeDip.Width, scale);
        var height = ScaleToPixels(probeSizeDip.Height, scale);
        var margin = ScaleToPixels(marginDip, scale);

        if (trayLeftEdgeClientX is not int trayX || trayX <= 0 || trayX > taskbarClientSize.Width)
            return Failure("explorer_probe_tray_anchor_unavailable", "The notification area anchor could not be determined.");

        const string anchorKind = TrayNotifyAnchor;
        var x = trayX - margin - width;

        if (x < 0 || height > taskbarClientSize.Height)
        {
            return Failure("explorer_probe_taskbar_too_small", "The taskbar has no room for the probe window.");
        }

        var y = (taskbarClientSize.Height - height) / 2;
        var placement = new RectInt32(x, y, width, height);
        var fits =
            placement.X >= 0 &&
            placement.Y >= 0 &&
            placement.X + placement.Width <= taskbarClientSize.Width &&
            placement.Y + placement.Height <= taskbarClientSize.Height;

        return fits
            ? CoreResult<ExplorerTaskbarProbePlacementResult>.Success(new ExplorerTaskbarProbePlacementResult(placement, anchorKind))
            : Failure("explorer_probe_placement_out_of_bounds", "The calculated probe rectangle is outside the taskbar client area.");
    }

    private static int ScaleToPixels(int dip, double scale) => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);

    private static CoreResult<ExplorerTaskbarProbePlacementResult> Failure(string code, string message) =>
        CoreResult<ExplorerTaskbarProbePlacementResult>.Failure(new StructuredError(code, message));
}

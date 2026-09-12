using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Windows.Graphics;

namespace Mtp.Host;

/// <summary>
/// One observed step of the Explorer taskbar embed probe.
/// </summary>
public sealed record ExplorerTaskbarProbeStep(string Name, bool Succeeded, string? Detail);

/// <summary>
/// The reproducible experiment record produced by a successful probe run.
/// It carries plain rectangles and strings only; no window handles or Win32 types.
/// A report proves one run on one machine and is never a compatibility claim.
/// </summary>
public sealed record ExplorerTaskbarProbeReport(
    DateTimeOffset Timestamp,
    string OsDescription,
    string ProcessArchitecture,
    int DisplayCount,
    uint TaskbarDpi,
    SizeInt32 TaskbarClientSize,
    RectInt32 TaskbarScreenRect,
    int? TrayLeftEdgeClientX,
    string AnchorKind,
    RectInt32 EmbeddedClientRect,
    RectInt32 EmbeddedScreenRect,
    IReadOnlyList<ExplorerTaskbarProbeStep> Steps)
{
    public static readonly IReadOnlyList<string> UnverifiedScope =
    [
        "多屏",
        "DPI 变化",
        "任务栏自动隐藏",
        "Explorer 重启",
        "第三方任务栏工具",
    ];
}

/// <summary>
/// Options for one probe run. Failure injection is a plain switch so the maintainer can
/// observe the failure and fallback path on real Windows without any configurable class name.
/// </summary>
public sealed record ExplorerTaskbarProbeRequest(
    bool SimulateTaskbarUnavailable = false,
    ProbeTransparencyMode TransparencyMode = ProbeTransparencyMode.SolidPaint,
    Mtp.Platform.Core.MaterialKind Material = Mtp.Platform.Core.MaterialKind.Solid,
    double MaterialOpacity = 0.1,
    uint MaterialColorRgb = 0xFFFFFF);

/// <summary>
/// Experimental transparency mechanisms compared by the probe. The acrylic controller is the maintainer-accepted
/// default: on the verification machine the transparent brush rendered opaque on the primary monitor while the
/// controller rendered correctly on both. None of these is a product decision.
/// </summary>
public enum ProbeTransparencyMode
{
    /// <summary>Transparent system backdrop brush, transparent XAML root, DWM frame extended over the client area.</summary>
    Backdrop,

    /// <summary>Same as Backdrop with a semi-transparent red brush, so the glass layer is visible in screenshots.</summary>
    TintDiagnostic,

    /// <summary>Same as Backdrop but the brush alpha is 1 instead of 0; DeskBox's solid mode never uses a fully transparent tint.</summary>
    NearTransparentBackdrop,

    /// <summary>DesktopAcrylicController (thin, zero tint) plus DWM frame extension; the controller path DeskBox uses.</summary>
    AcrylicController,

    /// <summary>
    /// Solid brush (ARGB 0x60202020) plus one GDI paint on the window DC. The GDI paint is what keeps
    /// alpha honoured on every output, because it takes the window out of DirectFlip/MPO promotion.
    /// </summary>
    SolidPaint,
}

public static class ExplorerTaskbarProbeReportFormatter
{
    public const string SuccessHeadline = "探针成功（实验性，不代表正式支持）";

    public static string Format(ExplorerTaskbarProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine(SuccessHeadline);
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"时间：{report.Timestamp:yyyy-MM-dd HH:mm:ss zzz}"));
        builder.AppendLine($"系统：{report.OsDescription}（{report.ProcessArchitecture}）");
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"屏幕数：{report.DisplayCount}；任务栏 DPI：{report.TaskbarDpi}"));
        builder.AppendLine($"任务栏屏幕矩形：{FormatRect(report.TaskbarScreenRect)}；客户区尺寸：{report.TaskbarClientSize.Width}x{report.TaskbarClientSize.Height}");
        builder.AppendLine(report.TrayLeftEdgeClientX is int trayX
            ? string.Create(CultureInfo.InvariantCulture, $"锚点：{report.AnchorKind}（通知区域左边缘 x={trayX}）")
            : $"锚点：{report.AnchorKind}（未识别通知区域）");
        builder.AppendLine($"嵌入矩形（任务栏客户区）：{FormatRect(report.EmbeddedClientRect)}；屏幕坐标：{FormatRect(report.EmbeddedScreenRect)}");
        builder.AppendLine("步骤：");
        foreach (var step in report.Steps)
        {
            builder.Append("  ");
            builder.Append(step.Succeeded ? "[通过] " : "[未通过] ");
            builder.Append(step.Name);
            if (!string.IsNullOrWhiteSpace(step.Detail))
            {
                builder.Append(" — ");
                builder.Append(step.Detail);
            }

            builder.AppendLine();
        }

        builder.Append("未验证：");
        builder.Append(string.Join("、", ExplorerTaskbarProbeReport.UnverifiedScope));
        return builder.ToString();
    }

    private static string FormatRect(RectInt32 rect) =>
        string.Create(CultureInfo.InvariantCulture, $"({rect.X},{rect.Y}) {rect.Width}x{rect.Height}");
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Mtp.Platform.Core;

public enum FlyoutGroupMode { Parallel, Hierarchical, Collapsed }
public sealed record FlyoutPanelMeasure(string TemplateId, double HeightDip, bool PreferParallel = false, double WidthDip = 320);
public sealed record FlyoutPanelPlacement(string TemplateId, TaskbarDipRect Bounds, bool Scrolls);
public sealed record FlyoutGroupLayoutResult(FlyoutGroupMode Mode, TaskbarDipRect Bounds,
    IReadOnlyList<FlyoutPanelPlacement> VisiblePanels, IReadOnlyList<string> CollapsedPanelIds,
    IReadOnlyList<string> Reasons);

public static class FlyoutGroupLayout
{
    public const int MaximumPanels = 16;
    public const double SafetyMarginDip = 16;
    public const double GapDip = 8;
    public const double HeaderHeightDip = 48;
    public const double MinimumWidthDip = 96;
    public const double MinimumHeightDip = HeaderHeightDip + 32;
    public static CoreResult<FlyoutGroupLayoutResult> Calculate(TaskbarDipRect workArea, TaskbarDipRect anchor,
        IReadOnlyList<FlyoutPanelMeasure> panels, string currentTemplateId)
    {
        if (!ValidRect(workArea) || !ValidRect(anchor) || panels is null || panels.Count is < 1 or > MaximumPanels)
            return Failure("InvalidFlyoutLayout", "浮窗工作区、锚点或面板预算无效");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in panels)
            if (panel is null || string.IsNullOrWhiteSpace(panel.TemplateId) || panel.TemplateId.Length > 256 ||
                !ids.Add(panel.TemplateId) || !ValidSize(panel.WidthDip) || !ValidSize(panel.HeightDip))
                return Failure("InvalidFlyoutLayout", "面板身份或测量无效");
        if (!ids.Contains(currentTemplateId)) return Failure("UnknownPanel", "当前面板不属于本组");
        var safe = new TaskbarDipRect(workArea.X + SafetyMarginDip, workArea.Y + SafetyMarginDip,
            workArea.Width - 2 * SafetyMarginDip, workArea.Height - 2 * SafetyMarginDip);
        if (safe.Width < MinimumWidthDip || safe.Height < MinimumHeightDip)
            return Failure("NoUsableWorkArea", "工作区无法容纳关闭、返回及必要操作区域");
        var current = panels.First(x => x.TemplateId == currentTemplateId);
        var totalWidth = panels.Sum(x => Math.Max(MinimumWidthDip, x.WidthDip)) + GapDip * (panels.Count - 1);
        bool parallel = panels.Count > 1 && panels.Skip(1).All(x => x.PreferParallel) && totalWidth <= safe.Width;
        var visible = parallel ? panels.ToArray() : new[] { current };
        var width = parallel ? totalWidth : Math.Min(current.WidthDip, safe.Width);
        var height = Math.Min(visible.Max(x => x.HeightDip), safe.Height);
        height = Math.Max(MinimumHeightDip, height);
        width = Math.Max(MinimumWidthDip, width);
        var x = Math.Clamp(anchor.Right - width, safe.X, safe.Right - width);
        var bottom = Math.Clamp(anchor.Y - GapDip, safe.Y + height, safe.Bottom);
        var placements = new List<FlyoutPanelPlacement>();
        double left = x;
        foreach (var panel in visible)
        {
            var panelWidth = parallel ? Math.Max(MinimumWidthDip, panel.WidthDip) : width;
            var panelHeight = Math.Clamp(panel.HeightDip, MinimumHeightDip, safe.Height);
            placements.Add(new(panel.TemplateId, new(left, bottom - panelHeight, panelWidth, panelHeight), panel.HeightDip > panelHeight));
            left += panelWidth + GapDip;
        }
        var collapsed = panels.Where(panel => visible.All(value => value.TemplateId != panel.TemplateId)).Select(x => x.TemplateId).ToArray();
        bool clamped = current.WidthDip > safe.Width || current.HeightDip > safe.Height;
        var reasons = new List<string>();
        if (!parallel && panels.Count > 1) reasons.Add("ParallelUnavailable");
        if (clamped) reasons.Add("PanelViewportClamped");
        if (collapsed.Length > 0) reasons.Add("RelatedPanelsCollapsed");
        var mode = parallel ? FlyoutGroupMode.Parallel : clamped && collapsed.Length > 0 ? FlyoutGroupMode.Collapsed : FlyoutGroupMode.Hierarchical;
        return CoreResult<FlyoutGroupLayoutResult>.Success(new(mode, new(x, bottom - height, width, height),
            placements.AsReadOnly(), Array.AsReadOnly(collapsed), reasons.AsReadOnly()));
    }

    internal static bool ValidRect(TaskbarDipRect value) => double.IsFinite(value.X) && double.IsFinite(value.Y) &&
        Math.Abs(value.X) <= 1e12 && Math.Abs(value.Y) <= 1e12 && ValidSize(value.Width) && ValidSize(value.Height);
    private static bool ValidSize(double value) => double.IsFinite(value) && value > 0 && value <= 1e9;
    private static CoreResult<FlyoutGroupLayoutResult> Failure(string code, string message) => CoreResult<FlyoutGroupLayoutResult>.Failure(new(code, message));
}

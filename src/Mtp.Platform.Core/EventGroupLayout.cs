using System;
using System.Collections.Generic;
using System.Linq;

namespace Mtp.Platform.Core;

public sealed record EventGroupMeasure(string Key, double WidthDip, double HeightDip, HintPosition Position = HintPosition.Default);
public sealed record EventGroupPlacement(string Key, TaskbarDipRect Bounds, bool Scrolls, bool Overlaps);
public sealed record EventGroupLayoutResult(IReadOnlyList<EventGroupPlacement> Placements, IReadOnlyList<string> Reasons);

/// <summary>Bounded DIP placement only; overlap never ends another group's lifetime.</summary>
public static class EventGroupLayout
{
    public const int MaximumHigherPriorityBounds = 64;
    public const double SafetyMarginDip = 16;
    public const double GapDip = 8;
    public static CoreResult<EventGroupLayoutResult> Calculate(TaskbarDipRect workArea, IReadOnlyList<EventGroupMeasure> groups,
        HintPosition position = HintPosition.BottomLeft, IReadOnlyList<TaskbarDipRect>? higherPriority = null)
    {
        if (!FlyoutGroupLayout.ValidRect(workArea) || groups is null || groups.Count > EventGroupSchedule.MaximumGroupsPerScreen ||
            !Enum.IsDefined(position) || higherPriority is { Count: > MaximumHigherPriorityBounds }) return Invalid();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
            if (group is null || string.IsNullOrWhiteSpace(group.Key) || group.Key.Length > EventGroupSchedule.MaximumKeyLength ||
                group.Key != group.Key.Trim() || !keys.Add(group.Key) || !ValidSize(group.WidthDip) || !ValidSize(group.HeightDip) ||
                !Enum.IsDefined(group.Position)) return Invalid();
        foreach (var bounds in higherPriority ?? []) if (!FlyoutGroupLayout.ValidRect(bounds)) return Invalid();
        var safe = new TaskbarDipRect(workArea.X + SafetyMarginDip, workArea.Y + SafetyMarginDip,
            workArea.Width - SafetyMarginDip * 2, workArea.Height - SafetyMarginDip * 2);
        if (safe.Width < FlyoutGroupLayout.MinimumWidthDip || safe.Height < FlyoutGroupLayout.MinimumHeightDip)
            return CoreResult<EventGroupLayoutResult>.Failure(new("NoUsableWorkArea", "事件组无法保留必要操作区域"));
        var placements = new List<EventGroupPlacement>();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var higher = (higherPriority ?? []).Where(x => OverlapArea(x, safe) > 0).ToArray();
        foreach (var group in groups)
        {
            double width = Math.Clamp(group.WidthDip, FlyoutGroupLayout.MinimumWidthDip, safe.Width);
            double height = Math.Clamp(group.HeightDip, FlyoutGroupLayout.MinimumHeightDip, safe.Height);
            var preference = group.Position == HintPosition.Default ? position : group.Position;
            var preferred = Preferred(workArea, safe, width, height, preference);
            var obstacles = higher.Concat(placements.Select(x => x.Bounds)).ToArray();
            // At most 64 hints + 9 already placed groups. Edge-derived candidate sets are finite;
            // try the requested column first, preserving bottom-up/top-down stacking when it fits.
            var xs = new[] { preferred.X, safe.X, safe.Right - width }
                .Concat(obstacles.SelectMany(x => new[] { x.X - GapDip - width, x.Right + GapDip }))
                .Select(x => Math.Clamp(x, safe.X, safe.Right - width)).Distinct().OrderBy(x => Math.Abs(x - preferred.X)).ToArray();
            var ys = new[] { preferred.Y, safe.Y, safe.Bottom - height }
                .Concat(obstacles.SelectMany(x => new[] { x.Y - GapDip - height, x.Bottom + GapDip }))
                .Select(y => Math.Clamp(y, safe.Y, safe.Bottom - height)).Distinct().OrderBy(y => Math.Abs(y - preferred.Y)).ToArray();
            TaskbarDipRect? chosen = null;
            TaskbarDipRect fallback = preferred;
            double leastHigher = double.PositiveInfinity, leastEvents = double.PositiveInfinity;
            foreach (double x in xs)
            {
                foreach (double y in ys)
                {
                    var candidate = new TaskbarDipRect(x, y, width, height);
                    if (!obstacles.Any(obstacle => Conflicts(candidate, obstacle))) { chosen = candidate; break; }
                    double highArea = higher.Sum(obstacle => OverlapArea(candidate, obstacle));
                    double eventArea = placements.Sum(placement => OverlapArea(candidate, placement.Bounds));
                    if (highArea < leastHigher || highArea == leastHigher && eventArea < leastEvents)
                    { leastHigher = highArea; leastEvents = eventArea; fallback = candidate; }
                }
                if (chosen is not null) break;
            }
            var bounds = chosen ?? fallback;
            bool highOverlap = higher.Any(x => OverlapArea(x, bounds) > 0);
            bool eventOverlap = false;
            for (int i = 0; i < placements.Count; i++)
                if (OverlapArea(placements[i].Bounds, bounds) > 0)
                { eventOverlap = true; placements[i] = placements[i] with { Overlaps = true }; }
            if (width != group.WidthDip || height != group.HeightDip) reasons.Add("EventViewportClamped");
            if (higher.Any(x => Conflicts(preferred, x)) && !highOverlap) reasons.Add("HigherPriorityAvoided");
            if (highOverlap) reasons.Add("HigherPriorityOverlay");
            if (eventOverlap) reasons.Add("EventGroupsOverlap");
            if (chosen is null && !highOverlap && !eventOverlap) reasons.Add("SpacingReduced");
            placements.Add(new(group.Key, bounds, group.HeightDip > height, highOverlap || eventOverlap));
        }
        return CoreResult<EventGroupLayoutResult>.Success(new(placements.AsReadOnly(), Array.AsReadOnly(reasons.ToArray())));
    }
    private static TaskbarDipRect Preferred(TaskbarDipRect work, TaskbarDipRect safe, double width, double height, HintPosition position)
    {
        double x = position switch
        {
            HintPosition.Default or HintPosition.TopLeft or HintPosition.BottomLeft => safe.X,
            HintPosition.TopRight or HintPosition.BottomRight => safe.Right - width,
            _ => work.X + (work.Width - width) / 2
        };
        double y = position switch
        {
            HintPosition.TopLeft or HintPosition.TopCenter or HintPosition.TopRight => safe.Y,
            HintPosition.Center => work.Y + (work.Height - height) / 2,
            HintPosition.LowerCenter => work.Y + work.Height * 0.8 - height / 2,
            _ => safe.Bottom - height
        };
        return new(Math.Clamp(x, safe.X, safe.Right - width), Math.Clamp(y, safe.Y, safe.Bottom - height), width, height);
    }
    private static bool Conflicts(TaskbarDipRect a, TaskbarDipRect b) =>
        a.X < b.Right + GapDip && a.Right + GapDip > b.X && a.Y < b.Bottom + GapDip && a.Bottom + GapDip > b.Y;
    private static double OverlapArea(TaskbarDipRect a, TaskbarDipRect b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
    private static bool ValidSize(double value) => double.IsFinite(value) && value > 0 && value <= 1e9;
    private static CoreResult<EventGroupLayoutResult> Invalid() =>
        CoreResult<EventGroupLayoutResult>.Failure(new("InvalidEventLayout", "事件组位置、尺寸、身份或预算无效"));
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Mtp.Platform.Core;

public sealed record FlyoutRectangleTarget(string Key, TaskbarDipRect Bounds, double Opacity = 1);
public sealed record FlyoutRectangleFrame(long Generation, long Revision, double Progress,
    IReadOnlyList<FlyoutRectangleTarget> Panels, bool IsComplete);

public sealed class FlyoutRectangleAnimation
{
    public long Generation { get; private set; } = 1;
    public long Revision { get; private set; }
    private FlyoutRectangleTarget[] targets = Array.Empty<FlyoutRectangleTarget>();
    private FlyoutRectangleTarget[] origins = Array.Empty<FlyoutRectangleTarget>();
    private TimeSpan lastTime, started;
    private bool running;
    public bool Retarget(IReadOnlyList<FlyoutRectangleTarget> target, TimeSpan now, bool reducedMotion,
        long expectedGeneration, bool forceTransition = false, FlyoutRectangleFrame? presentedFrame = null)
    {
        if (expectedGeneration != Generation) return false;
        if (presentedFrame is not null && (presentedFrame.Generation != Generation || presentedFrame.Revision != Revision)) return false;
        ArgumentNullException.ThrowIfNull(target);
        if (target.Count > FlyoutGroupLayout.MaximumPanels) throw new ArgumentOutOfRangeException(nameof(target));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in target)
            if (panel is null || string.IsNullOrWhiteSpace(panel.Key) || panel.Key.Length > 256 || !keys.Add(panel.Key) ||
                !FlyoutGroupLayout.ValidRect(panel.Bounds) || !double.IsFinite(panel.Opacity) || panel.Opacity is < 0 or > 1)
                throw new ArgumentException("Invalid flyout animation target.", nameof(target));
        if (presentedFrame is not null)
        {
            if (!double.IsFinite(presentedFrame.Progress) || presentedFrame.Progress is < 0 or > 1 ||
                presentedFrame.Panels is null || presentedFrame.Panels.Count > FlyoutGroupLayout.MaximumPanels ||
                presentedFrame.Panels.Count != targets.Length)
                throw new ArgumentException("Invalid presented flyout frame.", nameof(presentedFrame));
            var presentedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var panel in presentedFrame.Panels)
                if (panel is null || string.IsNullOrWhiteSpace(panel.Key) || panel.Key.Length > 256 || !presentedKeys.Add(panel.Key) ||
                    !targets.Any(value => value.Key == panel.Key) || !FlyoutGroupLayout.ValidRect(panel.Bounds) ||
                    !double.IsFinite(panel.Opacity) || panel.Opacity is < 0 or > 1)
                    throw new ArgumentException("Invalid presented flyout panel.", nameof(presentedFrame));
        }
        if (now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now));
        // The UI can be delayed after its last applied frame. Sampling the old trajectory at now
        // would invent a visible jump; a matching presented frame is the authoritative origin.
        var current = presentedFrame ?? Sample(now);
        if (now > lastTime) lastTime = now;
        if (!forceTransition && targets.SequenceEqual(target))
        {
            if (reducedMotion) Finish();
            return true;
        }
        Revision = checked(Revision + 1);
        var prior = current.Panels.ToDictionary(x => x.Key, StringComparer.Ordinal);
        targets = target.ToArray();
        origins = targets.Select(value => prior.GetValueOrDefault(value.Key) ??
            value with { Bounds = value.Bounds with { Y = value.Bounds.Y + 8 }, Opacity = 0 }).ToArray();
        started = lastTime;
        running = targets.Length > 0 && !reducedMotion;
        if (!running) Finish();
        return true;
    }
    public FlyoutRectangleFrame Sample(TimeSpan now)
    {
        if (now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now));
        if (now > lastTime) lastTime = now;
        if (!running) return new(Generation, Revision, 1, Array.AsReadOnly(targets), true);
        var progress = (lastTime - started).TotalMilliseconds / HostAnimationTiming.Duration.TotalMilliseconds;
        if (progress >= 1) { Finish(); return new(Generation, Revision, 1, Array.AsReadOnly(targets), true); }
        var amount = HostAnimationTiming.EaseOutCubic(progress);
        var frame = new FlyoutRectangleTarget[targets.Length];
        for (var i = 0; i < frame.Length; i++)
        {
            var from = origins[i]; var to = targets[i];
            frame[i] = new(to.Key, new(Mix(from.Bounds.X, to.Bounds.X, amount), Mix(from.Bounds.Y, to.Bounds.Y, amount),
                Mix(from.Bounds.Width, to.Bounds.Width, amount), Mix(from.Bounds.Height, to.Bounds.Height, amount)),
                Mix(from.Opacity, to.Opacity, amount));
        }
        return new(Generation, Revision, amount, Array.AsReadOnly(frame), false);
    }
    public void Clear()
    {
        Generation = checked(Generation + 1); Revision = checked(Revision + 1);
        targets = Array.Empty<FlyoutRectangleTarget>(); Finish(); lastTime = started = TimeSpan.Zero;
    }
    private void Finish() { running = false; origins = Array.Empty<FlyoutRectangleTarget>(); }
    private static double Mix(double from, double to, double amount) => from + (to - from) * amount;
}

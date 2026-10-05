using System;
using System.Collections.Generic;
using System.Linq;

namespace Mtp.Platform.Core;

public sealed record TaskbarAnimatedItem(TaskbarItemKey Key, TaskbarDipRect Bounds, double Opacity,
    bool IsInteractive, bool IsExiting);
public sealed record TaskbarGroupAnimationFrame(long Generation, long Revision, double WidthDip, double HeightDip,
    IReadOnlyList<TaskbarComponentPlacement> Components, IReadOnlyList<TaskbarAnimatedItem> Items,
    bool IsComplete, bool AnimationBudgetReduced);

/// <summary>Owned by one render driver; callers supply monotonic time. No timers, tasks or native objects.</summary>
public sealed class TaskbarGroupAnimation
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(220);
    public const int MaximumRetiredItems = 2048;
    public long Generation { get; private set; } = 1;
    public long Revision { get; private set; }
    private TaskbarGroupLayoutResult _target = TaskbarGroupLayout.Calculate(Array.Empty<TaskbarMeasuredComponent>(), 32);
    private ItemTrack[] _items = Array.Empty<ItemTrack>();
    private ComponentTrack[] _components = Array.Empty<ComponentTrack>();
    private TimeSpan _started;
    private TimeSpan _lastTime;
    private double _fromWidth;
    private double _fromHeight = 32;
    private bool _running;
    private bool _budgetReduced;
    private long _retirementSequence;

    public bool Retarget(TaskbarGroupLayoutResult target, TimeSpan now, bool reducedMotion, long expectedGeneration)
    {
        // Do not even advance the current clock for an obsolete owner callback.
        if (expectedGeneration != Generation) return false;
        ArgumentNullException.ThrowIfNull(target);
        var current = Sample(now);
        Revision = checked(Revision + 1);
        if (SameGeometry(_target, target))
        {
            _target = target;
            var interactions = target.Items.ToDictionary(x => x.Key, x => x.IsInteractive);
            foreach (var track in _items)
                track.IsInteractive = interactions.GetValueOrDefault(track.Key);
            if (reducedMotion) Finish();
            return true;
        }

        var previousItems = current.Items.ToDictionary(x => x.Key);
        var previousRetirement = _items.Where(x => x.IsExiting).ToDictionary(x => x.Key, x => x.RetirementSequence);
        var tracks = new List<ItemTrack>(target.Items.Count + Math.Min(current.Items.Count, MaximumRetiredItems));
        var liveKeys = new HashSet<TaskbarItemKey>();
        foreach (var item in target.Items)
        {
            liveKeys.Add(item.Key);
            var from = previousItems.GetValueOrDefault(item.Key);
            tracks.Add(new(item.Key, from?.Bounds ?? Collapsed(item.Bounds), item.Bounds,
                from?.Opacity ?? 0, 1, item.IsInteractive, false, 0));
        }
        var retired = new List<ItemTrack>();
        foreach (var item in current.Items)
        {
            if (liveKeys.Contains(item.Key) || item.Opacity <= 0) continue;
            var sequence = previousRetirement.TryGetValue(item.Key, out var prior)
                ? prior : checked(++_retirementSequence);
            retired.Add(new(item.Key, item.Bounds, Collapsed(item.Bounds), item.Opacity, 0, false, true, sequence));
        }
        if (retired.Count > MaximumRetiredItems)
        {
            _budgetReduced = true;
            retired = retired.OrderByDescending(x => x.RetirementSequence).Take(MaximumRetiredItems).ToList();
        }
        tracks.AddRange(retired);

        var previousComponents = current.Components.ToDictionary(x => x.Key);
        var componentTracks = new List<ComponentTrack>(target.Components.Count + retired.Count);
        var componentKeys = new HashSet<TaskbarComponentKey>();
        foreach (var component in target.Components)
        {
            componentKeys.Add(component.Key);
            componentTracks.Add(new(component.Key,
                previousComponents.GetValueOrDefault(component.Key)?.Bounds ?? Collapsed(component.Bounds), component.Bounds));
        }
        // Only ancestors of the bounded outgoing visuals remain; retired static declarations do not accumulate.
        foreach (var componentKey in retired.Select(x => x.Key.Component).Distinct())
            if (componentKeys.Add(componentKey) && previousComponents.TryGetValue(componentKey, out var previous))
                componentTracks.Add(new(componentKey, previous.Bounds, Collapsed(previous.Bounds)));

        _fromWidth = current.WidthDip;
        _fromHeight = current.WidthDip == 0 ? target.HeightDip : current.HeightDip;
        _target = target;
        _items = tracks.ToArray();
        _components = componentTracks.ToArray();
        _started = _lastTime;
        _running = true;
        if (reducedMotion) Finish();
        return true;
    }

    public TaskbarGroupAnimationFrame Sample(TimeSpan now)
    {
        if (now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now));
        // A delayed sampling callback cannot reverse the displayed timeline.
        if (now > _lastTime) _lastTime = now;
        if (!_running) return FinalFrame();
        var progress = Math.Min(1, (_lastTime - _started).TotalMilliseconds / Duration.TotalMilliseconds);
        if (progress >= 1)
        {
            Finish();
            return FinalFrame();
        }
        var amount = 1 - Math.Pow(1 - progress, 3);
        var components = _components.Select(x => new TaskbarComponentPlacement(x.Key, Interpolate(x.From, x.To, amount))).ToArray();
        var items = _items.Select(x =>
        {
            var bounds = Interpolate(x.From, x.To, amount);
            var opacity = Mix(x.FromOpacity, x.ToOpacity, amount);
            return new TaskbarAnimatedItem(x.Key, bounds, opacity,
                !x.IsExiting && x.IsInteractive && bounds.Width > 0 && opacity > 0, x.IsExiting);
        }).ToArray();
        return new(Generation, Revision, Mix(_fromWidth, _target.WidthDip, amount),
            Mix(_fromHeight, _target.HeightDip, amount), Array.AsReadOnly(components), Array.AsReadOnly(items), false, _budgetReduced);
    }

    /// <summary>Invalidates outstanding producers/frames and releases every live/retired geometry reference.</summary>
    public void Clear()
    {
        Generation = checked(Generation + 1);
        Revision = checked(Revision + 1);
        _target = TaskbarGroupLayout.Calculate(Array.Empty<TaskbarMeasuredComponent>(), 32);
        Finish();
        _lastTime = _started = TimeSpan.Zero;
        _fromWidth = 0;
        _fromHeight = 32;
        _budgetReduced = false;
        _retirementSequence = 0;
    }

    private void Finish()
    {
        _running = false;
        _items = Array.Empty<ItemTrack>();
        _components = Array.Empty<ComponentTrack>();
    }

    private TaskbarGroupAnimationFrame FinalFrame() => new(Generation, Revision, _target.WidthDip, _target.HeightDip,
        _target.Components, Array.AsReadOnly(_target.Items.Select(x =>
            new TaskbarAnimatedItem(x.Key, x.Bounds, 1, x.IsInteractive, false)).ToArray()), true, _budgetReduced);

    private static bool SameGeometry(TaskbarGroupLayoutResult a, TaskbarGroupLayoutResult b)
    {
        if (a.WidthDip != b.WidthDip || a.HeightDip != b.HeightDip || a.Components.Count != b.Components.Count ||
            a.Items.Count != b.Items.Count) return false;
        for (var i = 0; i < a.Components.Count; i++)
            if (a.Components[i] != b.Components[i]) return false;
        for (var i = 0; i < a.Items.Count; i++)
            if (a.Items[i].Key != b.Items[i].Key || a.Items[i].Bounds != b.Items[i].Bounds) return false;
        return true;
    }

    private static TaskbarDipRect Collapsed(TaskbarDipRect rect) => new(rect.Right, rect.Y, 0, rect.Height);
    private static double Mix(double from, double to, double amount) => from + (to - from) * amount;
    private static TaskbarDipRect Interpolate(TaskbarDipRect from, TaskbarDipRect to, double amount) =>
        new(Mix(from.X, to.X, amount), Mix(from.Y, to.Y, amount), Mix(from.Width, to.Width, amount), Mix(from.Height, to.Height, amount));

    private sealed class ItemTrack(TaskbarItemKey key, TaskbarDipRect from, TaskbarDipRect to,
        double fromOpacity, double toOpacity, bool isInteractive, bool isExiting, long retirementSequence)
    {
        public TaskbarItemKey Key { get; } = key;
        public TaskbarDipRect From { get; } = from;
        public TaskbarDipRect To { get; } = to;
        public double FromOpacity { get; } = fromOpacity;
        public double ToOpacity { get; } = toOpacity;
        public bool IsInteractive { get; set; } = isInteractive;
        public bool IsExiting { get; } = isExiting;
        public long RetirementSequence { get; } = retirementSequence;
    }
    private sealed record ComponentTrack(TaskbarComponentKey Key, TaskbarDipRect From, TaskbarDipRect To);
}

using System;
using System.Collections.Generic;

namespace Mtp.Platform.Core;

public enum FlyoutLifetimeKind { TaskbarGroup, ShortHint, InteractiveHint, EventGroup }

public sealed class FlyoutLifetime
{
    public const int MaximumInteractionSources = 16;
    private readonly long generation;
    private readonly FlyoutLifetimeKind kind;
    private readonly TimeProvider clock;
    private readonly TimeSpan? duration;
    private readonly HashSet<string> sources = new(StringComparer.Ordinal);
    private bool visible;
    private long idleSince, lastTimestamp;
    private long? lastInteractionEnded;

    /// <summary>One owner calls this timer-free policy with its current instance generation.</summary>
    public FlyoutLifetime(long generation, FlyoutLifetimeKind kind, bool persistent = false, TimeProvider? clock = null)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (persistent && kind != FlyoutLifetimeKind.EventGroup) throw new ArgumentException("Only an event group can declare persistence.", nameof(persistent));
        this.generation = generation; this.kind = kind; this.clock = clock ?? TimeProvider.System;
        lastTimestamp = this.clock.GetTimestamp();
        duration = kind switch
        {
            FlyoutLifetimeKind.ShortHint => TimeSpan.FromSeconds(3),
            FlyoutLifetimeKind.InteractiveHint => TimeSpan.FromSeconds(5),
            FlyoutLifetimeKind.EventGroup when !persistent => TimeSpan.FromSeconds(8),
            _ => null
        };
    }
    public bool MarkVisible(long generation)
    {
        if (generation != this.generation) return false;
        if (!visible) { visible = true; idleSince = Now(); }
        return true;
    }
    /// <summary>Only a new valid request/event/error calls Refresh; layout and state reads do not.</summary>
    public bool Refresh(long generation)
    {
        if (generation != this.generation) return false;
        if (visible) idleSince = Now();
        return true;
    }
    public bool SetInteraction(long generation, string source, bool active)
    {
        if (generation != this.generation || !visible || string.IsNullOrWhiteSpace(source) || source.Length > 256 || source != source.Trim()) return false;
        if (kind is FlyoutLifetimeKind.ShortHint or FlyoutLifetimeKind.TaskbarGroup) return true;
        if (active)
        {
            if (sources.Contains(source)) return true;
            if (sources.Count >= MaximumInteractionSources) return false;
            sources.Add(source);
        }
        else
        {
            if (!sources.Remove(source)) return false;
            if (sources.Count == 0) { idleSince = Now(); lastInteractionEnded = idleSince; }
        }
        return true;
    }
    public bool IsExpired(long generation) => generation == this.generation && visible && duration is { } limit &&
        sources.Count == 0 && clock.GetElapsedTime(idleSince, Now()) >= limit;
    public bool IsProtected(long generation) => generation == this.generation && visible && kind == FlyoutLifetimeKind.EventGroup &&
        (sources.Count > 0 || lastInteractionEnded is { } ended && clock.GetElapsedTime(ended, Now()) < TimeSpan.FromSeconds(5));

    private long Now()
    {
        var value = clock.GetTimestamp();
        if (value > lastTimestamp) lastTimestamp = value;
        return lastTimestamp;
    }
}

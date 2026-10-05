using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

public sealed record PresetTextMeasurement(TaskbarItemKey Key, double TextWidthDip, double ViewportWidthDip);
public sealed record PresetMotionReading(TaskbarItemKey Key, double? ProgressFraction, bool IsIndeterminate,
    double BusyPhase, double TextOffsetDip, bool IsAnimating);

/// <summary>One screen's bounded content tracks, sampled by the Host's existing render driver.</summary>
public sealed class PresetMotionSampler
{
    public const int MaximumTracks = 2048;
    public const double ScrollSpeedDipPerSecond = 24;
    public static readonly TimeSpan ScrollPause = TimeSpan.FromSeconds(1);
    private Dictionary<TaskbarItemKey, Track> tracks = [];
    private TimeSpan lastTime;
    public ProtocolResult Synchronize(IReadOnlyList<HostItemPresentation> items, IReadOnlyList<PresetTextMeasurement> measurements,
        TimeSpan now, bool reducedMotion)
    {
        if (items is null || measurements is null || now < TimeSpan.Zero)
            return ProtocolResult.Reject("preset_input_invalid", "预置内容或单调时间缺失");
        if (items.Count > MaximumTracks || measurements.Count > MaximumTracks)
            return ProtocolResult.Reject("preset_budget_exceeded", "预置内容轨道数量超限");
        var time = now > lastTime ? now : lastTime;
        var sizes = new Dictionary<TaskbarItemKey, PresetTextMeasurement>();
        foreach (var size in measurements)
            if (size?.Key is null || !ValidDimension(size.TextWidthDip) || !ValidDimension(size.ViewportWidthDip) || !sizes.TryAdd(size.Key, size))
                return ProtocolResult.Reject("preset_measurement_invalid", "文本测量缺失、重复或超限");
        var next = new Dictionary<TaskbarItemKey, Track>();
        var identities = new HashSet<DynamicItemIdentity>();
        foreach (var item in items)
        {
            if (item?.Handle?.Item is not { } identity || item.Item?.Fields is null || item.Presentation is null || item.Structure is null ||
                !ValidId(identity.ApplicationId) || !ValidId(identity.FeatureGroupId) || !ValidId(identity.ComponentId) ||
                !ValidId(identity.ItemId) || identity.ItemId != item.Item.ItemId || item.PresenceGeneration < 0 ||
                !ValidId(item.SessionId) || !ValidId(item.Handle.ScreenId) || !identities.Add(identity) ||
                !Enum.IsDefined(item.Structure.Overflow) || !Enum.IsDefined(item.Structure.Animation))
                return ProtocolResult.Reject("preset_input_invalid", "预置内容身份、会话或呈现无效");
            var id = item.Handle.Item;
            var key = new TaskbarItemKey(new(id.ApplicationId, id.FeatureGroupId, id.ComponentId), id.ItemId, item.PresenceGeneration);
            var progress = item.Item.Fields.Progress;
            if (progress is not null && (!Enum.IsDefined(progress.Mode) || (progress.Mode == ProgressMode.Indeterminate
                ? progress.Value is not null || progress.Maximum is not null
                : progress.Value is not { } actual || progress.Maximum is not { } total || !double.IsFinite(actual) || !double.IsFinite(total) || total <= 0 || actual < 0 || actual > total)))
                return ProtocolResult.Reject("preset_progress_invalid", "进度缺少明确有效依据");
            bool graph = item.Presentation.Fields.HasFlag(ContentFields.Progress) && item.Presentation.Variant != PresetVariant.Text;
            double? target = graph && progress is { Mode: ProgressMode.Determinate, Value: { } value, Maximum: { } maximum } ? value / maximum : null;
            bool busy = graph && progress?.Mode == ProgressMode.Indeterminate;
            double extent = sizes.TryGetValue(key, out var size) && item.Structure.Overflow == TextOverflow.Scroll
                ? Math.Max(0, size.TextWidthDip - size.ViewportWidthDip) : 0;
            string text = item.Item.Fields.Status?.Text ?? item.Item.Fields.Counter?.ToString() ?? item.Item.Fields.Timer?.ToString() ?? "";
            if (text.Length > 4096) return ProtocolResult.Reject("preset_input_invalid", "文本超出采样预算");
            tracks.TryGetValue(key, out var previous);
            if (previous?.Session != item.SessionId || previous?.Owner != item.Handle.Owner || previous?.Screen != item.Handle.ScreenId) previous = null;
            var reading = previous is null ? null : Read(key, previous, time, false);
            bool instant = reducedMotion || item.Structure.Animation == SemanticAnimation.None;
            var from = instant ? target : previous is not null && previous.Target == target ? previous.From : reading?.ProgressFraction ?? target;
            var started = previous is not null && previous.Target == target ? previous.Started : time;
            var scrollStarted = previous is not null && previous.Text == text && previous.Extent == extent ? previous.ScrollStarted : time;
            var busyStarted = previous is not null && previous.Busy == busy ? previous.BusyStarted : time;
            next.Add(key, new(item.SessionId, item.Handle.Owner, item.Handle.ScreenId, target, from, started,
                busy, busyStarted, extent, text, scrollStarted));
        }
        if (sizes.Keys.Any(key => !next.ContainsKey(key))) return ProtocolResult.Reject("preset_measurement_invalid", "测量项不在当前可见集合");
        tracks = next;
        lastTime = time;
        return ProtocolResult.Success();
    }
    public IReadOnlyList<PresetMotionReading> Sample(TimeSpan now, bool reducedMotion)
    {
        if (now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now));
        if (now > lastTime) lastTime = now;
        if (reducedMotion)
            foreach (var key in tracks.Keys.ToArray()) tracks[key] = tracks[key] with { From = tracks[key].Target };
        return Array.AsReadOnly(tracks.Select(pair => Read(pair.Key, pair.Value, lastTime, reducedMotion)).ToArray());
    }
    public void Clear() { tracks.Clear(); lastTime = TimeSpan.Zero; }

    private static PresetMotionReading Read(TaskbarItemKey key, Track track, TimeSpan time, bool reducedMotion)
    {
        double progress = Math.Min(1, (time - track.Started).TotalMilliseconds / HostAnimationTiming.Duration.TotalMilliseconds);
        double? value = track.Target is { } target && track.From is { } from
            ? from + (target - from) * HostAnimationTiming.EaseOutCubic(progress) : track.Target;
        double offset = 0;
        if (!reducedMotion && track.Extent > 0)
        {
            double journey = track.Extent / ScrollSpeedDipPerSecond;
            double phase = (time - track.ScrollStarted).TotalSeconds % (2 * journey + 2 * ScrollPause.TotalSeconds);
            offset = phase <= 1 ? 0 : phase <= 1 + journey ? -(phase - 1) * ScrollSpeedDipPerSecond :
                phase <= 2 + journey ? -track.Extent : -track.Extent + (phase - 2 - journey) * ScrollSpeedDipPerSecond;
        }
        double busyPhase = track.Busy && !reducedMotion ? (time - track.BusyStarted).TotalMilliseconds % 1200 / 1200 : 0;
        return new(key, reducedMotion ? track.Target : value, track.Busy, busyPhase, offset,
            !reducedMotion && (track.Busy || track.Extent > 0 || progress < 1 && track.From != track.Target));
    }
    private static bool ValidDimension(double value) => double.IsFinite(value) && value >= 0 && value <= TaskbarGroupLayout.MaximumDimensionDip;
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= DeclarationValidator.MaximumIdLength && value == value.Trim();
    private sealed record Track(string Session, Guid Owner, string Screen, double? Target, double? From, TimeSpan Started,
        bool Busy, TimeSpan BusyStarted, double Extent, string Text, TimeSpan ScrollStarted);
}

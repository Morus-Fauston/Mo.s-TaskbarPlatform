using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

public sealed record TimerDisplayReading(TaskbarItemKey Key, double ValueMilliseconds, string Text,
    double? ProgressFraction, bool IsPaused, bool IsOvertime, bool IsAdvancing);

/// <summary>Screen-owned local display samples; no business updates, native objects or timer loops.</summary>
public sealed class TimerPresentationSampler
{
    public const int MaximumTimers = 2048;
    private Dictionary<TaskbarItemKey, TimerTrack> _tracks = [];
    private TimeSpan _lastTime;

    public ProtocolResult Synchronize(IReadOnlyList<HostItemPresentation> visibleItems,
        DateTimeOffset utcNow, TimeSpan monotonicNow)
    {
        if (visibleItems is null || monotonicNow < TimeSpan.Zero)
            return ProtocolResult.Reject("timer_input_invalid", "计时可见项或单调时间无效");
        if (visibleItems.Count > MaximumTimers)
            return ProtocolResult.Reject("timer_budget_exceeded", "可见计时项超出本地采样预算");
        var time = monotonicNow > _lastTime ? monotonicNow : _lastTime;
        var next = new Dictionary<TaskbarItemKey, TimerTrack>();
        var identities = new HashSet<DynamicItemIdentity>();
        foreach (var item in visibleItems)
        {
            if (item?.Handle?.Item is not { } identity || item.Item?.Fields is null || item.Presentation is null ||
                !ValidId(identity.ApplicationId) || !ValidId(identity.FeatureGroupId) || !ValidId(identity.ComponentId) ||
                !ValidId(identity.ItemId) || item.Item.ItemId != identity.ItemId || item.PresenceGeneration < 0 ||
                !ValidId(item.SessionId) || string.IsNullOrWhiteSpace(item.Handle.ScreenId) ||
                item.Handle.ScreenId.Length > ItemPresentationLimits.MaximumScreenIdLength || !identities.Add(identity))
                return ProtocolResult.Reject("timer_input_invalid", "计时项身份缺失、重复或无效");
            if (!item.Presentation.Fields.HasFlag(ContentFields.Timer)) continue;
            if (item.Item.Fields.Timer is not { } basis || !DynamicContentValidator.ValidTimerBasis(basis))
                return ProtocolResult.Reject("timer_basis_invalid", "计时基准无效");
            var key = new TaskbarItemKey(new(identity.ApplicationId, identity.FeatureGroupId, identity.ComponentId),
                identity.ItemId, item.PresenceGeneration);
            if (_tracks.TryGetValue(key, out var current) && current.Basis == basis &&
                current.SessionId == item.SessionId && current.Owner == item.Handle.Owner && current.ScreenId == item.Handle.ScreenId)
            {
                next.Add(key, current);
                continue;
            }
            var direction = basis.Direction == TimerDirection.CountUp ? 1 : -1;
            next.Add(key, new(basis, item.SessionId, item.Handle.Owner, item.Handle.ScreenId, time, basis.ValueMillisecondsAtReference +
                (basis.IsPaused ? 0 : direction * (utcNow - basis.ReferenceUtc).TotalMilliseconds)));
        }
        _tracks = next;
        _lastTime = time;
        return ProtocolResult.Success();
    }

    public IReadOnlyList<TimerDisplayReading> Sample(TimeSpan monotonicNow)
    {
        if (monotonicNow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(monotonicNow));
        // Delayed render callbacks cannot reverse a timeline already shown to the user.
        if (monotonicNow > _lastTime) _lastTime = monotonicNow;
        return Array.AsReadOnly(_tracks.Select(pair =>
        {
            var track = pair.Value;
            var direction = track.Basis.Direction == TimerDirection.CountUp ? 1 : -1;
            var value = track.ValueAtAnchor + (track.Basis.IsPaused ? 0 : direction * (_lastTime - track.Anchor).TotalMilliseconds);
            var maximum = TimeSpan.MaxValue.TotalMilliseconds;
            value = Math.Clamp(value, direction == -1 && track.Basis.ShowOvertime ? -maximum : 0, maximum);
            var overtime = direction == -1 && value < 0;
            var seconds = (long)(direction == -1 && !overtime ? Math.Ceiling(value / 1000) : Math.Floor(Math.Abs(value) / 1000));
            var clock = seconds < 3600
                ? FormattableString.Invariant($"{seconds / 60:00}:{seconds % 60:00}")
                : FormattableString.Invariant($"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}");
            var text = (overtime ? "超时 " : "") + clock;
            double? fraction = track.Basis.ProgressDurationMilliseconds is { } duration
                ? Math.Clamp(direction == 1 ? value / duration : 1 - value / duration, 0, 1) : null;
            var advancing = !track.Basis.IsPaused && (direction == 1 ? value < maximum :
                track.Basis.ShowOvertime ? value > -maximum : value > 0);
            return new TimerDisplayReading(pair.Key, value, text, fraction, track.Basis.IsPaused, overtime, advancing);
        }).ToArray());
    }

    public void Clear()
    {
        _tracks.Clear();
        _lastTime = TimeSpan.Zero;
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= DeclarationValidator.MaximumIdLength && value == value.Trim();

    private sealed record TimerTrack(TimerBasis Basis, string SessionId, Guid Owner, string ScreenId,
        TimeSpan Anchor, double ValueAtAnchor);
}

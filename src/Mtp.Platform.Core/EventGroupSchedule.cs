using System;
using System.Collections.Generic;
using System.Linq;

namespace Mtp.Platform.Core;

public sealed record EventGroupRequest(string Key, string ScreenId, string SessionId, bool Persistent = false);
public sealed record EventGroupIdentity(string Key, string ScreenId, string SessionId, long Generation);
public enum EventGroupProposalKind { Create, Refresh, Replace }
public sealed class EventGroupProposal
{
    internal EventGroupProposal(EventGroupRequest request, EventGroupIdentity identity, EventGroupProposalKind kind, EventGroupIdentity? victim)
    { Request = request; Identity = identity; Kind = kind; Victim = victim; }
    public EventGroupRequest Request { get; }
    public EventGroupIdentity Identity { get; }
    public EventGroupProposalKind Kind { get; }
    public EventGroupIdentity? Victim { get; }
}
public sealed record EventGroupObservation(EventGroupIdentity Identity, bool Persistent, long? FirstShownOrder,
    bool Closing, bool Protected, bool Expired);

/// <summary>Single-owner, timer-free scheduling. Only confirmed native destruction releases a slot.</summary>
public sealed class EventGroupSchedule
{
    public const int MaximumScreens = 16;
    public const int MaximumGroupsPerScreen = 10;
    public const int MaximumKeyLength = 2048;
    private readonly TimeProvider clock;
    private readonly Dictionary<EventGroupIdentity, Entry> entries = [];
    private readonly Dictionary<string, EventGroupProposal> pending = new(StringComparer.Ordinal);
    private long generation, shownOrder;
    public int Limit { get; private set; }
    public EventGroupSchedule(int limit = 5, TimeProvider? clock = null)
    {
        if (limit is < 1 or > MaximumGroupsPerScreen) throw new ArgumentOutOfRangeException(nameof(limit));
        Limit = limit; this.clock = clock ?? TimeProvider.System;
    }

    public CoreResult<EventGroupProposal> Prepare(EventGroupRequest request)
    {
        if (request is null || !ValidId(request.Key, MaximumKeyLength) || !ValidId(request.ScreenId, 256) || !ValidId(request.SessionId, 256))
            return CoreResult<EventGroupProposal>.Failure(new("InvalidEventIdentity", "事件完整身份、会话或屏幕无效"));
        var existing = entries.Values.FirstOrDefault(x => x.Request.Key == request.Key);
        if (existing is not null && existing.Request.SessionId != request.SessionId)
            return CoreResult<EventGroupProposal>.Failure(new("SessionCleanupRequired", "同通道旧会话窗口必须先确认清理"));
        // A live channel keeps the screen on which it first opened, even when its trigger moves.
        if (existing is not null) request = request with { ScreenId = existing.Identity.ScreenId };
        if (pending.ContainsKey(request.ScreenId) || pending.Values.Any(x => x.Request.Key == request.Key))
            return CoreResult<EventGroupProposal>.Failure(new("Busy", "本屏已有未结束的窗口事务"));
        var screens = entries.Keys.Select(x => x.ScreenId).Concat(pending.Keys).Distinct(StringComparer.Ordinal).ToArray();
        if (!screens.Contains(request.ScreenId, StringComparer.Ordinal) && screens.Length >= MaximumScreens)
            return CoreResult<EventGroupProposal>.Failure(new("ScreenCapacityExceeded", "事件屏幕数量已达预算"));
        if (existing?.Closing == true)
            return CoreResult<EventGroupProposal>.Failure(new("CleanupPending", "同通道窗口正在关闭"));
        if (existing is not null && existing.Request.Persistent != request.Persistent)
            return CoreResult<EventGroupProposal>.Failure(new("EventPolicyChanged", "关闭策略变化必须先结束旧组"));
        var screen = entries.Values.Where(x => x.Request.ScreenId == request.ScreenId).ToArray();
        if (existing is null && screen.Length > Limit)
            return CoreResult<EventGroupProposal>.Failure(new("Converging", "本屏正在收敛至新上限，暂不新增通道"));
        Entry? victim = null;
        if (existing is null && screen.Length >= Limit)
        {
            if (screen.Any(x => x.Closing)) return CoreResult<EventGroupProposal>.Failure(new("CleanupPending", "本屏已有窗口等待实际销毁"));
            victim = screen.Where(x => !x.Closing && x.FirstShownOrder is not null && !x.Lifetime.IsProtected(x.Identity.Generation))
                .OrderBy(x => x.FirstShownOrder).FirstOrDefault();
            if (victim is null) return CoreResult<EventGroupProposal>.Failure(new("AllProtected", "没有可替换的已显示事件组"));
        }
        if (existing is null && generation == long.MaxValue)
            return CoreResult<EventGroupProposal>.Failure(new("GenerationExhausted", "事件窗口代次预算已耗尽"));
        var identity = existing?.Identity ?? new(request.Key, request.ScreenId, request.SessionId, ++generation);
        var proposal = new EventGroupProposal(request, identity,
            existing is not null ? EventGroupProposalKind.Refresh : victim is null ? EventGroupProposalKind.Create : EventGroupProposalKind.Replace,
            victim?.Identity);
        pending[request.ScreenId] = proposal;
        return CoreResult<EventGroupProposal>.Success(proposal);
    }
    public CoreResult<EventGroupIdentity> Commit(EventGroupProposal proposal)
    {
        if (!Current(proposal))
            return CoreResult<EventGroupIdentity>.Failure(new("StaleProposal", "调度提案已失效"));
        if (proposal.Victim is { } victim && entries.ContainsKey(victim))
            return CoreResult<EventGroupIdentity>.Failure(new("CleanupPending", "旧窗口尚未确认销毁"));
        if (proposal.Kind == EventGroupProposalKind.Refresh)
        {
            if (!entries.TryGetValue(proposal.Identity, out var existing) || existing.Closing)
                return CoreResult<EventGroupIdentity>.Failure(new("StaleProposal", "待刷新窗口已结束"));
            existing.Lifetime.Refresh(existing.Identity.Generation);
        }
        else
        {
            if (entries.Keys.Count(x => x.ScreenId == proposal.Identity.ScreenId) >= Limit)
                return CoreResult<EventGroupIdentity>.Failure(new("Converging", "实际窗口数量尚未满足新建条件"));
            entries.Add(proposal.Identity, new(proposal, clock));
        }
        pending.Remove(proposal.Request.ScreenId);
        return CoreResult<EventGroupIdentity>.Success(proposal.Identity);
    }
    public bool MarkVisible(EventGroupIdentity identity)
    {
        if (identity is null || !entries.TryGetValue(identity, out var entry) || entry.Closing) return false;
        if (entry.FirstShownOrder is null && shownOrder == long.MaxValue) return false;
        entry.FirstShownOrder ??= ++shownOrder;
        return entry.Lifetime.MarkVisible(identity.Generation);
    }
    public bool SetInteraction(EventGroupIdentity identity, string source, bool active) =>
        identity is not null && entries.TryGetValue(identity, out var entry) && !entry.Closing && entry.Lifetime.SetInteraction(identity.Generation, source, active);
    public CoreResult<bool> BeginEviction(EventGroupProposal proposal)
    {
        if (!Current(proposal) || proposal.Victim is not { } victim || !entries.TryGetValue(victim, out var entry))
            return CoreResult<bool>.Failure(new("StaleProposal", "替换提案或旧窗口已失效"));
        if (entry.Lifetime.IsProtected(victim.Generation)) return CoreResult<bool>.Failure(new("AllProtected", "旧窗口已进入操作保护期"));
        entry.Closing = true;
        return CoreResult<bool>.Success(true);
    }
    /// <summary>Call only after the owning adapter has verified actual resource destruction.</summary>
    public bool ConfirmDestroyed(EventGroupIdentity identity)
    {
        if (identity is null || !entries.Remove(identity)) return false;
        if (pending.TryGetValue(identity.ScreenId, out var proposal) && proposal.Identity == identity) pending.Remove(identity.ScreenId);
        return true;
    }
    public bool Cancel(EventGroupProposal proposal) => Current(proposal) && pending.Remove(proposal.Request.ScreenId);
    public CoreResult<bool> SetLimit(int limit)
    {
        if (limit is < 1 or > MaximumGroupsPerScreen) return CoreResult<bool>.Failure(new("InvalidLimit", "每屏事件组上限必须为1至10"));
        if (Limit != limit) { Limit = limit; pending.Clear(); }
        return CoreResult<bool>.Success(true);
    }
    public bool BeginClose(EventGroupIdentity identity)
    {
        if (identity is null || !entries.TryGetValue(identity, out var entry)) return false;
        entry.Closing = true;
        if (pending.TryGetValue(identity.ScreenId, out var proposal) && proposal.Identity == identity) pending.Remove(identity.ScreenId);
        return true;
    }
    public IReadOnlyList<EventGroupIdentity> ConvergenceCandidates(string screenId)
    {
        var screen = entries.Values.Where(x => x.Identity.ScreenId == screenId).ToArray();
        int needed = Math.Max(0, screen.Length - Limit - screen.Count(x => x.Closing));
        return screen.Where(x => !x.Closing && x.FirstShownOrder is not null && !x.Lifetime.IsProtected(x.Identity.Generation))
            .OrderBy(x => x.FirstShownOrder).Take(needed).Select(x => x.Identity).ToArray();
    }
    public IReadOnlyList<EventGroupIdentity> Expired() => entries.Values.Where(x => !x.Closing && x.Lifetime.IsExpired(x.Identity.Generation))
        .Select(x => x.Identity).ToArray();
    private bool Current(EventGroupProposal? proposal) => proposal is not null &&
        pending.TryGetValue(proposal.Request.ScreenId, out var current) && ReferenceEquals(current, proposal);
    private static bool ValidId(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim();
    public IReadOnlyList<EventGroupObservation> Snapshot() => entries.Values.Select(x => new EventGroupObservation(
        x.Identity, x.Request.Persistent, x.FirstShownOrder, x.Closing, x.Lifetime.IsProtected(x.Identity.Generation),
        x.Lifetime.IsExpired(x.Identity.Generation))).ToArray();
    private sealed class Entry(EventGroupProposal proposal, TimeProvider clock)
    {
        internal readonly EventGroupRequest Request = proposal.Request;
        internal readonly EventGroupIdentity Identity = proposal.Identity;
        internal readonly FlyoutLifetime Lifetime = new(proposal.Identity.Generation, FlyoutLifetimeKind.EventGroup, proposal.Request.Persistent, clock);
        internal long? FirstShownOrder;
        internal bool Closing;
    }
}

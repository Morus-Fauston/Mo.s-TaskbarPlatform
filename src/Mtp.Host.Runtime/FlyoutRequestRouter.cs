using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>An immutable entry produced by the Host declaration validator.</summary>
public sealed record ValidatedFlyoutEntry
{
    internal ValidatedFlyoutEntry(StableIdentity identity, FlyoutKind kind, EventClosePolicy? closePolicy = null,
        HintExpansionTarget? expansion = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Segments.Count != 3 || !Enum.IsDefined(kind) ||
            (kind == FlyoutKind.EventGroup ? closePolicy is null || !Enum.IsDefined(closePolicy.Value) : closePolicy is not null) ||
            (expansion is not null && kind != FlyoutKind.InteractiveHint))
            throw new ArgumentException("Invalid validated flyout entry.");
        Identity = identity;
        Kind = kind;
        ClosePolicy = closePolicy;
        Expansion = expansion;
    }

    public StableIdentity Identity { get; }
    public FlyoutKind Kind { get; }
    public EventClosePolicy? ClosePolicy { get; }
    public HintExpansionTarget? Expansion { get; }
}

public sealed record FlyoutRequestReceipt(string? RequestId, long RequestSequence, ProtocolResult Result);

/// <summary>Admits controlled requests; this phase never creates or claims to display a window.</summary>
public sealed class FlyoutRequestRouter
{
    public const int RequestsPerSecond = 10;
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<string, ApplicationRequests> applications = new(StringComparer.Ordinal);

    public FlyoutRequestRouter(TimeProvider? timeProvider = null) => clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Called by the declaration owner under its own lock after committing a current snapshot.
    /// The caller supplies the authoritative session; queued historical snapshots are not valid inputs.
    /// </summary>
    public ProtocolResult SynchronizeDeclaration(BrokerApplicationSnapshot snapshot, IReadOnlyList<ValidatedFlyoutEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            if (!ValidId(snapshot.ApplicationId) || !ValidId(snapshot.SessionId) || !snapshot.IsConnected || !snapshot.IsInteractive ||
                snapshot.Declaration is null || snapshot.State is null)
                return Reject("DeclarationRequired", "当前完整声明尚未确认");
            if (snapshot.Declaration.Identity.LocalId.Value != snapshot.ApplicationId || entries is null || entries.Count > DeclarationValidator.MaximumNodes)
                return Reject("InvalidDeclaration", "浮窗入口登记归属或预算无效");
            applications.TryGetValue(snapshot.ApplicationId, out var state);
            if (state is null && applications.Count >= ProtocolLimits.MaximumApplications) return Reject("Busy", "应用请求容量已满");
            var next = new Dictionary<StableIdentity, EntryPolicy>();
            foreach (var entry in entries)
            {
                if (entry is null || entry.Identity.Segments[0].Value != snapshot.ApplicationId || next.ContainsKey(entry.Identity))
                    return Reject("InvalidDeclaration", "浮窗入口登记归属无效");
                var enabled = state is null || !state.Entries.TryGetValue(entry.Identity, out var previous) || previous.Enabled;
                next.Add(entry.Identity, new EntryPolicy(entry, enabled));
            }
            if (state is null)
                applications.Add(snapshot.ApplicationId, state = new ApplicationRequests(snapshot.SessionId, clock.GetTimestamp()));
            else if (state.SessionId != snapshot.SessionId)
            {
                state.SessionId = snapshot.SessionId;
                state.HighestSequence = 0;
                state.Last = null;
            }
            state.Entries = next;
            state.Declaration = snapshot.Declaration;
            return ProtocolResult.Success();
        }
    }

    public ProtocolResult Handle(string applicationId, string sessionId, FlyoutRequest? request,
        BrokerApplicationSnapshot? currentSnapshot, IReadOnlyList<ValidatedFlyoutEntry> entries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidId(applicationId) || currentSnapshot is null || currentSnapshot.ApplicationId != applicationId)
                return Reject("UnknownApplication", "应用未登记");
            if (!ValidId(sessionId) || currentSnapshot.SessionId != sessionId)
                return Reject("StaleSession", "请求会话已失效");
            if (!applications.TryGetValue(applicationId, out var state))
            {
                if (applications.Count >= ProtocolLimits.MaximumApplications) return Reject("Busy", "应用请求容量已满");
                applications.Add(applicationId, state = new ApplicationRequests(sessionId, clock.GetTimestamp()));
            }
            else if (state.SessionId != sessionId)
            {
                state.SessionId = sessionId;
                state.HighestSequence = 0;
                state.Last = null;
                state.Declaration = null;
            }

            ProtocolResult Finish(ProtocolResult result)
            {
                state.Last = new FlyoutRequestReceipt(ValidId(request?.RequestId) ? request!.RequestId : null, request?.RequestSequence ?? 0, result);
                return result;
            }

            if (request is null || request.RequestSequence <= 0)
                return Finish(Reject("InvalidRequest", "请求或序号无效"));
            if (request.RequestSequence <= state.HighestSequence)
                return Finish(Reject("DuplicateRequest", "请求序号已处理或已过期"));
            state.HighestSequence = request.RequestSequence;

            var now = clock.GetTimestamp();
            if (clock.GetElapsedTime(state.WindowStarted, now) >= TimeSpan.FromSeconds(1))
            {
                state.WindowStarted = now;
                state.WindowRequests = 0;
            }
            if (state.WindowRequests >= RequestsPerSecond) return Finish(Reject("RateLimited", "浮窗请求过于频繁"));
            state.WindowRequests++;
            if (!ValidId(request.RequestId) || !ValidId(request.FeatureGroupId) || !ValidId(request.EntryId) ||
                !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Screen) || !Enum.IsDefined(request.Position))
                return Finish(Reject("InvalidRequest", "请求字段或偏好无效"));
            if (!currentSnapshot.IsConnected) return Finish(Reject("Disconnected", "应用连接已中断"));
            if (!currentSnapshot.IsInteractive || currentSnapshot.Declaration is null || currentSnapshot.State is null)
                return Finish(Reject("DeclarationRequired", "当前完整声明尚未确认"));
            if (request.StateRevision != currentSnapshot.State.Revision)
                return Finish(Reject("StaleState", "请求未引用当前有效状态"));

            if (!ReferenceEquals(state.Declaration, currentSnapshot.Declaration))
            {
                var synchronized = SynchronizeDeclaration(currentSnapshot, entries);
                if (!synchronized.Accepted) return Finish(synchronized);
            }

            var identity = new StableIdentity(new StableId(applicationId)).CreateChild(new StableId(request.FeatureGroupId)).CreateChild(new StableId(request.EntryId));
            if (!state.Entries.TryGetValue(identity, out var policy)) return Finish(Reject("UnknownEntry", "浮窗入口或事件通道未声明"));
            if (policy.Entry.Kind != request.Kind) return Finish(Reject("KindMismatch", "浮窗请求类型与声明不一致"));
            if (!policy.Enabled) return Finish(Reject("EntryDisabled", "入口显示已由Host关闭"));
            return Finish(new ProtocolResult(true, "Received", "请求已接收，尚未创建窗口"));
        }
    }

    public ProtocolResult SetEntryEnabled(ValidatedFlyoutEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (gate)
        {
            var application = entry.Identity.Segments[0].Value;
            if (!applications.TryGetValue(application, out var state) || !state.Entries.TryGetValue(entry.Identity, out var policy))
                return Reject("UnknownEntry", "入口尚未登记");
            state.Entries[entry.Identity] = policy with { Enabled = enabled };
            return ProtocolResult.Success();
        }
    }

    public FlyoutRequestReceipt? GetLastResult(string applicationId)
    {
        lock (gate) return applications.GetValueOrDefault(applicationId)?.Last;
    }

    public bool IsEntryEnabled(string applicationId, string sessionId, string groupId, string entryId)
    {
        lock (gate)
        {
            if (!applications.TryGetValue(applicationId, out var state) || state.SessionId != sessionId) return false;
            return state.Entries.Any(pair => pair.Key.Segments[1].Value == groupId && pair.Key.Segments[2].Value == entryId && pair.Value.Enabled);
        }
    }

    /// <summary>A late UI result cannot overwrite a newer request or a replacement session.</summary>
    public bool RecordPresentationResult(string applicationId, string sessionId, FlyoutRequest request, ProtocolResult result, string? expectedCode = null)
    {
        lock (gate)
        {
            if (!applications.TryGetValue(applicationId, out var state) || state.SessionId != sessionId ||
                state.Last is not { } last || last.RequestId != request.RequestId || last.RequestSequence != request.RequestSequence ||
                (expectedCode is not null && last.Result.Code != expectedCode))
                return false;
            state.Last = last with { Result = result };
            return true;
        }
    }

    private static ProtocolResult Reject(string code, string message) => ProtocolResult.Reject(code, message);
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= DeclarationValidator.MaximumIdLength && value == value.Trim();
    private sealed record EntryPolicy(ValidatedFlyoutEntry Entry, bool Enabled);
    private sealed class ApplicationRequests(string sessionId, long windowStarted)
    {
        public string SessionId = sessionId;
        public long HighestSequence;
        public long WindowStarted = windowStarted;
        public int WindowRequests;
        public ValidatedApplicationDeclaration? Declaration;
        public Dictionary<StableIdentity, EntryPolicy> Entries = new();
        public FlyoutRequestReceipt? Last;
    }
}

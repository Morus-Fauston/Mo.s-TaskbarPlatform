using Mtp.Contracts;

namespace Mtp.Host;

public sealed class ActionReservation
{
    internal ActionReservation(ProtocolResult result, ActionInvocation? invocation, Task<ProtocolResult> completion)
    { Result = result; Invocation = invocation; Completion = completion; }
    public bool Accepted => Result.Accepted;
    public ProtocolResult Result { get; }
    public ActionInvocation? Invocation { get; }
    public Task<ProtocolResult> Completion { get; }
}

public sealed record PendingActionSnapshot(string RequestId, string SessionId, ActionSlotReference Slot,
    ActionParameter? Preview, bool IsBusy);
public sealed record ActionErrorHint(string ApplicationId, string SessionId, string RequestId,
    ActionSlotReference Slot, ProtocolResult Result);

/// <summary>Bounded action reservations; confirmed state remains owned by the supplied atomic state commit boundary.</summary>
public sealed class ActionRequestTracker
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private bool stopped;

    public ActionRequestTracker(TimeProvider? timeProvider = null) => clock = timeProvider ?? TimeProvider.System;
    public int OutstandingCount { get { lock (gate) return sessions.Values.Sum(session => session.Pending.Count); } }
    public int BusyCount { get { lock (gate) return sessions.Values.Sum(session => session.Pending.Values.Count(item => item.Busy)); } }

    public ActionReservation Begin(BrokerApplicationSnapshot current, ActionSlotReference slot, ActionParameter parameter, ActionParameter? preview = null)
    {
        lock (gate)
        {
            if (stopped || current is null || slot is null || parameter is null || !current.IsInteractive ||
                current.ApplicationId != slot.ApplicationId || current.Declaration is null ||
                !current.Declaration.ActionSlots.Any(known => known.Reference == slot))
                return RejectReservation("ActionNotAvailable", "当前会话没有可用动作槽位");
            var expectedKind = current.Declaration.ActionSlots.Single(known => known.Reference == slot).ParameterKind;
            if (!ValidParameter(parameter, expectedKind) || (preview is not null && !ValidParameter(preview, expectedKind)))
                return RejectReservation("ActionNotAvailable", "动作参数与当前槽位不匹配");
            if (!sessions.TryGetValue(current.ApplicationId, out var session) || session.Id != current.SessionId)
            {
                if (session is not null) EndSession(current.ApplicationId, session.Id);
                if (sessions.Count >= ProtocolLimits.MaximumApplications)
                    return RejectReservation("Busy", "动作应用数达到预算");
                session = new(current.SessionId);
                sessions[current.ApplicationId] = session;
            }
            if (session.Pending.Count >= ActionLimits.MaximumOutstandingPerApplication ||
                sessions.Values.Sum(value => value.Pending.Count) >= ProtocolLimits.MaximumPendingRequests || session.Sequence == long.MaxValue)
                return RejectReservation("Busy", "未终结动作达到预算");
            var invocation = new ActionInvocation(Guid.NewGuid().ToString("N"), ++session.Sequence, slot, parameter);
            var pending = new Pending(invocation, preview, clock.GetTimestamp());
            session.Pending.Add(invocation.RequestId, pending);
            return new(ProtocolResult.Success(), invocation, pending.Completion.Task);
        }
    }

    public ProtocolResult Receive(string applicationId, string sessionId, ActionCompletion completion,
        BrokerApplicationSnapshot? current, Func<ApplicationState, ProtocolResult> validateAndCommitState)
    {
        lock (gate)
        {
            if (stopped || current is null || current.ApplicationId != applicationId || current.SessionId != sessionId || !current.IsConnected)
                return ProtocolResult.Reject("ActionNotAvailable", "动作结果不属于当前有效会话");
            if (completion is null || !ValidId(completion.RequestId) || completion.Sequence <= 0 || !ValidResult(completion.Result))
                return ProtocolResult.Reject("InvalidActionCompletion", "动作结果字段无效或超出预算");
            if (!sessions.TryGetValue(applicationId, out var session) || session.Id != sessionId ||
                !session.Pending.TryGetValue(completion.RequestId, out var pending) || pending.Invocation.Sequence != completion.Sequence)
                return ProtocolResult.Reject("ActionNotAvailable", "动作请求不属于当前会话");
            Tick();
            var result = completion.Result;
            if (result.Accepted && completion.State is not null)
            {
                ProtocolResult committed;
                try { committed = validateAndCommitState(completion.State); }
                catch (Exception) { committed = ProtocolResult.Reject("ActionStateRejected", "动作确认状态无法接收"); }
                if (!ValidResult(committed, applicationId)) committed = ProtocolResult.Reject("ActionStateRejected", "动作确认状态结果无效");
                if (!committed.Accepted || committed.ActivityRejections is { Count: > 0 })
                    result = committed.ActivityRejections is null ? committed : committed with
                    { ActivityRejections = Array.AsReadOnly(committed.ActivityRejections.ToArray()) };
            }
            if ((!result.Accepted || result.ActivityRejections is { Count: > 0 }) && pending.Busy)
                session.ErrorHint = new(applicationId, sessionId, completion.RequestId, pending.Invocation.Slot, result);
            pending.Busy = false;
            pending.Completion.TrySetResult(result);
            session.Pending.Remove(completion.RequestId);
            return result;
        }
    }

    public bool Cancel(string applicationId, string sessionId, string requestId)
    {
        lock (gate)
        {
            if (!TryPending(applicationId, sessionId, requestId, out _, out var pending)) return false;
            pending.Busy = false;
            pending.Completion.TrySetResult(ProtocolResult.Reject("ActionCancelled", "动作等待已取消"));
            return true;
        }
    }

    public void FailDispatch(string applicationId, string sessionId, string requestId, ProtocolResult reason)
    {
        lock (gate)
        {
            if (!TryPending(applicationId, sessionId, requestId, out var session, out var pending)) return;
            if (!ValidResult(reason) || reason.Accepted) reason = ProtocolResult.Reject("ActionDispatchFailed", "动作无法发送");
            if (pending.Busy) session.ErrorHint = new(applicationId, sessionId, requestId, pending.Invocation.Slot, reason);
            pending.Busy = false;
            pending.Completion.TrySetResult(reason);
            session.Pending.Remove(requestId);
        }
    }

    public void Tick()
    {
        lock (gate)
        {
            foreach (var pair in sessions)
                foreach (var pending in pair.Value.Pending.Values)
                    if (pending.Busy && clock.GetElapsedTime(pending.Started, clock.GetTimestamp()) >= TimeSpan.FromSeconds(ActionLimits.WaitTimeoutSeconds))
                    {
                        var error = ProtocolResult.Reject("ActionTimedOut", "动作等待超时，保留最后确认值");
                        pending.Busy = false;
                        pending.Completion.TrySetResult(error);
                        pair.Value.ErrorHint = new(pair.Key, pair.Value.Id, pending.Invocation.RequestId, pending.Invocation.Slot, error);
                    }
        }
    }

    public void EndSession(string applicationId, string sessionId, ProtocolResult? reason = null)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(applicationId, out var session) || session.Id != sessionId) return;
            if (reason is not null && (!ValidResult(reason) || reason.Accepted)) reason = null;
            foreach (var pending in session.Pending.Values)
                pending.Completion.TrySetResult(reason ?? ProtocolResult.Reject("ActionCancelled", "动作会话已结束"));
            sessions.Remove(applicationId);
        }
    }

    public void Shutdown()
    {
        lock (gate)
        {
            stopped = true;
            foreach (var pair in sessions.ToArray()) EndSession(pair.Key, pair.Value.Id);
        }
    }

    public IReadOnlyList<PendingActionSnapshot> GetPending(string applicationId)
    {
        lock (gate) return Array.AsReadOnly(sessions.TryGetValue(applicationId, out var session)
            ? session.Pending.Values.Select(pending => new PendingActionSnapshot(pending.Invocation.RequestId,
                session.Id, pending.Invocation.Slot, pending.Preview, pending.Busy)).ToArray() : []);
    }

    public ActionErrorHint? GetLastErrorHint(string applicationId)
    {
        lock (gate) return sessions.GetValueOrDefault(applicationId)?.ErrorHint;
    }

    private bool TryPending(string app, string sessionId, string requestId, out Session session, out Pending pending)
    {
        pending = null!;
        return sessions.TryGetValue(app, out session!) && session.Id == sessionId && session.Pending.TryGetValue(requestId, out pending!);
    }

    private static ActionReservation RejectReservation(string code, string message)
    {
        var result = ProtocolResult.Reject(code, message);
        return new(result, null, Task.FromResult(result));
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim();
    private static bool ValidResult(ProtocolResult? result, string? admissionApplicationId = null) => result is not null && ValidId(result.Code) &&
        result.Message is not null && result.Message.Length <= ProtocolLimits.MaximumTextLength &&
        (result.Path is null || result.Path.Length <= ProtocolLimits.MaximumTextLength) &&
        (result.ActivityRejections is null || (admissionApplicationId is not null && result.Accepted &&
            result.Code == "AcceptedWithActivityRejections" && result.ActivityRejections.Count is > 0 and <= DynamicContentLimits.MaximumActivitiesPerApplication &&
            result.ActivityRejections.All(value => value is not null && value.ApplicationId == admissionApplicationId &&
                ValidId(value.FeatureGroupId) && ValidId(value.ComponentId) && ValidId(value.ActivityId) && value.Code == "DisplayNotAllowed") &&
            result.ActivityRejections.Select(value => (value.FeatureGroupId, value.ComponentId, value.ActivityId)).Distinct().Count() == result.ActivityRejections.Count));

    private static bool ValidParameter(ActionParameter parameter, ActionParameterKind expected) => parameter.Kind == expected && expected switch
    {
        ActionParameterKind.None => parameter.Boolean is null && parameter.Number is null && parameter.Text is null,
        ActionParameterKind.Boolean => parameter.Boolean is not null && parameter.Number is null && parameter.Text is null,
        ActionParameterKind.Number => parameter.Boolean is null && parameter.Text is null && parameter.Number is { } number && double.IsFinite(number),
        ActionParameterKind.Text => parameter.Boolean is null && parameter.Number is null && parameter.Text is { Length: <= ProtocolLimits.MaximumTextLength },
        _ => false
    };

    private sealed class Session(string id)
    {
        public string Id { get; } = id;
        public long Sequence;
        public Dictionary<string, Pending> Pending { get; } = new(StringComparer.Ordinal);
        public ActionErrorHint? ErrorHint;
    }

    private sealed class Pending(ActionInvocation invocation, ActionParameter? preview, long started)
    {
        public ActionInvocation Invocation { get; } = invocation;
        public ActionParameter? Preview { get; } = preview;
        public long Started { get; } = started;
        public bool Busy = true;
        public TaskCompletionSource<ProtocolResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

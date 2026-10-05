using Mtp.Contracts;

namespace Mtp.Transport;

public enum HeartbeatFailureReason { ProcessExited, PipeDisconnected, HeartbeatTimedOut }
public sealed record HeartbeatFault(string ApplicationId, string SessionId, HeartbeatFailureReason Reason);
public sealed record HeartbeatSessionSnapshot(string ApplicationId, string SessionId, long LastSequence,
    DateTimeOffset? LastSentAt, HeartbeatFailureReason? Failure);

/// <summary>Tracks only session liveness. It never owns timers or terminates or restarts processes.</summary>
public sealed class HeartbeatMonitor
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DetectionInterval = TimeSpan.FromSeconds(1);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private bool stopped;

    public HeartbeatMonitor(TimeProvider? timeProvider = null) => clock = timeProvider ?? TimeProvider.System;

    public ProtocolResult StartSession(string applicationId, string sessionId)
    {
        lock (gate)
        {
            if (stopped || !ValidId(applicationId) || !ValidId(sessionId)) return ProtocolResult.Reject("InvalidHeartbeatSession", "心跳会话不可用");
            if (sessions.TryGetValue(applicationId, out var current) && current.Snapshot.SessionId == sessionId)
                return ProtocolResult.Reject("DuplicateSession", "心跳会话已经登记");
            if (current is null && sessions.Count >= ProtocolLimits.MaximumApplications)
                return ProtocolResult.Reject("Busy", "心跳应用数量超限");
            sessions[applicationId] = new(new(applicationId, sessionId, 0, null, null), clock.GetTimestamp());
            return ProtocolResult.Success();
        }
    }

    public ProtocolResult Receive(string applicationId, string sessionId, long sequence, DateTimeOffset sentAt)
    {
        lock (gate)
        {
            if (!TrySession(applicationId, sessionId, out var current)) return ProtocolResult.Reject("StaleSession", "心跳不属于当前会话");
            if (sequence <= 0 || sentAt == default) return ProtocolResult.Reject("InvalidHeartbeat", "心跳字段无效");
            if (current.Snapshot.Failure is not null) return ProtocolResult.Reject("HeartbeatUnavailable", "失效会话必须重新握手");
            if (sequence <= current.Snapshot.LastSequence) return ProtocolResult.Reject("StaleHeartbeat", "心跳序号没有递增");
            current.Snapshot = current.Snapshot with { LastSequence = sequence, LastSentAt = sentAt };
            current.LastReceived = clock.GetTimestamp();
            return ProtocolResult.Success();
        }
    }

    public IReadOnlyList<HeartbeatFault> Tick()
    {
        lock (gate)
        {
            if (stopped) return Array.Empty<HeartbeatFault>();
            var now = clock.GetTimestamp();
            var faults = new List<HeartbeatFault>();
            foreach (var current in sessions.Values)
            {
                if (current.Snapshot.Failure is not null || clock.GetElapsedTime(current.LastReceived, now) < Timeout) continue;
                current.Snapshot = current.Snapshot with { Failure = HeartbeatFailureReason.HeartbeatTimedOut };
                faults.Add(new(current.Snapshot.ApplicationId, current.Snapshot.SessionId, HeartbeatFailureReason.HeartbeatTimedOut));
            }
            return faults.AsReadOnly();
        }
    }

    public HeartbeatFault? ReportFault(string applicationId, string sessionId, HeartbeatFailureReason reason)
    {
        lock (gate)
        {
            if (!Enum.IsDefined(reason) || !TrySession(applicationId, sessionId, out var current) || current.Snapshot.Failure is not null) return null;
            current.Snapshot = current.Snapshot with { Failure = reason };
            return new(applicationId, sessionId, reason);
        }
    }

    public void EndSession(string applicationId, string sessionId)
    {
        lock (gate) if (TrySession(applicationId, sessionId, out _)) sessions.Remove(applicationId);
    }

    public void Shutdown()
    {
        lock (gate) { stopped = true; sessions.Clear(); }
    }

    public HeartbeatSessionSnapshot? GetSnapshot(string applicationId)
    {
        lock (gate) return ValidId(applicationId) ? sessions.GetValueOrDefault(applicationId)?.Snapshot : null;
    }

    public IReadOnlyList<HeartbeatSessionSnapshot> Snapshots
    {
        get { lock (gate) return Array.AsReadOnly(sessions.Values.Select(value => value.Snapshot).ToArray()); }
    }

    private bool TrySession(string applicationId, string sessionId, out Session current)
    {
        current = null!;
        return !stopped && ValidId(applicationId) && ValidId(sessionId) && sessions.TryGetValue(applicationId, out current!) && current.Snapshot.SessionId == sessionId;
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim();

    private sealed class Session(HeartbeatSessionSnapshot snapshot, long lastReceived)
    {
        public HeartbeatSessionSnapshot Snapshot = snapshot;
        public long LastReceived = lastReceived;
    }
}

using Mtp.Contracts;

namespace Mtp.Host;

public sealed record QueuedHostFlyout(ProtocolMessage Message, bool Expired);

/// <summary>One Host-owned bounded handoff to its UI thread; no work runs on the control reader.</summary>
public sealed class HostFlyoutRequestQueue(TimeProvider? timeProvider = null)
{
    public const int Capacity = 64;
    public const int BatchSize = 4;
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Queue<(ProtocolMessage Message, long Created)> pending = new();
    private bool closed;
    public int Count { get { lock (gate) return pending.Count; } }

    // The caller must run the authoritative declaration/request admission before this handoff.
    public ProtocolResult Enqueue(ProtocolMessage message)
    {
        lock (gate)
        {
            if (closed) return ProtocolResult.Reject("HostClosed", "Host正在关闭");
            if (message?.Kind != MessageKind.FlyoutRequest || message.Flyout is null)
                return ProtocolResult.Reject("InvalidRequest", "浮窗消息缺失");
            if (pending.Count >= Capacity) return ProtocolResult.Reject("Busy", "浮窗显示队列已满");
            pending.Enqueue((message, clock.GetTimestamp()));
            return new(true, "Queued", "请求已进入Host显示队列，尚未创建窗口");
        }
    }

    public IReadOnlyList<QueuedHostFlyout> Drain()
    {
        lock (gate)
        {
            var batch = new List<QueuedHostFlyout>(BatchSize);
            while (batch.Count < BatchSize && pending.TryDequeue(out var next))
                batch.Add(new(next.Message, clock.GetElapsedTime(next.Created) >= TimeSpan.FromSeconds(5)));
            return batch.AsReadOnly();
        }
    }

    public void Close() { lock (gate) { closed = true; pending.Clear(); } }
}

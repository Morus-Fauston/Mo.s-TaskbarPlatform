using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class HostFlyoutRequestQueueTests
{
    private static ProtocolMessage Message(int sequence = 1) => new()
    {
        Kind = MessageKind.FlyoutRequest, ApplicationId = "app", SessionId = "session",
        RequestId = "request-" + sequence,
        Flyout = new("request-" + sequence, sequence, "main", "details", FlyoutKind.TaskbarGroup, 1)
    };

    [Fact]
    public void Capacity_is_bounded_and_drain_preserves_identity_and_order()
    {
        var queue = new HostFlyoutRequestQueue();
        for (int i = 1; i <= 64; i++) Assert.Equal("Queued", queue.Enqueue(Message(i)).Code);
        Assert.Equal("Busy", queue.Enqueue(Message(65)).Code);
        var batch = queue.Drain();
        Assert.Equal(4, batch.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, batch.Select(x => x.Message.Flyout!.RequestSequence));
        Assert.All(batch, x => Assert.Equal("session", x.Message.SessionId));
        Assert.Equal(60, queue.Count);
    }

    [Fact]
    public void Captured_trigger_point_remains_with_its_request_until_ui_consumption()
    {
        var queue = new HostFlyoutRequestQueue();
        queue.Enqueue(Message(1), new(-1920, 500));
        queue.Enqueue(Message(2), new(900, 100));
        var batch = queue.Drain();
        Assert.Equal(new HostFlyoutTrigger(-1920, 500), batch[0].Trigger);
        Assert.Equal(new HostFlyoutTrigger(900, 100), batch[1].Trigger);
    }

    [Fact]
    public void Shutdown_discards_pending_and_rejects_late_callbacks()
    {
        var queue = new HostFlyoutRequestQueue();
        Assert.True(queue.Enqueue(Message()).Accepted);
        queue.Close(); queue.Close();
        Assert.Empty(queue.Drain());
        Assert.Equal(0, queue.Count);
        Assert.Equal("HostClosed", queue.Enqueue(Message(2)).Code);
    }

    [Fact]
    public void Expiry_uses_monotonic_clock_and_does_not_claim_display()
    {
        var clock = new Clock();
        var queue = new HostFlyoutRequestQueue(clock);
        Assert.Equal("Queued", queue.Enqueue(Message()).Code);
        clock.Now = TimeSpan.FromSeconds(5).Ticks;
        Assert.True(Assert.Single(queue.Drain()).Expired);
    }

    [Fact]
    public void Missing_or_non_flyout_payload_is_rejected_without_consuming_capacity()
    {
        var queue = new HostFlyoutRequestQueue();
        Assert.False(queue.Enqueue(Message() with { Flyout = null }).Accepted);
        Assert.False(queue.Enqueue(Message() with { Kind = MessageKind.State }).Accepted);
        Assert.Equal(0, queue.Count);
    }

    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
    }
}

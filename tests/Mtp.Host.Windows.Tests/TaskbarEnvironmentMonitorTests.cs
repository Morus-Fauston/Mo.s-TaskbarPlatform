using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class TaskbarEnvironmentMonitorTests
{
    [Fact]
    public void BurstsCoalesceAndQueuedWorkReadsCurrentStateThenStopsAfterShutdown()
    {
        var queue = new Queue<Action>();
        var state = 1;
        var observed = 0;
        using var monitor = new TaskbarEnvironmentMonitor(action => { queue.Enqueue(action); return true; }, () => observed = state, () => 0);
        monitor.RequestRefresh();
        monitor.RequestRefresh();
        Assert.Single(queue);
        state = 2;
        queue.Dequeue()();
        Assert.Equal(2, observed);
        monitor.RequestRefresh();
        Assert.True(monitor.TryStop());
        state = 3;
        queue.Dequeue()();
        monitor.RequestRefresh();
        Assert.Empty(queue);
        Assert.Equal(2, observed);
    }

    [Fact]
    public void RejectedQueueCanBeRetriedByPolling()
    {
        var accept = false;
        Action? pending = null;
        var count = 0;
        using var monitor = new TaskbarEnvironmentMonitor(action => { if (accept) pending = action; return accept; }, () => count++, () => 0);
        monitor.RequestRefresh();
        accept = true;
        monitor.RequestRefresh();
        Assert.NotNull(pending);
        pending();
        Assert.Equal(1, count);
    }
}

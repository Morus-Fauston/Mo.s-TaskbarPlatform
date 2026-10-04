using Mtp.Host;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Mtp.Platform.Core.Tests;

public sealed class TaskbarEnvironmentMonitorTests
{
    [Fact]
    public void ForegroundLocationChangesDoNotRefreshTaskbarOrConsole()
    {
        var foreground = GetForegroundWindow();
        Assert.NotEqual(0, foreground);
        var queue = new Queue<Action>();
        var refreshes = 0;
        using var monitor = new TaskbarEnvironmentMonitor(action => { queue.Enqueue(action); return true; }, () => refreshes++, () => 0);
        var onEvent = typeof(TaskbarEnvironmentMonitor).GetMethod("OnEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (var i = 0; i < 1000; i++)
        {
            onEvent.Invoke(monitor, [nint.Zero, 0x800Bu, foreground, 0, 0, 0u, 0u]);
            while (queue.TryDequeue(out var action)) action();
        }
        Assert.Equal(0, refreshes);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(0x8002u)]
    [InlineData(0x8003u)]
    [InlineData(0x800Bu)]
    public void TaskbarEventsStillRefresh(uint kind)
    {
        var queue = new Queue<Action>();
        var refreshes = 0;
        nint taskbar = 123;
        using var monitor = new TaskbarEnvironmentMonitor(action => { queue.Enqueue(action); return true; }, () => refreshes++, () => taskbar);
        var onEvent = typeof(TaskbarEnvironmentMonitor).GetMethod("OnEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
        onEvent.Invoke(monitor, [nint.Zero, kind, taskbar, 0, 0, 0u, 0u]);
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(1, refreshes);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

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

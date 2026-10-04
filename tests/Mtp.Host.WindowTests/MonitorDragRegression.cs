using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Mtp.Host.WindowTests;

internal static class MonitorDragRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) throw new InvalidOperationException("Foreground window missing for event regression.");
        var refreshes = 0;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        using var monitor = new TaskbarEnvironmentMonitor(action => dispatcher.TryEnqueue(() => action()), () => refreshes++, () => 0);
        if (monitor.Error is not null) throw new InvalidOperationException(monitor.Error.Message);
        for (var i = 0; i < 60; i++)
        {
            NotifyWinEvent(0x800B, foreground, 0, 0);
            await Task.Delay(1);
        }
        await Task.Delay(100);
        if (refreshes != 0) throw new InvalidOperationException($"Foreground drag events refreshed the console {refreshes} times.");
        log("PASS: 60 native foreground location events do not refresh taskbar/console; physical drag smoothness remains pending human confirmation.");
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void NotifyWinEvent(uint kind, nint window, int objectId, int childId);
}

using System.Runtime.InteropServices;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>Out-of-context notifications request fresh UI-thread reads; queued work never carries window snapshots.</summary>
internal sealed class TaskbarEnvironmentMonitor : IDisposable
{
    private readonly Func<Action, bool> enqueue;
    private readonly Action refresh;
    private readonly Func<nint> taskbar;
    private readonly Native.WinEventCallback callback;
    private readonly List<nint> hooks = new();
    private bool pending;
    private bool stopped;

    public TaskbarEnvironmentMonitor(Func<Action, bool> enqueue, Action refresh, Func<nint> taskbar)
    {
        this.enqueue = enqueue;
        this.refresh = refresh;
        this.taskbar = taskbar;
        callback = OnEvent;
        foreach (var (first, last) in new (uint, uint)[] { (3, 3), (0x8002, 0x8003), (0x800B, 0x800B) })
        {
            var hook = Native.SetWinEventHook(first, last, 0, callback, 0, 0, 0);
            if (hook != 0) hooks.Add(hook);
            else Error = new("dock_event_monitor_failed", "部分窗口事件订阅失败，使用定时环境检查。");
        }
    }

    public StructuredError? Error { get; private set; }

    public void RequestRefresh()
    {
        if (stopped || pending) return;
        pending = true;
        if (!enqueue(() =>
        {
            pending = false;
            if (!stopped) refresh();
        })) pending = false;
    }

    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint threadId, uint time)
    {
        if (stopped) return;
        try
        {
            if (kind == 3 || (objectId == 0 && childId == 0 && window != 0 &&
                (window == taskbar() || window == Win32TaskbarVisibility.ForegroundWindow)))
                RequestRefresh();
        }
        catch (Exception exception)
        {
            Error = new("dock_event_monitor_failed", "窗口事件处理失败，使用定时环境检查。", exception.GetType().Name);
        }
    }

    public bool TryStop()
    {
        stopped = true;
        for (var i = hooks.Count - 1; i >= 0; i--)
        {
            if (Native.UnhookWinEvent(hooks[i])) hooks.RemoveAt(i);
        }
        if (hooks.Count == 0) return true;
        Error = new("dock_event_cleanup_failed", "窗口事件尚未确认解除，请重试关闭 Host。");
        return false;
    }

    public void Dispose()
    {
        if (!TryStop()) throw new InvalidOperationException(Error!.Message);
        GC.KeepAlive(callback);
    }

    private static class Native
    {
        internal delegate void WinEventCallback(nint hook, uint kind, nint window, int objectId, int childId, uint threadId, uint time);
        [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    }
}

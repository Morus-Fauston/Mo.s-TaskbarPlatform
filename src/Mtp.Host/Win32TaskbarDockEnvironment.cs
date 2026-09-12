using System.Diagnostics;
using System.Runtime.InteropServices;
using Mtp.Platform.Core;

namespace Mtp.Host;

internal sealed record TaskbarDockDisplay(string Id, bool IsPrimary, PixelRect Bounds, PixelRect WorkArea, uint Dpi);

/// <summary>Read-only Explorer geometry. It never reparents a window or inspects unrelated tools.</summary>
internal sealed class Win32TaskbarDockEnvironment(TimeProvider? clock = null) : ITaskbarDockEnvironment
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    public bool SimulateUnavailable { get; set; }
    internal nint ObservedTaskbar { get; private set; }

    public IReadOnlyList<TaskbarDockDisplay> GetDisplays()
    {
        var displays = new List<TaskbarDockDisplay>();
        Native.MonitorCallback callback = (nint monitor, nint dc, ref Native.Rect rect, nint data) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = string.Empty };
            if (Native.GetMonitorInfoW(monitor, ref info))
            {
                var dpiResult = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _);
                displays.Add(new(info.Device, (info.Flags & 1) != 0, info.Bounds.ToPixelRect(), info.Work.ToPixelRect(), dpiResult >= 0 ? dpi : 96));
            }
            return true;
        };
        if (!Native.EnumDisplayMonitors(0, 0, callback, 0))
            throw new InvalidOperationException("无法枚举显示器。");
        return displays.AsReadOnly();
    }

    public CoreResult<TaskbarDockEnvironmentSnapshot> Capture(string? displayId)
    {
        var started = clock.GetTimestamp();
        try
        {
            var displays = GetDisplays();
            var requested = displays.FirstOrDefault(display => display.Id == displayId);
            var display = requested ?? displays.FirstOrDefault(item => item.IsPrimary);
            if (display is null) return Failure("dock_display_unavailable", "当前没有可用显示器。");
            StructuredError? error = displayId is not null && requested is null
                ? new("dock_display_missing", "目标显示器暂时不可用，使用主屏幕独立贴靠，已保留原屏幕偏好。") : null;
            TaskbarDockGeometry? geometry = null;
            string? identity = null;
            var taskbar = FindTaskbar(display);
            ObservedTaskbar = taskbar;
            var visibility = TaskbarVisibility.Allowed;
            if (error is null && !SimulateUnavailable && taskbar != 0 && Native.GetWindowRect(taskbar, out var barRect))
            {
                var tray = Native.FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
                PixelRect? anchor = tray != 0 && Native.IsWindowVisible(tray) && Native.GetWindowRect(tray, out var trayRect)
                    ? trayRect.ToPixelRect() : null;
                Native.GetWindowThreadProcessId(taskbar, out var pid);
                using var explorer = Process.GetProcessById((int)pid);
                identity = $"{taskbar}:{pid}:{explorer.StartTime.ToUniversalTime().Ticks}";
                geometry = new(display.Id, display.Bounds, barRect.ToPixelRect(), anchor, Native.GetDpiForWindow(taskbar));
                // The taskbar can start retracting between the visibility read and the anchor read.
            }
            else if (SimulateUnavailable || taskbar == 0)
                error ??= new("dock_taskbar_unavailable", "目标屏幕的任务栏锚点不可用。");
            // Discard stale multi-read snapshots, including shell auto-hide queries.
            if (clock.GetElapsedTime(started) > TimeSpan.FromMilliseconds(250))
            {
                geometry = null;
                identity = null;
                visibility = TaskbarVisibility.Allowed;
                error = new("dock_probe_timeout", "任务栏探测超过时间预算，已舍弃本次锚点。");
            }
            return CoreResult<TaskbarDockEnvironmentSnapshot>.Success(new(
                display.Id, display.Bounds, display.WorkArea, display.Dpi, identity, geometry, error, visibility));
        }
        catch (Exception exception)
        {
            // Explorer can disappear between any two reads. Keep the monitor fallback usable.
            try
            {
                var display = GetDisplays().FirstOrDefault(item => item.Id == displayId) ?? GetDisplays().FirstOrDefault(item => item.IsPrimary);
                if (display is not null)
                    return CoreResult<TaskbarDockEnvironmentSnapshot>.Success(new(display.Id, display.Bounds, display.WorkArea, display.Dpi,
                        null, null, new("dock_probe_failed", "任务栏环境读取失败，已保留原设置。", exception.GetType().Name), TaskbarVisibility.Unknown));
            }
            catch (Exception) { }
            return Failure("dock_display_unavailable", "无法读取当前屏幕工作区。");
        }
    }

    private static nint FindTaskbar(TaskbarDockDisplay display)
    {
        // These are experimental Explorer class names, not a public Windows extension contract.
        foreach (var name in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            nint previous = 0;
            for (var count = 0; count < 32; count++)
            {
                var candidate = Native.FindWindowExW(0, previous, name, null);
                if (candidate == 0) break;
                var monitor = Native.MonitorFromWindow(candidate, 0);
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = string.Empty };
                if (monitor != 0 && Native.GetMonitorInfoW(monitor, ref info) && info.Device == display.Id) return candidate;
                previous = candidate;
            }
        }
        return 0;
    }

    private static CoreResult<TaskbarDockEnvironmentSnapshot> Failure(string code, string message) =>
        CoreResult<TaskbarDockEnvironmentSnapshot>.Failure(new(code, message));

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public readonly PixelRect ToPixelRect() => new(Left, Top, Right - Left, Bottom - Top);
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MonitorInfo
        {
            public int Size;
            public Rect Bounds, Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        internal delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rect, nint data);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowExW(nint parent, nint after, string className, string? title);
        [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
        [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, uint type, out uint x, out uint y);
    }
}

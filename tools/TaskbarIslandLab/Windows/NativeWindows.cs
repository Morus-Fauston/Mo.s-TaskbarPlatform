using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TaskbarIslandLab.Logic;

namespace TaskbarIslandLab.Windows;

// All native window discovery/identity and ownership stays in the experimental adapter.
internal static class NativeWindows
{
    internal const uint Child = 0x40000000, Popup = 0x80000000, ClipChildren = 0x02000000, ClipSiblings = 0x04000000;
    private const string ClassName = "MTP.TaskbarIslandLab.Owned";
    private static readonly WindowProc Procedure = Dispatch;
    private static readonly Dictionary<nint, Action<uint, nuint>> Callbacks = [];
    private static readonly HashSet<nint> Owned = [];
    private static bool registered;

    internal static nint Create(int x, int y, int width, int height, Action<uint, nuint>? callback = null)
    {
        if (!registered)
        {
            var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Procedure, Instance = GetModuleHandleW(null), Name = ClassName, Cursor = LoadCursorW(0, 32512) };
            if (RegisterClassExW(ref wc) == 0) throw Error("RegisterClassEx");
            registered = true;
        }
        var hwnd = CreateWindowExW(0x08200080, ClassName, "MTP Island Lab", Popup | ClipChildren | ClipSiblings,
            x, y, width, height, 0, 0, GetModuleHandleW(null), 0);
        if (hwnd == 0) throw Error("CreateWindowEx");
        Owned.Add(hwnd);
        if (callback != null) Callbacks.Add(hwnd, callback);
        return hwnd;
    }

    private static nint Dispatch(nint hwnd, uint message, nuint wparam, nint lparam)
    {
        // Never let a managed exception unwind through a native window procedure.
        if (Callbacks.TryGetValue(hwnd, out var callback)) callback(message, wparam);
        if (message == 0x82) { Callbacks.Remove(hwnd); Owned.Remove(hwnd); }
        if (message == 0x21) return 3; // MA_NOACTIVATE: deliver the click without foreground activation.
        if (message == 0x14) return 1; // No GDI erase/bitmap display path.
        if (message == 0x10) return 0; // Owner handles close through its dispatcher.
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }

    internal static void Destroy(nint hwnd)
    {
        // WM_NCDESTROY removes ownership even when Windows later reuses the numeric HWND.
        if (hwnd == 0 || !Owned.Contains(hwnd)) return;
        if (!IsOurs(hwnd)) throw new InvalidOperationException("Refusing to destroy a window not owned by this UI thread.");
        if (!DestroyWindow(hwnd)) throw Error("DestroyWindow");
    }

    internal static bool IsOurs(nint hwnd) => GetWindowThreadProcessId(hwnd, out var pid) == GetCurrentThreadId() && pid == Environment.ProcessId;
    internal static int EnableHostBackdrop(nint hwnd)
    {
        if (hwnd == 0 || !IsOurs(hwnd) || (GetWindowLongPtrW(hwnd, -16).ToInt64() & Child) != 0)
            throw new InvalidOperationException("Host backdrop requires this UI thread's own top-level window.");
        var enabled = 1;
        var result = DwmSetWindowAttribute(hwnd, 17, ref enabled, sizeof(int)); // DWMWA_USE_HOSTBACKDROPBRUSH
        Marshal.ThrowExceptionForHR(result);
        return result;
    }
    internal static int ProcessDpiAwareness()
    {
        Marshal.ThrowExceptionForHR(GetProcessDpiAwareness(0, out var awareness));
        return awareness;
    }
    internal static void Attach(nint host, nint parent)
    {
        if (!IsOurs(host)) throw new InvalidOperationException("DWXS host is not owned by the current UI thread.");
        SetStyle(host, (long)(Child | ClipChildren | ClipSiblings));
        Marshal.SetLastPInvokeError(0);
        var previous = SetParent(host, parent);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw Error("SetParent");
        if (GetParent(host) != parent) throw new InvalidOperationException("Parent relation was not established.");
        if (!SetWindowPos(host, 0, 0, 0, 0, 0, 0x0037)) throw Error("FrameChanged");
    }

    internal static void SetStyle(nint hwnd, long style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtrW(hwnd, -16, (nint)style);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw Error("SetWindowLongPtr");
    }

    internal static void Place(nint hwnd, int x, int y, int width, int height)
    {
        if (!SetWindowPos(hwnd, 0, x, y, width, height, 0x0014)) throw Error("SetWindowPos");
    }
    internal static void Show(nint hwnd, bool visible) => ShowWindow(hwnd, visible ? 4 : 0);
    internal static Rect Client(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var rect)) throw Error("GetClientRect");
        return rect;
    }
    internal static Rect DisplayBounds(nint hwnd) => GetMonitor(hwnd).Monitor;
    internal static Rect WindowBounds(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) throw Error("GetWindowRect");
        return rect;
    }
    internal static nint[] OwnThreadTopLevelWindows()
    {
        var windows = new List<nint>();
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (IsOurs(hwnd)) windows.Add(hwnd);
            return true;
        }, 0);
        return windows.ToArray();
    }
    internal static object Describe(nint hwnd)
    {
        var thread = GetWindowThreadProcessId(hwnd, out var process);
        GetWindowRect(hwnd, out var rect);
        return new { hwnd = $"0x{hwnd:X}", process, thread, windowClass = Class(hwnd), parent = $"0x{GetParent(hwnd):X}", style = $"0x{GetWindowLongPtrW(hwnd, -16):X}", exStyle = $"0x{GetWindowLongPtrW(hwnd, -20):X}", dpi = GetDpiForWindow(hwnd), dpiContext = GetWindowDpiAwarenessContext(hwnd).ToInt64(), rect, logicalVisible = IsWindowVisible(hwnd) };
    }
    internal static object[] ParentChain(nint hwnd)
    {
        var result = new List<object>();
        var seen = new HashSet<nint>();
        while (hwnd != 0 && seen.Add(hwnd) && result.Count < 16) { result.Add(Describe(hwnd)); hwnd = GetParent(hwnd); }
        return result.ToArray();
    }
    internal static string Class(nint hwnd)
    {
        var name = new StringBuilder(256);
        GetClassNameW(hwnd, name, name.Capacity);
        return name.ToString();
    }

    internal static object[] ListExplorerTargets()
    {
        var results = new List<object>();
        EnumWindows((hwnd, _) =>
        {
            if (Class(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                var monitor = GetMonitor(hwnd);
                results.Add(new { window = Describe(hwnd), monitor = monitor.Device, bounds = monitor.Monitor });
            }
            return true;
        }, 0);
        return results.ToArray();
    }

    internal static nint ValidateExplorerTarget(LabOptions options)
    {
        if (options.Mode != "explorer") throw new InvalidOperationException("Explorer access requires explicit mode.");
        var hwnd = (nint)options.ParentHwnd;
        if (!IsWindow(hwnd) || Class(hwnd) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
            throw new ArgumentException("Selected HWND is not a recognized taskbar parent.");
        GetWindowThreadProcessId(hwnd, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (!process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Selected taskbar is not owned by Explorer.");
        var monitor = GetMonitor(hwnd);
        if (!string.Equals(monitor.Device, options.Monitor, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Selected HWND is on a different monitor.");
        if (!GetWindowRect(hwnd, out var rect) || rect.Right - rect.Left <= rect.Bottom - rect.Top || rect.Top < monitor.Monitor.Bottom - 256)
            throw new ArgumentException("Only the selected monitor's bottom taskbar is accepted.");
        return hwnd;
    }

    private static MonitorInfo GetMonitor(nint hwnd)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info)) throw Error("GetMonitorInfo");
        return info;
    }
    private static Win32Exception Error(string call) => new(Marshal.GetLastPInvokeError(), call);

    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProc? Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu, Name;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate nint WindowProc(nint hwnd, uint message, nuint wparam, nint lparam);
    private delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nuint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetParent(nint hwnd, nint parent);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] internal static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumProc callback, nint param);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, int cursor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(nint process, out int awareness);
}

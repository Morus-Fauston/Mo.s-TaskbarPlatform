using System.Runtime.InteropServices;
using Mtp.Platform.Core;

namespace Mtp.Host.Experiments;

// All native declarations belong to the experimental Host adapter, never Core or Contracts.
internal static class ProbeNative
{
    internal const uint ChildStyle = 0x40000000 | 0x08000000 | 0x04000000; // CHILD, DISABLED, CLIPSIBLINGS
    internal const uint ExtendedStyle = 0x08000000 | 0x00080000 | 0x00000020 | 0x00000080;
    internal delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    internal delegate void EventProcedure(nint hook, uint kind, nint window, int objectId, int childId, uint threadId, uint time);
    internal delegate bool MonitorProcedure(nint monitor, nint dc, ref Rect rect, nint data);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelRect Pixels => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Paint
    {
        public nint DC;
        public int Erase;
        public Rect Bounds;
        public int Restore, Incremental;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu;
        public string Name;
        public nint SmallIcon;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint window, out Paint paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(nint window, ref Paint paint);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint window);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] internal static extern nuint GetClassLongPtrW(nint window, int index);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern nint GetDCEx(nint window, nint region, uint flags);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] internal static extern int GetClipBox(nint dc, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetLayeredWindowAttributes(nint window, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWinEventHook(uint first, uint last, nint module, EventProcedure callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProcedure callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowExW(nint parent, nint after, string cls, string? title);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, uint type, out uint x, out uint y);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, int size);
}

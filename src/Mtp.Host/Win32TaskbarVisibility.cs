using System.Runtime.InteropServices;
using System.Text;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>Reads window geometry only; it never opens or sends messages to an application's process.</summary>
internal static class Win32TaskbarVisibility
{
    internal static nint ForegroundWindow => Native.GetForegroundWindow();

    internal static bool? IsFullScreenWindow(nint window, PixelRect display)
    {
        if (window == 0 || !Native.IsWindow(window) || !display.IsValid) return null;
        if (!Native.IsWindowVisible(window) || Native.IsIconic(window)) return false;
        var style = Native.GetWindowLongPtrW(window, -16);
        // Custom title bars can extend a normally maximized client to the monitor edges.
        if ((style & 0x01C00000) == 0x01C00000) return false; // WS_MAXIMIZE and WS_CAPTION.
        var name = new StringBuilder(256);
        if (Native.GetClassNameW(window, name, name.Capacity) == 0) return null;
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (Native.DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(uint)) < 0 || cloaked != 0) return null;
        if (!Native.GetClientRect(window, out var client)) return null;
        var start = new Native.Point { X = client.Left, Y = client.Top };
        var end = new Native.Point { X = client.Right, Y = client.Bottom };
        if (!Native.ClientToScreen(window, ref start) || !Native.ClientToScreen(window, ref end) || !Native.IsWindow(window)) return null;
        // Client bounds avoid classifying a large decorated window as fullscreen.
        return new PixelRect(start.X, start.Y, end.X - start.X, end.Y - start.Y).Contains(display);
    }

    internal static bool? ReadTaskbarExposure(nint taskbar, TaskbarDockDisplay display, out bool autoHide)
    {
        autoHide = false;
        // Absence is a placement fault, not proof that the desktop is in fullscreen mode.
        if (taskbar == 0) return true;
        if (!Native.IsWindow(taskbar)) return null;
        if (!Native.IsWindowVisible(taskbar)) return false;
        if (!Native.GetWindowRect(taskbar, out var rect)) return null;
        var bar = rect.ToRect();
        var data = new Native.AppBarData
        {
            Size = (uint)Marshal.SizeOf<Native.AppBarData>(),
            Edge = 3,
            Bounds = new() { Left = display.Bounds.X, Top = display.Bounds.Y, Right = (int)display.Bounds.Right, Bottom = (int)display.Bounds.Bottom }
        };
        autoHide = Native.SHAppBarMessage(0xB, ref data) == taskbar;
        return EvaluateTaskbarExposure(display.Bounds, display.WorkArea, bar, autoHide);
    }

    internal static bool? EvaluateTaskbarExposure(PixelRect display, PixelRect workArea, PixelRect bar, bool autoHide)
    {
        if (!display.IsValid || !bar.IsValid) return null;
        if (bar.Width <= bar.Height) return true; // Unsupported orientation is handled by placement/fallback.
        if (bar.Y >= display.Bottom - 2 || bar.Bottom > display.Bottom) return false;
        if (autoHide)
            return display.Contains(bar) && bar.Bottom == display.Bottom && bar.Height > 2;
        if (bar.Bottom == display.Bottom && workArea.Bottom > bar.Y) return null;
        return true;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            internal readonly PixelRect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct AppBarData
        {
            public uint Size;
            public nint Window;
            public uint Callback, Edge;
            public Rect Bounds;
            public nint Parameter;
        }
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint window, StringBuilder name, int count);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref Point point);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, int size);
        [DllImport("shell32.dll")] internal static extern nint SHAppBarMessage(uint message, ref AppBarData data);
    }
}

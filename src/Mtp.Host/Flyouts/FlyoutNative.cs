using System.ComponentModel;
using System.Runtime.InteropServices;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

internal static class FlyoutNative
{
    internal const uint WmInput = 0x00ff, WmMouseActivate = 0x0021;
    internal delegate nint WindowProcedure(nint window, uint message, nuint wparam, nint lparam);
    internal delegate void WinEventProcedure(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
    {
        public uint Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RawDevice { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] internal struct RawHeader { public uint Type, Size; public nint Device, WParam; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint Extra;
    }
    internal static bool IsAfter(uint time, uint baseline) => unchecked((int)(time - baseline)) > 0;
    internal static nint Root(nint window) => window == 0 ? 0 : GetAncestor(window, 2);
    internal static bool BelongsTo(nint candidate, IEnumerable<nint> owners)
    {
        var set = owners.Where(x => x != 0).ToHashSet();
        var current = candidate;
        for (int depth = 0; current != 0 && depth < 32; depth++)
        {
            if (set.Contains(current)) return true;
            var root = Root(current);
            if (set.Contains(root)) return true;
            var next = GetWindow(root, 4);
            if (next == current || next == root) break;
            current = next;
        }
        return false;
    }
    internal static bool Contains(PixelRect rectangle, Point point) => point.X >= rectangle.X && point.Y >= rectangle.Y &&
        point.X < rectangle.Right && point.Y < rectangle.Bottom;
    internal static nint CurrentFocus()
    {
        var foreground = GetForegroundWindow();
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return foreground != 0 && GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info) ? info.Focus : 0;
    }
    internal static PixelRect Bounds(nint window)
    {
        if (!GetWindowRect(window, out var rect)) throw Error("GetWindowRect");
        long width = (long)rect.Right - rect.Left, height = (long)rect.Bottom - rect.Top;
        if (width <= 0 || width > int.MaxValue || height <= 0 || height > int.MaxValue) throw new InvalidOperationException("浮窗原生矩形无效");
        return new(rect.Left, rect.Top, (int)width, (int)height);
    }
    internal static TaskbarDipRect ToDip(PixelRect value, uint dpi)
    {
        if (dpi == 0 || !value.IsValid) throw new ArgumentException("Invalid flyout environment");
        var scale = 96d / dpi;
        return new(value.X * scale, value.Y * scale, value.Width * scale, value.Height * scale);
    }
    internal static PixelRect ToPixels(TaskbarDipRect value, uint dpi)
    {
        if (dpi == 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        var scale = dpi / 96d;
        var left = Math.Round(value.X * scale, MidpointRounding.AwayFromZero);
        var top = Math.Round(value.Y * scale, MidpointRounding.AwayFromZero);
        var right = Math.Round(value.Right * scale, MidpointRounding.AwayFromZero);
        var bottom = Math.Round(value.Bottom * scale, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) ||
            left < int.MinValue || left > int.MaxValue || top < int.MinValue || top > int.MaxValue ||
            right < int.MinValue || right > int.MaxValue || bottom < int.MinValue || bottom > int.MaxValue ||
            right - left < 1 || right - left > int.MaxValue || bottom - top < 1 || bottom - top > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "浮窗像素边缘超出原生整数范围");
        return new((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }
    internal static void Position(nint window, PixelRect bounds)
    {
        if (!SetWindowPos(window, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010)) throw Error("SetWindowPos");
    }
    internal static void Opacity(nint window, double opacity)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        var style = GetWindowLongPtrW(window, -20).ToInt64();
        if ((style & 0x00080000) == 0)
        {
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLongPtrW(window, -20, new nint(style | 0x00080000)) == 0 && Marshal.GetLastPInvokeError() != 0)
                throw Error("SetLayeredStyle");
        }
        if (!SetLayeredWindowAttributes(window, 0, (byte)Math.Round(opacity * 255), 2)) throw Error("SetLayeredWindowAttributes");
    }
    internal static byte ReadOpacity(nint window)
    {
        if (!GetLayeredWindowAttributes(window, out _, out var opacity, out var flags) || (flags & 2) == 0)
            throw Error("GetLayeredWindowAttributes");
        return opacity;
    }
    internal static void NoActivate(nint window, bool noActivate)
    {
        var value = GetWindowLongPtrW(window, -20).ToInt64() | 0x80;
        value = noActivate ? value | 0x08000000 : value & ~0x08000000;
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtrW(window, -20, new nint(value)) == 0 && Marshal.GetLastPInvokeError() != 0) throw Error("SetWindowLongPtr");
    }
    internal static Exception Error(string operation) => new Win32Exception(Marshal.GetLastPInvokeError(), operation);

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern int GetMessageTime();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte opacity, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLayeredWindowAttributes(nint window, out uint key, out byte opacity, out uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterClassW(string className, nint instance);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint DefWindowProcW(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? module);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProcedure callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
}

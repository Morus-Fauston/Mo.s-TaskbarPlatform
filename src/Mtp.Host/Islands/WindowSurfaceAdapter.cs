using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mtp.Host.Islands;

/// <summary>Top-level Window transparency only; never apply to an Explorer child host.</summary>
internal static class WindowSurfaceAdapter
{
    // One Windows.System queue controller is retained for the process UI thread.
    private static nint queueController;
    internal static void EnsureCompositionQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
        var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out queueController));
        if (queueController == 0) throw new InvalidOperationException("Windows composition queue unavailable");
    }
    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);

    internal static string PrepareAndPaint(nint window)
    {
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(window, ref margins));
        uint backdrop = 1; // DWMSBT_NONE: do not leave the default opaque system surface behind the tint.
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 38, ref backdrop, sizeof(uint)));
        var blur = new BlurBehind { Flags = 1, Enabled = 1 };
        Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(window, ref blur));
        var dc = GetDC(window);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取窗口表面");
        nint brush = 0;
        try
        {
            brush = CreateSolidBrush(0);
            if (brush == 0 || !GetClientRect(window, out var rectangle) || FillRect(dc, ref rectangle, brush) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法初始化透明窗口表面");
            return $"painted {rectangle.Right - rectangle.Left}x{rectangle.Bottom - rectangle.Top}; visual-pending-human";
        }
        finally
        {
            if (brush != 0) DeleteObject(brush);
            ReleaseDC(window, dc);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public uint Flags; public int Enabled; public nint Region; public int Transition; }
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint window, ref BlurBehind blur);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref uint value, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Rectangle rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern int FillRect(nint dc, ref Rectangle rectangle, nint brush);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
}

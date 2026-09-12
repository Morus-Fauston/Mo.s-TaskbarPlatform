using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host.WindowTests;

internal sealed class ChildMaterialRegression(Application app)
{
    private readonly string log = Path.Combine(AppContext.BaseDirectory, "child-material.log");
    private ExplorerTaskbarProbeWindow? child;
    private Window? lifetime;
    private DispatcherTimer? timer;
    private nint parent;
    private readonly bool pixels = Environment.GetCommandLineArgs().Contains("--child-pixels");
    private int stage;

    public void Run()
    {
        File.WriteAllText(log, "Child material regression\n");
        try
        {
            lifetime = new Window();
            parent = Native.CreateWindowExW(0x08000080, "STATIC", "MTP child material fixture", 0x90000006,
                pixels ? 80 : -30000, pixels ? 80 : -30000, 640, 64, 0, 0, 0, 0);
            if (parent == 0) throw new InvalidOperationException("Parent fixture creation failed.");
            if (pixels) Native.SetWindowPos(parent, -1, 80, 80, 640, 64, 0x50);
            var queueArgs = new object?[] { null };
            var queueReady = typeof(Win32ExplorerTaskbarEmbedAdapter).GetMethod("EnsureSystemDispatcherQueue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, queueArgs);
            File.AppendAllText(log, $"System dispatcher={queueReady}: {queueArgs[0]}\n");
            child = new ExplorerTaskbarProbeWindow(HostComponentDisplayModel.From(
                new Component(new StableIdentity(new StableId("child-material")), CapabilityState.Available)));
            child.PrepareHidden(new SizeInt32(360, 48), false);
            var hwnd = child.WindowHandle;
            Native.SetWindowLongPtrW(hwnd, -16, 0x40000000);
            Native.SetParent(hwnd, parent);
            if (Native.GetAncestor(hwnd, 1) != parent) throw new InvalidOperationException("Parent did not bind.");
            Native.SetWindowPos(hwnd, 0, 8, 8, 360, 48, 0x70);
            var materialArgs = new object?[] { 0.1, 0xFFFFFFu, null };
            var applied = typeof(ExplorerTaskbarProbeWindow).GetMethod("TryApplyChildMaterial", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(child, materialArgs);
            File.AppendAllText(log, $"Apply after parent={applied}; {materialArgs[1]}\n");
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var state = child.DescribeBackdropState();
                    File.AppendAllText(log, state + "\n");
                    if (!state.Contains("connected", StringComparison.OrdinalIgnoreCase) || state.Contains("failed", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Child color brush did not connect.");
                    if (!Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce(hwnd, out var paint))
                        throw new InvalidOperationException(paint);
                    File.AppendAllText(log, paint + "\n");
                    if (pixels && stage++ == 0) { timer.Start(); return; }
                    if (pixels)
                    {
                        Native.DwmFlush();
                        var dc = Native.GetDC(0);
                        try
                        {
                            var parentPixel = Native.GetPixel(dc, 680, 110);
                            var childPixel = Native.GetPixel(dc, 420, 130);
                            File.AppendAllText(log, $"parent pixel=0x{parentPixel:X6}; child pixel=0x{childPixel:X6}; expected approximately C7C7C7\n");
                            if (parentPixel != 0xFFFFFF || Math.Abs((int)(childPixel & 255) - 199) > 8)
                                throw new InvalidOperationException("Child background did not blend its alpha with the white parent fixture.");
                        }
                        finally { Native.ReleaseDC(0, dc); }
                    }
                    File.AppendAllText(log, "PASS: child brush connection and surface paint (not human acceptance).\n");
                    Finish(null);
                }
                catch (Exception error) { Finish(error); }
            };
            timer.Start();
        }
        catch (Exception error) { Finish(error); }
    }

    private void Finish(Exception? error)
    {
        timer?.Stop();
        if (error is not null) File.AppendAllText(log, error + "\n");
        try
        {
            if (child is not null)
            {
                child.ClearMaterial();
                Native.SetParent(child.WindowHandle, 0);
                Native.SetWindowLongPtrW(child.WindowHandle, -16, unchecked((nint)(int)0x80000000));
                child.Close();
            }
            if (parent != 0) Native.DestroyWindow(parent);
            lifetime?.Close();
        }
        finally { Environment.ExitCode = error is null ? 0 : 1; app.Exit(); }
    }

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] internal static extern nint SetParent(nint window, nint parent);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
        [DllImport("gdi32.dll")] internal static extern uint GetPixel(nint dc, int x, int y);
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
    }
}

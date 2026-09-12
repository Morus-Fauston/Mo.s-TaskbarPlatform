using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Runs the layered route continuously with moving content, so the cost can be watched live in Task
/// Manager instead of being inferred from millisecond samples.
///
/// A native WinUI window composites on the GPU: updating a value costs the UI thread almost nothing,
/// and the frame never passes through the CPU. The layered route cannot do that, because a layered
/// window's content is an application-supplied bitmap, so every update has to be rendered, read back
/// out of video memory, copied and submitted again.
///
/// Run with <c>--sustained</c>. Add <c>--native</c> for the comparison run, which animates the same
/// content without the layer so the two can be watched side by side. <c>--seconds=N</c> changes how
/// long it runs (default 120).
/// </summary>
internal sealed class SustainedCostProbe(Application app)
{
    private const int Width = 260;
    private const int Height = 56;
    private const int IntervalMilliseconds = 33;
    private const int DefaultSeconds = 120;
    private const uint UlwAlpha = 0x00000002;
    private const uint WsChild = 0x40000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExLayered = 0x00080000;
    private const int GwlStyle = -16;

    private readonly string log = Path.Combine(AppContext.BaseDirectory, "sustained-cost.log");
    private readonly bool nativeOnly = Environment.GetCommandLineArgs().Contains("--native");
    private readonly int seconds = ReadSeconds();

    private Window? lifetime;
    private DispatcherQueue? queue;
    private DispatcherTimer? timer;
    private Window? renderer;
    private TextBlock? readout;
    private ProgressBar? bar;
    private nint taskbar;
    private nint classAtom;
    private nint memoryDc;
    private nint bitmap;
    private nint bitmapBits;
    private nint layerWindow;
    private readonly Stopwatch clock = new();
    private double cpuAtStart;
    private int frames;
    private int skipped;
    private bool working;
    private int pixelWidth;
    private int pixelHeight;

    private readonly Native.WndProc windowProc = static (window, message, wParam, lParam) =>
        Native.DefWindowProcW(window, message, wParam, lParam);

    public void Run()
    {
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        File.WriteAllText(log, nativeOnly
            ? "Sustained cost run: native WinUI only (no layer)\n"
            : "Sustained cost run: layered child window fed by RenderTargetBitmap\n");
        try
        {
            lifetime = new Window();
            queue = lifetime.DispatcherQueue;

            var panel = BuildPanel();
            renderer = new Window { Content = panel };
            renderer.AppWindow.Resize(new SizeInt32(Width, Height));
            panel.UpdateLayout();

            var handle = WinRT.Interop.WindowNative.GetWindowHandle(renderer);
            File.AppendAllText(log, string.Create(
                CultureInfo.InvariantCulture,
                $"dpi {Native.GetDpiForWindow(handle)}\n"));

            var probe = new RenderTargetBitmap();
            await probe.RenderAsync(panel);
            pixelWidth = probe.PixelWidth;
            pixelHeight = probe.PixelHeight;

            if (nativeOnly)
            {
                // Leave the window as a normal top-level window and just animate it. Whatever Task
                // Manager shows here is the cost of the animation itself, which both routes pay.
                var rect = DisplayAreaOf(renderer);
                _ = Native.SetWindowPos(handle, 0, rect.X, rect.Y, Width, Height, 0x0040);
            }
            else
            {
                CreateLayerWindow();
            }

            var budget = nativeOnly ? "native (no layer feed)" : $"{pixelWidth * pixelHeight * 4:N0} bytes per update";
            File.AppendAllText(log, string.Create(
                CultureInfo.InvariantCulture,
                $"running for {seconds} s at {1000 / IntervalMilliseconds} updates/s, {pixelWidth}x{pixelHeight} px, {budget}\n"));

            cpuAtStart = CpuNow();
            clock.Start();

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IntervalMilliseconds) };
            timer.Tick += async (_, _) => await Tick();
            timer.Start();
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    private static int ReadSeconds()
    {
        var argument = Environment.GetCommandLineArgs()
            .FirstOrDefault(value => value.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
        return argument is not null && int.TryParse(argument.AsSpan("--seconds=".Length), out var value) && value > 0
            ? value
            : DefaultSeconds;
    }

    private async Task Tick()
    {
        try
        {
            // The timer is left repeating. Stopping and restarting it inside the handler would add the
            // work time to the period, so a 30/s target quietly becomes whatever the work allows and
            // the run no longer measures the rate it claims to.
            if (working)
            {
                skipped++;
                return;
            }

            working = true;
            frames++;

            // Move something on every tick. A static widget would let the compositor skip the work
            // entirely and the measurement would flatter the layered route.
            var phase = (frames % 60) / 60.0;
            if (readout is not null)
            {
                readout.Text = string.Create(CultureInfo.InvariantCulture, $"{phase * 100:0}%");
            }

            if (bar is not null)
            {
                bar.Value = phase * 100;
            }

            if (!nativeOnly)
            {
                await FeedLayer();
            }

            if (clock.Elapsed.TotalSeconds >= seconds)
            {
                Report();
                timer!.Stop();
                Finish(null);
                return;
            }
        }
        catch (Exception error)
        {
            timer?.Stop();
            Finish(error);
        }
        finally
        {
            working = false;
        }
    }

    private async Task FeedLayer()
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(renderer!.Content as FrameworkElement);

        var buffer = await target.GetPixelsAsync();
        if (buffer is null || buffer.Length == 0)
        {
            throw new InvalidOperationException("GetPixelsAsync handed back an empty buffer.");
        }

        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }

        Marshal.Copy(pixels, 0, bitmapBits, pixels.Length);

        var size = new Native.Size { Width = pixelWidth, Height = pixelHeight };
        var source = new Native.Point { X = 0, Y = 0 };
        var blend = new Native.BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (!Native.UpdateLayeredWindow(layerWindow, 0, 0, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha))
        {
            throw new InvalidOperationException("UpdateLayeredWindow failed: " + Native.LastError());
        }
    }

    private void Report()
    {
        clock.Stop();
        var cpu = CpuNow() - cpuAtStart;
        var wall = clock.Elapsed.TotalMilliseconds;
        var duty = cpu / wall;

        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"\nran {wall / 1000:0.0} s, {frames} updates, {frames / clock.Elapsed.TotalSeconds:0.0} updates/s\n"
            + $"ticks skipped because the previous update was still running: {skipped}\n"
            + $"CPU time consumed: {cpu / 1000:0.000} s over {wall / 1000:0.0} s\n"
            + $"average: {duty:P1} of one logical processor\n"
            + $"CPU per update: {cpu / Math.Max(frames, 1):0.000} ms\n"));
    }

    private Grid BuildPanel()
    {
        readout = new TextBlock
        {
            Text = "0%",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        bar = new ProgressBar
        {
            Value = 0,
            Width = 200,
            Height = 4,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var panel = new Grid { Width = Width, Height = Height, Background = null };
        panel.Children.Add(bar);
        panel.Children.Add(readout);
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        });
        return panel;
    }

    private static (int X, int Y) DisplayAreaOf(Window window)
    {
        _ = window;
        var work = new Native.Rect();
        _ = Native.SystemParametersInfoW(0x0030, 0, ref work, 0);
        return (work.Left + 100, work.Top + 100);
    }

    private void CreateLayerWindow()
    {
        taskbar = Native.FindWindowW("Shell_TrayWnd", null);
        if (taskbar == 0)
        {
            throw new InvalidOperationException("Shell_TrayWnd was not found; this probe needs a visible taskbar.");
        }

        var description = new Native.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<Native.WndClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(windowProc),
            Instance = Native.GetModuleHandleW(null),
            BackgroundBrush = 0,
            ClassName = "MtpSustainedCostProbe",
        };
        classAtom = Native.RegisterClassExW(ref description);
        if (classAtom == 0)
        {
            throw new InvalidOperationException("RegisterClassExW failed: " + Native.LastError());
        }

        Native.GetWindowRect(taskbar, out var rect);

        // Child coordinates are relative to the parent's client area, not the screen.
        var x = (rect.Right - rect.Left) * 3 / 4 - (pixelWidth / 2);
        var y = ((rect.Bottom - rect.Top) - pixelHeight) / 2;

        memoryDc = Native.CreateCompatibleDC(0);
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
            Width = pixelWidth,
            Height = -pixelHeight,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };
        bitmap = Native.CreateDIBSection(memoryDc, ref header, 0, out bitmapBits, 0, 0);
        if (bitmap == 0 || bitmapBits == 0)
        {
            throw new InvalidOperationException("The layered bitmap could not be created.");
        }

        _ = Native.SelectObject(memoryDc, bitmap);

        layerWindow = Native.CreateWindowExW(
            WsExLayered, "MtpSustainedCostProbe", string.Empty, WsChild | WsVisible,
            x, y, pixelWidth, pixelHeight, taskbar, 0, Native.GetModuleHandleW(null), 0);
        if (layerWindow == 0)
        {
            throw new InvalidOperationException("CreateWindowExW failed: " + Native.LastError());
        }

        // No SWP_NOZORDER: it cancels HWND_TOP, and a reparented child would then sit behind the
        // taskbar's own content window while looking perfectly healthy.
        _ = Native.SetWindowPos(layerWindow, 0, x, y, pixelWidth, pixelHeight, 0x0040);

        // Opaque red first, so "the layer is really on screen" can be checked. This is also the first
        // submit, and it happens after SetParent: submitting while the window is still top level makes
        // the layer permanently stop showing.
        var count = pixelWidth * pixelHeight;
        var pixels = new byte[count * 4];
        for (var index = 0; index < count; index++)
        {
            pixels[(index * 4) + 2] = 255;
            pixels[(index * 4) + 3] = 255;
        }

        Marshal.Copy(pixels, 0, bitmapBits, pixels.Length);
        var size = new Native.Size { Width = pixelWidth, Height = pixelHeight };
        var source = new Native.Point { X = 0, Y = 0 };
        var blend = new Native.BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (!Native.UpdateLayeredWindow(layerWindow, 0, 0, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha))
        {
            throw new InvalidOperationException("The first UpdateLayeredWindow failed: " + Native.LastError());
        }

        Native.DwmFlush();

        // Without this the run could be measuring an invisible window and look identical.
        Native.GetWindowRect(layerWindow, out var placed);
        var sample = SampleScreen((placed.Left + placed.Right) / 2, (placed.Top + placed.Bottom) / 2);
        if (sample != 0x0000FF)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The layer should be opaque red at its centre but the screen reads 0x{sample:X6}; a hidden window would make this run meaningless."));
        }

        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"layer window {placed.Left},{placed.Top}-{placed.Right},{placed.Bottom}, centre reads red\n"));
    }

    private static uint SampleScreen(int x, int y)
    {
        var screen = Native.GetDC(0);
        try
        {
            return Native.GetPixel(screen, x, y);
        }
        finally
        {
            Native.ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// CPU time consumed on this thread, in milliseconds. Read once at the start and once at the end:
    /// over a run of a minute the counter's scheduler-clock granularity stops mattering.
    /// </summary>
    private static double CpuNow()
    {
        if (!Native.GetThreadTimes(Native.GetCurrentThread(), out _, out _, out var kernel, out var user))
        {
            throw new InvalidOperationException("GetThreadTimes failed: " + Native.LastError());
        }

        return (kernel + user) / 10000.0;
    }

    private void Finish(Exception? error)
    {
        timer?.Stop();
        if (error is not null)
        {
            File.AppendAllText(log, "FAILED: " + error + "\n");
        }

        try
        {
            if (layerWindow != 0)
            {
                _ = Native.SetParent(layerWindow, 0);
                _ = Native.SetWindowLongPtrW(layerWindow, GwlStyle, unchecked((nint)(int)(WsPopup | WsVisible)));
                _ = Native.DestroyWindow(layerWindow);
            }

            if (classAtom != 0)
            {
                _ = Native.UnregisterClassW("MtpSustainedCostProbe", Native.GetModuleHandleW(null));
            }

            if (bitmap != 0)
            {
                _ = Native.DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                _ = Native.DeleteDC(memoryDc);
            }

            renderer?.Close();
            lifetime?.Close();
        }
        finally
        {
            Environment.ExitCode = error is null ? 0 : 1;
            app.Exit();
        }
    }

    private static class Native
    {
        internal delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Size
        {
            internal int Width;
            internal int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BlendFunction
        {
            internal byte BlendOp;
            internal byte BlendFlags;
            internal byte SourceConstantAlpha;
            internal byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width;
            internal int Height;
            internal ushort Planes;
            internal ushort BitCount;
            internal uint Compression;
            internal uint SizeImage;
            internal int XPelsPerMeter;
            internal int YPelsPerMeter;
            internal uint ColorsUsed;
            internal uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WndClassEx
        {
            internal uint Size;
            internal uint Style;
            internal nint WindowProc;
            internal int ClassExtra;
            internal int WindowExtra;
            internal nint Instance;
            internal nint Icon;
            internal nint Cursor;
            internal nint BackgroundBrush;
            internal string? MenuName;
            internal string ClassName;
            internal nint SmallIcon;
        }

        internal static string LastError() => Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowW(string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WndClassEx description);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool UnregisterClassW(string className, nint instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UpdateLayeredWindow(
            nint window, nint destinationDc, nint destination, ref Size size,
            nint sourceDc, ref Point source, uint colorKey, ref BlendFunction blend, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetParent(nint window, nint parent);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetWindowRect(nint window, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SystemParametersInfoW(uint action, uint parameter, ref Rect value, uint flags);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint window);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint CreateCompatibleDC(nint dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint SelectObject(nint dc, nint value);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteObject(nint value);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteDC(nint dc);

        [DllImport("user32.dll")]
        internal static extern nint GetDC(nint window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(nint window, nint dc);

        [DllImport("gdi32.dll")]
        internal static extern uint GetPixel(nint dc, int x, int y);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? name);

        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetThreadTimes(nint thread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
    }
}

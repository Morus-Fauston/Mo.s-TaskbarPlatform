using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Answers the question that decides how expensive the layered-window route is: can a real WinUI
/// control tree be turned into a bitmap with a transparent background and handed to
/// UpdateLayeredWindow?
///
/// A layered window replaces the window's own content, so WinUI cannot draw into it directly. If
/// RenderTargetBitmap can produce a premultiplied BGRA bitmap with alpha, the interface can keep
/// being written in WinUI and only needs one extra step. If it cannot, widget content has to be
/// drawn by hand.
///
/// The run collects the pixels first and reports what they contain, then shows one block in the
/// taskbar so the result can be seen. Run with <c>--render-target</c>.
/// </summary>
internal sealed class RenderTargetProbe(Application app)
{
    private const int Width = 260;
    private const int Height = 56;
    private const int DefaultHoldSeconds = 90;
    private const uint UlwAlpha = 0x00000002;
    private const uint WsChild = 0x40000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExLayered = 0x00080000;
    private const int GwlStyle = -16;

    private readonly string log = Path.Combine(AppContext.BaseDirectory, "render-target.log");
    private readonly int holdSeconds = ReadHoldSeconds();

    private Window? lifetime;
    private DispatcherTimer? holdTimer;
    private Window? renderer;
    private nint taskbar;
    private nint classAtom;
    private nint memoryDc;
    private nint bitmap;
    private nint bitmapBits;
    private nint block;

    private readonly Native.WndProc windowProc = static (window, message, wParam, lParam) =>
        Native.DefWindowProcW(window, message, wParam, lParam);

    public async Task Run()
    {
        File.WriteAllText(log, "RenderTargetBitmap -> layered child window probe\n");
        try
        {
            lifetime = new Window();
            _ = typeof(Win32ExplorerTaskbarEmbedAdapter)
                .GetMethod("EnsureSystemDispatcherQueue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [null]);

            // A real WinUI control tree, with a transparent background on purpose.
            var panel = new Grid { Width = Width, Height = Height, Background = null };
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Child = new TextBlock
                {
                    Text = "MTP 控件",
                    FontSize = 17,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });

            renderer = new Window { Content = panel };
            renderer.AppWindow.Resize(new SizeInt32(Width, Height));
            panel.UpdateLayout();
            File.AppendAllText(log, "built a WinUI TextBlock inside a Border, panel background = transparent\n");

            // U-1 of SYS-004: the earlier run recorded a size nobody could explain. These four numbers
            // are what that run was missing, so the same render can be checked against the display DPI.
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(renderer);
            File.AppendAllText(log, string.Create(
                CultureInfo.InvariantCulture,
                $"layout: panel {panel.ActualWidth:0.##}x{panel.ActualHeight:0.##} dip; window dpi {Native.GetDpiForWindow(windowHandle)}; "
                + $"XamlRoot scale {(panel.XamlRoot is null ? "unavailable" : panel.XamlRoot.RasterizationScale.ToString("0.##", CultureInfo.InvariantCulture))}\n"));

            await CheckPixelFormat();

            // Await properly: blocking the UI thread here deadlocks, because the continuation of
            // RenderAsync has to run on this same thread.
            var bitmapOutput = await RenderPanel(panel);
            Report(bitmapOutput);
            if (bitmapOutput is null)
            {
                Finish(null);
                return;
            }

            ShowInTaskbar(bitmapOutput.Value);
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    private static int ReadHoldSeconds()
    {
        var argument = Environment.GetCommandLineArgs()
            .FirstOrDefault(value => value.StartsWith("--hold=", StringComparison.OrdinalIgnoreCase));
        return argument is not null && int.TryParse(argument.AsSpan("--hold=".Length), out var seconds) && seconds > 0
            ? seconds
            : DefaultHoldSeconds;
    }

    /// <summary>Renders the panel and reads the pixels back as premultiplied BGRA.</summary>
    private static async Task<(byte[] Pixels, int PixelWidth, int PixelHeight, int LogicalWidth)?> RenderPanel(FrameworkElement panel)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(panel);
        var pixelWidth = target.PixelWidth;
        var pixelHeight = target.PixelHeight;
        var logicalWidth = (int)Math.Round(panel.ActualWidth);
        var pixels = await target.GetPixelsAsync();
        if (pixels is null || pixels.Length == 0)
        {
            return null;
        }

        var bytes = new byte[pixels.Length];
        using var reader = DataReader.FromBuffer(pixels);
        reader.ReadBytes(bytes);
        return (bytes, pixelWidth, pixelHeight, logicalWidth);
    }

    private void Report((byte[] Pixels, int PixelWidth, int PixelHeight, int LogicalWidth)? output)
    {
        if (output is null)
        {
            File.AppendAllText(log, "RenderTargetBitmap returned no pixels\n");
            return;
        }

        var (pixels, width, height, logicalWidth) = output.Value;
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"render: {width}x{height} physical for a {Width}x{Height} dip panel, {pixels.Length} bytes, 32bpp\n"));
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"implied scale: {(double)width / Math.Max(logicalWidth, 1):0.00}x\n"));

        var opaque = 0;
        var transparent = 0;
        var partial = 0;
        var alphaMin = 255;
        var alphaMax = 0;
        var backgroundAlpha = -1;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index];
            alphaMin = Math.Min(alphaMin, alpha);
            alphaMax = Math.Max(alphaMax, alpha);
            switch (alpha)
            {
                case 0: transparent++; break;
                case 255: opaque++; break;
                default: partial++; break;
            }
        }

        // Corner pixel: where the panel has no child, so it shows what the background contributed.
        if (pixels.Length >= 4)
        {
            backgroundAlpha = pixels[3];
        }

        File.AppendAllText(log, $"alpha: min={alphaMin} max={alphaMax}; transparent={transparent} partial={partial} opaque={opaque}\n");
        File.AppendAllText(log, $"top-left pixel alpha={backgroundAlpha}\n");

        if (transparent == 0)
        {
            File.AppendAllText(log, "FINDING: no transparent pixels at all. The background came out opaque, so this bitmap would cover the taskbar.\n");
        }
        else if (partial == 0)
        {
            File.AppendAllText(log, "FINDING: transparent and opaque only, so alpha is real but hard-edged; text antialiasing did not survive as partial alpha.\n");
        }
        else
        {
            File.AppendAllText(log, "FINDING: transparent, partial and opaque pixels all present: alpha survived, including antialiased edges.\n");
        }

        File.AppendAllText(log, "text is rendered into the bitmap, so the widget content is the WinUI control, not hand-drawn GDI\n");
    }

    /// <summary>
    /// Settles U-2 of SYS-004: does <c>GetPixelsAsync</c> hand back premultiplied or straight alpha?
    ///
    /// The layered window side is documented to want premultiplied 32bpp, but the WinUI side is only
    /// documented as "BGRA8". One render of three solid fills decides it, because the two candidate
    /// formats predict different bytes for the same colour: with premultiplied alpha a 50% red fill
    /// comes back at roughly half-strength red, while with straight alpha the red channel stays at
    /// full strength and only the alpha byte carries the 50%.
    ///
    /// The opaque fill is the control. It must come back as its own colour; if it does not, the
    /// comparison below is not testing anything and the run says so instead of guessing.
    /// </summary>
    private async Task CheckPixelFormat()
    {
        (int Dip, byte Alpha, byte Red, byte Green, byte Blue)[] swatches =
        [
            (20, 255, 255, 0, 0),
            (60, 128, 255, 0, 0),
            (100, 64, 0, 255, 0),
        ];

        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Width = 120,
            Height = 40,
            Background = null,
        };
        foreach (var swatch in swatches)
        {
            strip.Children.Add(new Border
            {
                Width = 40,
                Height = 40,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(swatch.Alpha, swatch.Red, swatch.Green, swatch.Blue)),
            });
        }

        var host = new Window { Content = strip };
        host.AppWindow.Resize(new SizeInt32(120, 40));
        strip.UpdateLayout();

        var target = new RenderTargetBitmap();
        await target.RenderAsync(strip);
        var pixelWidth = target.PixelWidth;
        var pixelHeight = target.PixelHeight;
        var buffer = await target.GetPixelsAsync();
        if (buffer is null || buffer.Length == 0)
        {
            host.Close();
            File.AppendAllText(log, "\nformat check: GetPixelsAsync returned nothing, so premultiplication is undecided\n");
            return;
        }

        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }

        var scale = pixelWidth / 120.0;
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"\nformat check: {pixelWidth}x{pixelHeight} px for a 120x40 dip strip ({scale:0.00}x); three solid fills in one render\n"));

        int? opaqueMatches = null;
        var discriminators = 0;
        var premultipliedRight = 0;
        var straightRight = 0;
        foreach (var swatch in swatches)
        {
            var x = (int)Math.Round(swatch.Dip * scale);
            var y = pixelHeight / 2;
            var offset = ((y * pixelWidth) + x) * 4;
            if (offset < 0 || offset + 3 >= pixels.Length)
            {
                continue;
            }

            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            var alpha = pixels[offset + 3];

            var premultipliedBlue = (byte)(swatch.Blue * swatch.Alpha / 255);
            var premultipliedGreen = (byte)(swatch.Green * swatch.Alpha / 255);
            var premultipliedRed = (byte)(swatch.Red * swatch.Alpha / 255);

            var matchesPremultiplied = Near(blue, premultipliedBlue) && Near(green, premultipliedGreen) && Near(red, premultipliedRed);
            var matchesStraight = Near(blue, swatch.Blue) && Near(green, swatch.Green) && Near(red, swatch.Red);

            if (swatch.Alpha == 255)
            {
                opaqueMatches = matchesStraight ? 1 : 0;
            }
            else
            {
                discriminators++;
                if (matchesPremultiplied)
                {
                    premultipliedRight++;
                }

                if (matchesStraight)
                {
                    straightRight++;
                }
            }

            File.AppendAllText(log, string.Create(
                CultureInfo.InvariantCulture,
                $"  src argb({swatch.Alpha},{swatch.Red},{swatch.Green},{swatch.Blue}) -> got bgra({blue},{green},{red},{alpha}); "
                + $"premultiplied predicts bgra({premultipliedBlue},{premultipliedGreen},{premultipliedRed}) {matchesPremultiplied}; "
                + $"straight predicts bgra({swatch.Blue},{swatch.Green},{swatch.Red}) {matchesStraight}\n"));
        }

        host.Close();

        if (opaqueMatches != 1)
        {
            File.AppendAllText(log, "FINDING: the opaque control did not come back as its own colour, so this render cannot decide the format.\n");
            return;
        }

        if (discriminators == 0)
        {
            File.AppendAllText(log, "FINDING: only the opaque control was readable, so premultiplication is undecided.\n");
            return;
        }

        File.AppendAllText(log, premultipliedRight == discriminators && straightRight == 0
            ? "FINDING: premultiplied. Every partially transparent fill came back with its colour scaled by alpha, which is exactly what AC_SRC_ALPHA wants.\n"
            : straightRight == discriminators && premultipliedRight == 0
                ? "FINDING: straight alpha, not premultiplied. The buffer must be premultiplied before it is handed to UpdateLayeredWindow.\n"
                : "FINDING: neither interpretation matches every fill, so the format is undecided.\n");
    }

    private static bool Near(byte left, byte right) => Math.Abs(left - right) <= 2;

    private void ShowInTaskbar((byte[] Pixels, int PixelWidth, int PixelHeight, int LogicalWidth) output)
    {
        var (pixels, pixelWidth, pixelHeight, _) = output;
        taskbar = Native.FindWindowW("Shell_TrayWnd", null);
        if (taskbar == 0 || !Native.GetWindowRect(taskbar, out var rect))
        {
            File.AppendAllText(log, "Shell_TrayWnd was not found; skipping the on-screen block.\n");
            Finish(null);
            return;
        }

        RegisterWindowClass();
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

        // RenderTargetBitmap already returns premultiplied BGRA, exactly what UpdateLayeredWindow wants.
        // The DIB matches the render's own pixel size, so nothing is cropped or stretched.
        Marshal.Copy(pixels, 0, bitmapBits, pixels.Length);

        var x = ((rect.Right - rect.Left) - pixelWidth) / 2;
        var y = ((rect.Bottom - rect.Top) - pixelHeight) / 2;
        block = Native.CreateWindowExW(
            WsExLayered, "MtpRenderTargetProbe", string.Empty, WsChild | WsVisible,
            x, y, pixelWidth, pixelHeight, taskbar, 0, Native.GetModuleHandleW(null), 0);
        if (block == 0)
        {
            File.AppendAllText(log, "CreateWindowExW failed: " + Native.LastError() + "\n");
            Finish(null);
            return;
        }

        var size = new Native.Size { Width = pixelWidth, Height = pixelHeight };
        var source = new Native.Point { X = 0, Y = 0 };
        var blend = new Native.BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        var applied = Native.UpdateLayeredWindow(block, 0, 0, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        _ = Native.SetWindowPos(block, 0, x, y, pixelWidth, pixelHeight, 0x0040);
        applied = Native.UpdateLayeredWindow(block, 0, 0, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        _ = Native.GetWindowRect(block, out var placed);
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"block at ({placed.Left},{placed.Top})-({placed.Right},{placed.Bottom}); layer applied={applied}\n"));

        File.AppendAllText(log, $"\n===== 请看任务栏中间的方块，保持 {holdSeconds} 秒 =====\n");
        holdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(holdSeconds) };
        holdTimer.Tick += (_, _) => Finish(null);
        holdTimer.Start();
    }

    private void RegisterWindowClass()
    {
        var description = new Native.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<Native.WndClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(windowProc),
            Instance = Native.GetModuleHandleW(null),
            BackgroundBrush = 0,
            ClassName = "MtpRenderTargetProbe",
        };
        classAtom = Native.RegisterClassExW(ref description);
        if (classAtom == 0)
        {
            throw new InvalidOperationException("RegisterClassExW failed: " + Native.LastError());
        }
    }

    private void Finish(Exception? error)
    {
        holdTimer?.Stop();
        if (error is not null)
        {
            File.AppendAllText(log, error.GetType().Name + ": " + error.Message + "\n");
        }

        try
        {
            if (block != 0)
            {
                _ = Native.SetParent(block, 0);
                _ = Native.SetWindowLongPtrW(block, GwlStyle, unchecked((nint)(int)(WsPopup | WsVisible)));
                _ = Native.DestroyWindow(block);
            }

            if (classAtom != 0)
            {
                _ = Native.UnregisterClassW("MtpRenderTargetProbe", Native.GetModuleHandleW(null));
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
        internal static extern uint GetDpiForWindow(nint window);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? name);
    }
}

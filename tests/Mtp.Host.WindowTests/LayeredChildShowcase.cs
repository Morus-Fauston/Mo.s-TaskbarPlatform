using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Puts layered child windows inside the taskbar and leaves them there, so a human can look at the
/// result instead of reading pixel numbers.
///
/// Five blocks sit side by side in the taskbar:
///   1. opaque red                  - proves a block is really there
///   2. fully transparent           - the taskbar shows through untouched
///   3. 50% red                     - the taskbar icons show through a red tint
///   4. alpha ramp 0% -> 100%       - the taskbar fades out across one block
///   5. text drawn into the layer   - a widget could paint its own content
///
/// Block 5 is the important one: a layered window replaces the window's own content, so a WinUI
/// control cannot be seen through one. Painting into the layer by hand is the way a widget would
/// have to render.
///
/// Run with <c>--show-layered</c>. Hold time defaults to 60 seconds; override with
/// <c>--hold=30</c>. This is a viewing aid, not an automated check: it always exits 0.
/// </summary>
internal sealed class LayeredChildShowcase(Application app)
{
    private const int Width = 230;
    private const int Height = 48;
    private const int Gap = 14;
    private const uint UlwAlpha = 0x00000002;
    private const uint WsChild = 0x40000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExLayered = 0x00080000;
    private const int GwlStyle = -16;
    private const int DefaultHoldSeconds = 60;

    private readonly string log = Path.Combine(AppContext.BaseDirectory, "layered-child-showcase.log");
    private readonly List<nint> blocks = [];
    private readonly int holdSeconds = ReadHoldSeconds();

    private Window? lifetime;
    private DispatcherTimer? timer;
    private nint classAtom;
    private nint memoryDc;
    private nint bitmap;
    private nint bitmapBits;
    private nint maskDc;
    private nint maskBitmap;
    private nint maskBits;
    private nint taskbar;

    private readonly Native.WndProc windowProc = static (window, message, wParam, lParam) =>
        Native.DefWindowProcW(window, message, wParam, lParam);

    public void Run()
    {
        File.WriteAllText(log, "Layered child window showcase\n");
        try
        {
            lifetime = new Window();
            taskbar = Native.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == 0 || !Native.GetWindowRect(taskbar, out var rect))
            {
                throw new InvalidOperationException("Shell_TrayWnd was not found; the showcase needs a visible taskbar.");
            }

            RegisterWindowClass();
            memoryDc = Native.CreateCompatibleDC(0);
            maskDc = Native.CreateCompatibleDC(0);
            if (!CreateLayerBitmap())
            {
                throw new InvalidOperationException("The layered bitmap could not be created.");
            }

            var boardHeight = rect.Bottom - rect.Top;
            var baseX = ((rect.Right - rect.Left) - ((Width * 5) + (Gap * 4))) / 2;
            var y = (boardHeight - Height) / 2;
            File.AppendAllText(log, $"taskbar {rect.Left},{rect.Top}-{rect.Right},{rect.Bottom}\n");

            AddBlock("1 不透明红（对照：证明方块确实存在）", baseX + ((Width + Gap) * 0), y, rect, OpaqueRed);
            AddBlock("2 全透明（任务栏完全露出）", baseX + ((Width + Gap) * 1), y, rect, FullyTransparent);
            AddBlock("3 50% 红（浅红蒙在任务栏上）", baseX + ((Width + Gap) * 2), y, rect, HalfRed);
            AddBlock("4 左透明→右不透明（任务栏渐隐）", baseX + ((Width + Gap) * 3), y, rect, AlphaRamp);
            AddBlock("5 图层里自绘文字（透明底）", baseX + ((Width + Gap) * 4), y, rect, SelfDrawnText);

            File.AppendAllText(log, $"\n===== 现在请看屏幕的任务栏，保持 {holdSeconds} 秒 =====\n");
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(holdSeconds) };
            timer.Tick += (_, _) => Finish(null);
            timer.Start();
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

    private void AddBlock(string label, int x, int y, Native.Rect taskbarRect, Action fill)
    {
        var window = Native.CreateWindowExW(
            WsExLayered, "MtpLayeredShowcase", string.Empty, WsChild | WsVisible,
            x, y, Width, Height, taskbar, 0, Native.GetModuleHandleW(null), 0);
        if (window == 0)
        {
            File.AppendAllText(log, $"{label}: CreateWindowExW failed: {Native.LastError()}\n");
            return;
        }

        fill();
        var applied = ApplyLayer(window);
        // A new child starts at the bottom of its siblings and is hidden behind the taskbar content.
        _ = Native.SetWindowPos(window, 0, x, y, Width, Height, 0x0040);
        // Re-apply after the z-order change so the layer is what the compositor keeps.
        applied = ApplyLayer(window);
        _ = Native.GetWindowRect(window, out var placed);
        File.AppendAllText(log, $"  {label}: {applied}; screen rect ({placed.Left},{placed.Top})-({placed.Right},{placed.Bottom})\n");
        blocks.Add(window);
    }

    private string ApplyLayer(nint window)
    {
        var size = new Native.Size { Width = Width, Height = Height };
        var source = new Native.Point { X = 0, Y = 0 };
        var blend = new Native.BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        return Native.UpdateLayeredWindow(window, 0, 0, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha)
            ? "layer applied"
            : "UpdateLayeredWindow failed: " + Native.LastError();
    }

    private bool CreateLayerBitmap()
    {
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
            Width = Width,
            Height = -Height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };
        bitmap = Native.CreateDIBSection(memoryDc, ref header, 0, out bitmapBits, 0, 0);
        if (bitmap == 0 || bitmapBits == 0)
        {
            return false;
        }

        _ = Native.SelectObject(memoryDc, bitmap);

        maskBitmap = Native.CreateDIBSection(maskDc, ref header, 0, out maskBits, 0, 0);
        if (maskBitmap == 0 || maskBits == 0)
        {
            return false;
        }

        _ = Native.SelectObject(maskDc, maskBitmap);
        return true;
    }

    private void RegisterWindowClass()
    {
        var description = new Native.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<Native.WndClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(windowProc),
            Instance = Native.GetModuleHandleW(null),
            BackgroundBrush = 0,
            ClassName = "MtpLayeredShowcase",
        };
        classAtom = Native.RegisterClassExW(ref description);
        if (classAtom == 0)
        {
            throw new InvalidOperationException("RegisterClassExW failed: " + Native.LastError());
        }
    }

    private void OpaqueRed() => PaintBoard(255, 255, 0, 0, 235, "MTP 不透明");

    private void FullyTransparent()
    {
        // Nothing at all: the whole block disappears and the taskbar is untouched.
        var empty = new byte[Width * Height * 4];
        Marshal.Copy(empty, 0, bitmapBits, empty.Length);
    }

    private void HalfRed() => PaintBoard(128, 255, 0, 0, 235, "MTP 半透明");

    /// <summary>Alpha climbs from 0 on the left to 255 on the right, so the taskbar fades out.</summary>
    private void AlphaRamp() => PaintBoard(-1, 255, 0, 0, 235, "MTP 渐变");

    /// <summary>
    /// Draws text into the layer with an empty background, which is how a widget would have to render
    /// its own content: a layered window replaces whatever the window itself would have drawn.
    /// </summary>
    private void SelfDrawnText() => PaintBoard(0, 255, 255, 255, 255, "MTP 自绘");

    /// <summary>
    /// Builds one layer image: a background colour with an optional white text label on top, then
    /// premultiplies. <paramref name="alpha"/> of -1 means a left-to-right alpha ramp.
    /// </summary>
    private void PaintBoard(int alpha, byte red, byte green, byte blue, int textAlpha, string text)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var index = ((y * Width) + x) * 4;
                var rowAlpha = alpha < 0 ? (byte)(x * 255 / (Width - 1)) : (byte)alpha;
                pixels[index] = blue;
                pixels[index + 1] = green;
                pixels[index + 2] = red;
                pixels[index + 3] = rowAlpha;
            }
        }

        if (textAlpha > 0)
        {
            CompositeText(pixels, text, textAlpha);
        }

        Premultiply(pixels);
        Marshal.Copy(pixels, 0, bitmapBits, pixels.Length);
    }

    /// <summary>
    /// The text is drawn into a separate black bitmap so "how white a pixel is" gives its coverage.
    /// Reading coverage straight out of the coloured layer would not work, because a red background is
    /// already fully bright in its red channel.
    /// </summary>
    private void CompositeText(byte[] pixels, string text, int textAlpha)
    {
        var blank = new byte[pixels.Length];
        Marshal.Copy(blank, 0, maskBits, blank.Length);
        _ = Native.SetBkMode(maskDc, 1); // TRANSPARENT
        _ = Native.SetTextColor(maskDc, 0x00FFFFFF);
        var font = Native.CreateFontW(21, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 0, 0, "Microsoft YaHei UI");
        var previous = Native.SelectObject(maskDc, font);
        var rect = new Native.Rect { Left = 14, Top = 10, Right = Width - 14, Bottom = Height - 6 };
        _ = Native.DrawTextW(maskDc, text, text.Length, ref rect, 0x00000001 | 0x00000020); // LEFT | VCENTER
        _ = Native.SelectObject(maskDc, previous);
        _ = Native.DeleteObject(font);

        var mask = new byte[pixels.Length];
        Marshal.Copy(maskBits, mask, 0, mask.Length);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var coverage = Math.Max(mask[index], Math.Max(mask[index + 1], mask[index + 2])) * textAlpha / 255;
            if (coverage == 0)
            {
                continue;
            }

            // White text over the background, keeping the background's own alpha.
            pixels[index] = (byte)((pixels[index] * (255 - coverage) + (255 * coverage)) / 255);
            pixels[index + 1] = (byte)((pixels[index + 1] * (255 - coverage) + (255 * coverage)) / 255);
            pixels[index + 2] = (byte)((pixels[index + 2] * (255 - coverage) + (255 * coverage)) / 255);
            pixels[index + 3] = Math.Max(pixels[index + 3], (byte)coverage);
        }
    }

    private static void Premultiply(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            pixels[index] = (byte)(pixels[index] * alpha / 255);
            pixels[index + 1] = (byte)(pixels[index + 1] * alpha / 255);
            pixels[index + 2] = (byte)(pixels[index + 2] * alpha / 255);
        }
    }

    private void Finish(Exception? error)
    {
        timer?.Stop();
        if (error is not null)
        {
            File.AppendAllText(log, error + "\n");
        }

        try
        {
            foreach (var block in blocks)
            {
                _ = Native.SetParent(block, 0);
                _ = Native.SetWindowLongPtrW(block, GwlStyle, unchecked((nint)(int)(WsPopup | WsVisible)));
                _ = Native.DestroyWindow(block);
            }

            if (classAtom != 0)
            {
                _ = Native.UnregisterClassW("MtpLayeredShowcase", Native.GetModuleHandleW(null));
            }

            if (maskBitmap != 0)
            {
                _ = Native.DeleteObject(maskBitmap);
            }

            if (maskDc != 0)
            {
                _ = Native.DeleteDC(maskDc);
            }

            if (bitmap != 0)
            {
                _ = Native.DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                _ = Native.DeleteDC(memoryDc);
            }

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

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int SetBkMode(nint dc, int mode);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern uint SetTextColor(nint dc, uint color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateFontW(
            int height, int width, int escapement, int orientation, int weight,
            uint italic, uint underline, uint strikeOut, uint charSet,
            uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int DrawTextW(nint dc, string text, int length, ref Rect rect, uint format);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? name);
    }
}

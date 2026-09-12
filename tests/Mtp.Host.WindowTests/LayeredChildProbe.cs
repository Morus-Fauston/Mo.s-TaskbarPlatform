using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Tests the mechanism that FluentFlyout uses, on an embedded taskbar child window.
///
/// FluentFlyout embeds a WS_CHILD window into Shell_TrayWnd and its window is still transparent. Its
/// window is created by WPF with AllowsTransparency="True", which makes WPF turn the HWND into a
/// LAYERED window and feed it per-pixel alpha through UpdateLayeredWindow. Its embedded path never
/// calls DwmExtendFrameIntoClientArea. That is a different mechanism from the one MTP uses, so the
/// earlier "child windows cannot be transparent" conclusion does not cover it.
///
/// Each case paints a colour that can only have come from the layer, so "the window is gone" and
/// "the layer works" can be told apart. Without that control a fully transparent result would be
/// indistinguishable from a window that was never created.
///
/// Run with <c>--layered-child</c>. Needs an interactive desktop with a visible taskbar.
/// </summary>
internal sealed class LayeredChildProbe(Application app)
{
    private const int Width = 300;
    private const int Height = 48;
    private const int ChannelTolerance = 6;

    private const uint WsChild = 0x40000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint WsExLayered = 0x00080000;
    private const uint UlwAlpha = 0x00000002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const nint HwndTopmost = -1;
    private const uint GwHwndPrev = 3;
    private const int SwHide = 0;

    private readonly string log = Path.Combine(AppContext.BaseDirectory, "layered-child.log");
    private readonly List<Case> cases = [];
    private readonly List<string> findings = [];

    private Window? lifetime;
    private DispatcherTimer? timer;
    private nint taskbar;
    private int caseIndex;
    private int stage;
    private uint[] baseline = [];
    private uint[] gridBaseline = [];
    private Native.Rect probeScreenRect;
    private (int X, int Y) probeRelative;

    private nint classAtom;
    private nint rawWindow;
    private nint rawMemoryDc;
    private nint rawBitmap;
    private nint rawBitmapBits;
    private ExplorerTaskbarProbeWindow? winuiWindow;

    private readonly Native.WndProc windowProc = static (window, message, wParam, lParam) =>
        Native.DefWindowProcW(window, message, wParam, lParam);

    /// <summary>
    /// <paramref name="Apply"/> returns a one-line description of what it just did. When
    /// <paramref name="SecondStep"/> is set, the case is sampled after <paramref name="Apply"/> (that
    /// reading is the precondition the second step depends on) and sampled again afterwards.
    /// </summary>
    private sealed record Case(
        string Name,
        Func<string> Apply,
        bool SampleWholeWindow = false,
        bool TransparentLayer = false,
        Func<string>? SecondStep = null);

    public void Run()
    {
        File.WriteAllText(log, "Layered child window probe (FluentFlyout mechanism)\n");
        try
        {
            lifetime = new Window();
            _ = typeof(Win32ExplorerTaskbarEmbedAdapter)
                .GetMethod("EnsureSystemDispatcherQueue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [null]);

            taskbar = Native.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == 0)
            {
                throw new InvalidOperationException("Shell_TrayWnd was not found; this probe needs a visible taskbar.");
            }

            if (!Native.GetWindowRect(taskbar, out var rect))
            {
                throw new InvalidOperationException("The taskbar rectangle could not be read.");
            }

            RegisterWindowClass();
            rawMemoryDc = Native.CreateCompatibleDC(0);
            if (!CreateLayerBitmap())
            {
                throw new InvalidOperationException("The layered bitmap could not be created.");
            }

            var spot = TaskbarSpot();
            File.AppendAllText(log, $"taskbar {rect.Left},{rect.Top}-{rect.Right},{rect.Bottom}; probe at screen {probeScreenRect.Left},{probeScreenRect.Top} (parent-relative {spot.X},{spot.Y}) {Width}x{Height}\n");

            cases.Add(new Case("A. raw child window, nothing painted", () => ShowRaw(0, "created, no painting")));
            cases.Add(new Case("B. raw child + WS_EX_LAYERED + opaque red layer", () =>
                ShowRaw(WsExLayered, FillLayer(255, 255, 0, 0, opaque: true))));
            cases.Add(new Case("C. raw child + WS_EX_LAYERED + fully transparent layer", () =>
                ShowRaw(WsExLayered, FillLayer(0, 0, 0, 0, opaque: false))));
            cases.Add(new Case("D. raw child + WS_EX_LAYERED + 50% red layer", () =>
                ShowRaw(WsExLayered, FillLayer(128, 128, 0, 0, opaque: false))));
            cases.Add(new Case("E. raw child + region shrunk to nothing", () => ShowRawWithEmptyRegion()));
            cases.Add(new Case("F. WinUI child + WS_EX_LAYERED + transparent layer", () => ShowWinUiLayered(FillLayer(0, 0, 0, 0, opaque: false), alphaApi: false), SampleWholeWindow: true, TransparentLayer: true));
            cases.Add(new Case("G. WinUI child + WS_EX_LAYERED + opaque red layer (control for F)", () => ShowWinUiLayered(FillLayer(255, 255, 0, 0, opaque: true), alphaApi: false), SampleWholeWindow: true));
            cases.Add(new Case("H. WinUI child + SetLayeredWindowAttributes alpha 0", () => ShowWinUiLayered(string.Empty, alphaApi: true), SampleWholeWindow: true, TransparentLayer: true));
            // SYS-004 S1: separate "the layer never survived the reparent" from "the layer survived and
            // nothing re-submitted it". Both cases paint opaque red, so the taskbar showing through can
            // only mean the layer really is not on screen any more.
            cases.Add(new Case(
                "I. transparent while top level, then reparented with no resubmit",
                ShowTopLevelLayered,
                SampleWholeWindow: true,
                SecondStep: () => ReparentIntoTaskbar(repair: false)));
            cases.Add(new Case(
                "J. same, but styles reset + SWP_FRAMECHANGED + layer resubmitted after reparent",
                ShowTopLevelLayered,
                SampleWholeWindow: true,
                SecondStep: () => ReparentIntoTaskbar(repair: true)));
            // The case above differs from F/G in one more way than the repair sequence: this window was
            // already shown and composited as a top-level layered window. F/G's window was still hidden
            // when it became a child. K hides first so that variable is isolated.
            cases.Add(new Case(
                "K. same as J, but hidden before the reparent",
                ShowTopLevelLayered,
                SampleWholeWindow: true,
                SecondStep: () => ReparentIntoTaskbar(repair: true, hideFirst: true)));
            // I/J/K all submitted the layer while the window was still top level, and all created it
            // with WS_EX_LAYERED. L keeps the style and drops only the early submit, so the two are told
            // apart. This also matches what production would do: embed first, submit afterwards.
            cases.Add(new Case(
                "L. layered from creation but never submitted until after the reparent",
                ShowTopLevelLayeredLate,
                SampleWholeWindow: true,
                SecondStep: () => ReparentIntoTaskbar(repair: true)));

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            timer.Tick += (_, _) => Advance();
            timer.Start();
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    private void RegisterWindowClass()
    {
        var instance = Native.GetModuleHandleW(null);
        var description = new Native.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<Native.WndClassEx>(),
            Style = 0,
            WindowProc = Marshal.GetFunctionPointerForDelegate(windowProc),
            Instance = instance,
            BackgroundBrush = 0, // No class brush: nothing erases the child's surface.
            ClassName = "MtpLayeredChildProbe",
        };
        classAtom = Native.RegisterClassExW(ref description);
        if (classAtom == 0)
        {
            throw new InvalidOperationException("RegisterClassExW failed: " + Native.LastError());
        }
    }

    private bool CreateLayerBitmap()
    {
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
            Width = Width,
            Height = -Height, // Negative: top-down rows.
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };
        rawBitmap = Native.CreateDIBSection(rawMemoryDc, ref header, 0, out rawBitmapBits, 0, 0);
        if (rawBitmap == 0 || rawBitmapBits == 0)
        {
            return false;
        }

        _ = Native.SelectObject(rawMemoryDc, rawBitmap);
        return true;
    }

    /// <summary>Premultiplied BGRA, which is what UpdateLayeredWindow expects with AC_SRC_ALPHA.</summary>
    private string FillLayer(byte alpha, byte red, byte green, byte blue, bool opaque)
    {
        var count = Width * Height;
        var pixels = new byte[count * 4];
        for (var index = 0; index < count; index++)
        {
            pixels[index * 4] = Premultiply(blue, alpha);
            pixels[(index * 4) + 1] = Premultiply(green, alpha);
            pixels[(index * 4) + 2] = Premultiply(red, alpha);
            pixels[(index * 4) + 3] = alpha;
        }

        Marshal.Copy(pixels, 0, rawBitmapBits, pixels.Length);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"layer filled{(opaque ? " (opaque)" : string.Empty)} argb({alpha},{red},{green},{blue})");
    }

    private static byte Premultiply(byte channel, byte alpha) => (byte)(channel * alpha / 255);

    /// <summary>
    /// Parent-of-a-child coordinates are relative to the parent's client area, not the screen. Using
    /// screen coordinates here put the probe below the display and every reading was meaningless.
    /// </summary>
    private (int X, int Y) TaskbarSpot()
    {
        var rect = ReadTaskbarRect();
        var x = (rect.Right - rect.Left) * 3 / 4 - (Width / 2);
        var y = ((rect.Bottom - rect.Top) - Height) / 2;
        probeRelative = (x, y);
        probeScreenRect = new Native.Rect { Left = rect.Left + x, Top = rect.Top + y, Right = rect.Left + x + Width, Bottom = rect.Top + y + Height };
        return probeRelative;
    }

    private string ShowRaw(uint exStyle, string description)
    {
        var spot = TaskbarSpot();
        rawWindow = Native.CreateWindowExW(
            exStyle, "MtpLayeredChildProbe", string.Empty,
            WsChild | WsVisible, spot.X, spot.Y, Width, Height, taskbar, 0, Native.GetModuleHandleW(null), 0);
        if (rawWindow == 0)
        {
            return "CreateWindowExW failed: " + Native.LastError();
        }

        // A freshly created child sits at the bottom of its siblings' z-order, which is behind the
        // taskbar's own content window. Without HWND_TOP the probe is simply never visible and every
        // reading is meaningless. Case B (opaque red) is the control that catches exactly that.
        _ = Native.SetWindowPos(rawWindow, 0, spot.X, spot.Y, Width, Height,
            SwpNoActivate | SwpShowWindow);

        // A layered child window is positioned with SetWindowPos; coordinates are parent-relative.
        var applied = ApplyLayer(rawWindow);
        return $"{description}; {applied}; {Describe(rawWindow)}";
    }

    private static string Describe(nint window)
    {
        var visible = Native.IsWindowVisible(window);
        _ = Native.GetWindowRect(window, out var rect);
        var parent = Native.GetAncestor(window, 1);
        var style = (uint)(long)Native.GetWindowLongPtrW(window, GwlStyle);
        var exStyle = (uint)(long)Native.GetWindowLongPtrW(window, GwlExStyle);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"visible={visible} rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) parent=0x{parent:X} style=0x{style:X8} exstyle=0x{exStyle:X8} siblings-above={SiblingsAbove(window)}");
    }

    private string ApplyLayer(nint window)
    {
        var size = new Native.Size { Width = Width, Height = Height };
        var source = new Native.Point { X = 0, Y = 0 };
        var blend = new Native.BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        // A null destination keeps the current position. Passing a point here silently moves the window.
        var ok = Native.UpdateLayeredWindow(window, 0, 0, ref size, rawMemoryDc, ref source, 0, ref blend, UlwAlpha);
        return ok ? "UpdateLayeredWindow ok" : "UpdateLayeredWindow failed: " + Native.LastError();
    }

    /// <summary>
    /// SYS-004 S1, first half: become transparent while still a top-level window. The window is made
    /// topmost on purpose -- the taskbar is topmost itself, so a plain popup would sit behind it and
    /// every later reading would say "the taskbar shows through" no matter what the layer does.
    /// </summary>
    private string ShowTopLevelLayered()
    {
        _ = TaskbarSpot();
        rawWindow = Native.CreateWindowExW(
            WsExLayered, "MtpLayeredChildProbe", string.Empty,
            WsPopup | WsVisible, probeScreenRect.Left, probeScreenRect.Top, Width, Height, 0, 0,
            Native.GetModuleHandleW(null), 0);
        if (rawWindow == 0)
        {
            return "CreateWindowExW failed: " + Native.LastError();
        }

        _ = Native.SetWindowPos(rawWindow, HwndTopmost, probeScreenRect.Left, probeScreenRect.Top, Width, Height,
            SwpNoActivate | SwpShowWindow);

        var applied = ApplyLayer(rawWindow);
        return $"top level, WS_EX_LAYERED, opaque red layer; {applied}; {Describe(rawWindow)}";
    }

    /// <summary>
    /// SYS-004 S1 variant: same window as the top-level cases, but with the layer never submitted
    /// while it is top level. A layered window draws nothing before its first submit, so this case has
    /// no internal precondition; its control is that the identical reparent step paints red in case B.
    /// </summary>
    private string ShowTopLevelLayeredLate()
    {
        _ = TaskbarSpot();
        rawWindow = Native.CreateWindowExW(
            WsExLayered, "MtpLayeredChildProbe", string.Empty,
            WsPopup | WsVisible, probeScreenRect.Left, probeScreenRect.Top, Width, Height, 0, 0,
            Native.GetModuleHandleW(null), 0);
        if (rawWindow == 0)
        {
            return "CreateWindowExW failed: " + Native.LastError();
        }

        _ = Native.SetWindowPos(rawWindow, HwndTopmost, probeScreenRect.Left, probeScreenRect.Top, Width, Height,
            SwpNoActivate | SwpShowWindow);

        return $"top level, WS_EX_LAYERED, shown, layer deliberately not submitted; {Describe(rawWindow)}";
    }

    /// <summary>
    /// SYS-004 S1, second half. <paramref name="repair"/> false is timing alpha from hard fact 9
    /// (reposition only). True is the S1 sequence: rewrite the styles a reparent can invalidate,
    /// refresh the frame with parent-client coordinates, then submit the layer again.
    /// </summary>
    private string ReparentIntoTaskbar(bool repair, bool hideFirst = false)
    {
        var spot = TaskbarSpot();

        if (hideFirst)
        {
            // F/G's window was still hidden when it became a child; this one was not. Hiding first
            // leaves that as the only difference between the two paths.
            _ = Native.ShowWindow(rawWindow, SwHide);
        }

        // SetParent does not touch WS_CHILD/WS_POPUP; the caller has to switch the style itself.
        // WS_EX_TOPMOST is dropped at the same time: it is a top-level bit and has no meaning on a
        // child, and leaving bits behind is exactly how a measurement stops describing production.
        Native.SetStyle(rawWindow, WsChild);
        var exStyle = (uint)(long)Native.GetWindowLongPtrW(rawWindow, GwlExStyle);
        exStyle &= ~Native.WsExTopmost;
        exStyle |= WsExLayered;
        _ = Native.SetWindowLongPtrW(rawWindow, GwlExStyle, unchecked((nint)(int)exStyle));
        var previous = Native.SetParent(rawWindow, taskbar);

        // HWND_TOP must be allowed to take effect. Passing SWP_NOZORDER here cancels it, and a
        // reparented child sits at the bottom of the taskbar's children -- behind the taskbar's own
        // content window. That reads as "the layer is gone" while the layer is fine and merely
        // covered. Case B is the control that this call is what makes a raw child visible at all.
        RaiseChild(spot, frameChanged: repair);

        if (!repair)
        {
            return $"reparented (previous parent 0x{previous:X}); no style refresh, no layer resubmit; {Describe(rawWindow)}";
        }

        var resubmitted = ApplyLayer(rawWindow);
        RaiseChild(spot, frameChanged: false);
        return $"reparented (previous parent 0x{previous:X}); styles reset, SWP_FRAMECHANGED, resubmit -> {resubmitted}; {Describe(rawWindow)}";
    }

    private void RaiseChild((int X, int Y) spot, bool frameChanged) =>
        _ = Native.SetWindowPos(
            rawWindow, 0, spot.X, spot.Y, Width, Height,
            SwpNoActivate | SwpShowWindow | (frameChanged ? SwpFrameChanged : 0));

    /// <summary>
    /// How many siblings sit above this window in z-order. Zero means nothing can cover it, so a
    /// reading of "the taskbar shows through" really is about the layer and not about being buried.
    /// </summary>
    private static int SiblingsAbove(nint window)
    {
        var count = 0;
        var current = Native.GetWindow(window, GwHwndPrev);
        while (current != 0 && count < 64)
        {
            count++;
            current = Native.GetWindow(current, GwHwndPrev);
        }

        return count;
    }

    private string ShowRawWithEmptyRegion()
    {
        var description = ShowRaw(0, "created");
        var region = Native.CreateRectRgn(0, 0, 0, 0); // An empty region removes the whole window.
        var set = Native.SetWindowRgn(rawWindow, region, true) != 0;
        return $"{description}; empty region set={set}";
    }

    private string ShowWinUiLayered(string layerDescription, bool alphaApi)
    {
        var spot = TaskbarSpot();
        winuiWindow = new ExplorerTaskbarProbeWindow(HostComponentDisplayModel.From(
            new Component(new StableIdentity(new StableId("layered-child")), CapabilityState.Available)));
        winuiWindow.PrepareHidden(new SizeInt32(Width, Height), useToolWindowPresenter: false);
        var handle = winuiWindow.WindowHandle;

        // Style order matching FluentFlyout: the layer exists before the window becomes a child.
        Native.SetStyle(handle, WsChild);
        var exStyle = (uint)(long)Native.GetWindowLongPtrW(handle, GwlExStyle);
        _ = Native.SetWindowLongPtrW(handle, GwlExStyle, unchecked((nint)(int)(exStyle | WsExLayered)));
        _ = Native.SetParent(handle, taskbar);
        _ = Native.SetWindowPos(handle, 0, spot.X, spot.Y, Width, Height,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow | SwpFrameChanged);

        string applied;
        if (alphaApi)
        {
            var ok = Native.SetLayeredWindowAttributes(handle, 0, 0, 0x00000002);
            applied = ok ? "SetLayeredWindowAttributes(alpha 0) ok" : "SetLayeredWindowAttributes failed: " + Native.LastError();
        }
        else
        {
            applied = ApplyLayer(handle);
        }

        return $"WinUI window made a layered child; {layerDescription}; {applied}; {Describe(handle)}";
    }

    private Native.Rect ReadTaskbarRect() =>
        Native.GetWindowRect(taskbar, out var rect) ? rect : default;

    private void Advance()
    {
        timer!.Stop();
        try
        {
            var entry = cases[caseIndex];
            if (stage == 0)
            {
                CloseAll();
                baseline = Sample();
                gridBaseline = SampleGrid();
                File.AppendAllText(log, $"\n--- {entry.Name} ---\n  taskbar underneath: {Format(baseline)}\n");
                stage = 1;
                timer.Start();
                return;
            }

            var second = stage > 1;
            var detail = second ? entry.SecondStep!() : entry.Apply();
            Native.DwmFlush();
            File.AppendAllText(log, $"  {(second ? "after repair:       " : string.Empty)}{detail}\n");

            var samples = Sample();
            var matches = CountMatches(samples, baseline);
            File.AppendAllText(log, $"  on screen:          {Format(samples)}  ({matches}/{samples.Length} match the taskbar)\n");

            var gridMatches = -1;
            var gridTotal = 0;

            // Edges only, or the whole window: the edges miss the vertically centred component text, so
            // they answer "did the surface go transparent" but not "did the widget content survive".
            // Deviating from the taskbar only means "the layer put something there"; it says nothing
            // about content, so the content verdict is limited to the transparent-layer cases where the
            // entire window matching the taskbar proves the window itself draws nothing.
            if (entry.SampleWholeWindow)
            {
                var grid = SampleGrid();
                gridMatches = CountMatches(grid, GridBaseline());
                gridTotal = grid.Length;
                File.AppendAllText(log, $"  full-window grid:   {Format(grid)}  ({gridMatches}/{grid.Length} match the taskbar)\n");
                if (gridMatches < grid.Length)
                {
                    File.AppendAllText(log, "  the layer covers that much of the window; this says nothing about widget content\n");
                }
                else if (entry.TransparentLayer)
                {
                    File.AppendAllText(log, "  the window draws nothing of its own: the layer replaced its whole content\n");
                }
            }

            if (second || entry.SecondStep is null)
            {
                // The taskbar keeps repainting its own pixels (the clock, button hover, the widget
                // strip), so a strict "every sample" test flips at random. Classify by how much of the
                // probe rectangle still looks like the taskbar instead, and use the whole-window grid
                // when the case collects one.
                var (matched, total) = gridMatches >= 0 ? (gridMatches, gridTotal) : (matches, samples.Length);
                var verdict = matched >= total * 3 / 4
                    ? "taskbar shows through"
                    : matched <= total / 4
                        ? "surface covers the taskbar"
                        : $"inconclusive ({matched}/{total} match the taskbar)";
                var label = second ? " [after reparent]" : string.Empty;
                findings.Add(entry.Name + label + " -> " + verdict);
            }

            if (!second && entry.SecondStep is not null)
            {
                stage = 2;
                timer.Start();
                return;
            }

            NextCase();
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    private void NextCase()
    {
        CloseAll();
        caseIndex++;
        stage = 0;
        if (caseIndex >= cases.Count)
        {
            File.AppendAllText(log, "\n=== summary (screen pixels only; not human acceptance) ===\n");
            foreach (var finding in findings)
            {
                File.AppendAllText(log, "  " + finding + "\n");
            }

            Finish(null);
            return;
        }

        timer!.Start();
    }

    private void CloseAll()
    {
        if (rawWindow != 0)
        {
            _ = Native.SetParent(rawWindow, 0);
            Native.SetStyle(rawWindow, WsPopup);
            _ = Native.DestroyWindow(rawWindow);
            rawWindow = 0;
        }

        if (winuiWindow is not null)
        {
            winuiWindow.ClearMaterial();
            _ = Native.SetParent(winuiWindow.WindowHandle, 0);
            winuiWindow.Close();
            winuiWindow = null;
        }

        Native.DwmFlush();
    }

    private uint[] Sample()
    {
        var rect = probeScreenRect;
        var screen = Native.GetDC(0);
        try
        {
            var values = new List<uint>();
            foreach (var y in new[] { rect.Top + 5, rect.Bottom - 5 })
            {
                foreach (var x in new[] { rect.Left + 20, rect.Left + (Width / 2), rect.Right - 20 })
                {
                    values.Add(Native.GetPixel(screen, x, y));
                }
            }

            return [.. values];
        }
        finally
        {
            Native.ReleaseDC(0, screen);
        }
    }

    private uint[] GridBaseline() => gridBaseline;

    /// <summary>A 5x5 grid across the whole probe rectangle, centre row included.</summary>
    private uint[] SampleGrid()
    {
        var rect = probeScreenRect;
        var screen = Native.GetDC(0);
        try
        {
            var values = new List<uint>();
            for (var row = 0; row < 5; row++)
            {
                for (var column = 0; column < 5; column++)
                {
                    var x = rect.Left + (Width * column / 4);
                    var y = rect.Top + (Height * row / 4);
                    values.Add(Native.GetPixel(screen, Math.Min(x, rect.Right - 1), Math.Min(y, rect.Bottom - 1)));
                }
            }

            return [.. values];
        }
        finally
        {
            Native.ReleaseDC(0, screen);
        }
    }

    private static int CountMatches(uint[] samples, uint[] reference)
    {
        var matches = 0;
        for (var index = 0; index < samples.Length && index < reference.Length; index++)
        {
            if (Within(samples[index], reference[index]))
            {
                matches++;
            }
        }

        return matches;
    }

    private static bool Within(uint left, uint right) =>
        left != 0xFFFFFFFF && right != 0xFFFFFFFF
        && Math.Abs((int)(left & 255) - (int)(right & 255)) <= ChannelTolerance
        && Math.Abs((int)(left >> 8 & 255) - (int)(right >> 8 & 255)) <= ChannelTolerance
        && Math.Abs((int)(left >> 16 & 255) - (int)(right >> 16 & 255)) <= ChannelTolerance;

    private static string Format(uint[] values) => string.Join(", ", values.Select(value => value == 0xFFFFFFFF
        ? "CLR_INVALID"
        : string.Create(CultureInfo.InvariantCulture, $"0x{value & 0xFFFFFF:X6}")));

    private void Finish(Exception? error)
    {
        timer?.Stop();
        if (error is not null)
        {
            File.AppendAllText(log, error + "\n");
        }

        try
        {
            CloseAll();
            if (classAtom != 0)
            {
                _ = Native.UnregisterClassW("MtpLayeredChildProbe", Native.GetModuleHandleW(null));
            }

            if (rawBitmap != 0)
            {
                _ = Native.DeleteObject(rawBitmap);
            }

            if (rawMemoryDc != 0)
            {
                _ = Native.DeleteDC(rawMemoryDc);
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

        internal const uint WsExTopmost = 0x00000008;

        internal static void SetStyle(nint window, uint style) =>
            _ = SetWindowLongPtrW(window, GwlStyle, unchecked((nint)(int)style));

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll")]
        internal static extern nint GetAncestor(nint window, uint flags);

        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint window, uint command);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(nint window, int command);

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
        internal static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UpdateLayeredWindow(
            nint window, nint destinationDc, nint destination, ref Size size,
            nint sourceDc, ref Point source, uint colorKey, ref BlendFunction blend, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetParent(nint window, nint parent);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint GetWindowLongPtrW(nint window, int index);

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
        internal static extern nint GetDC(nint window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(nint window, nint dc);

        [DllImport("gdi32.dll")]
        internal static extern uint GetPixel(nint dc, int x, int y);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int SetWindowRgn(nint window, nint region, bool redraw);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? name);
    }
}

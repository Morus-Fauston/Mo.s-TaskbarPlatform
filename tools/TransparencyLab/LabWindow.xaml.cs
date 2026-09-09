using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;

namespace TransparencyLab;

/// <summary>
/// Single-purpose lab: a top-level WinUI 3 window configured in the exact order DeskBox uses
/// (presenter -> WS_EX_TOOLWINDOW -> strip frame styles + SWP_FRAMECHANGED -> IsShownInSwitchers=false
/// -> move -> DWM border/corner -> system dispatcher queue -> DwmExtendFrameIntoClientArea(-1) -> backdrop
/// -> DWMSBT_NONE). Buttons switch the material so each variable can be observed on its own.
/// The window is deliberately not owned by any other window and never reparented.
/// </summary>
public sealed partial class LabWindow : Window
{
    private static nint dispatcherQueueController;
    private nint hwnd;
    private BrushBackdrop? brushBackdrop;
    private DesktopAcrylicController? acrylic;
    private MicaController? mica;
    private SystemBackdropConfiguration? configuration;

    public LabWindow()
    {
        InitializeComponent();
    }

    public void Configure()
    {
        hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var log = new StringBuilder();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Reverted to the configuration that was transparent before: keeping WS_THICKFRAME / dropping
        // WS_EX_TOOLWINDOW made the brush opaque on both screens and brought the white edge back.
        var exStyle = Native.GetWindowLongPtrW(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtrW(hwnd, Native.GWL_EXSTYLE, exStyle | Native.WS_EX_TOOLWINDOW);

        var style = Native.GetWindowLongPtrW(hwnd, Native.GWL_STYLE);
        style &= ~(Native.WS_CAPTION | Native.WS_BORDER | Native.WS_DLGFRAME | Native.WS_THICKFRAME);
        Native.SetWindowLongPtrW(hwnd, Native.GWL_STYLE, style);
        Native.SetWindowPos(hwnd, 0, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = false;
        AppWindow.MoveAndResize(new RectInt32(200, 200, 980, 340));

        uint border = Native.DWMWA_COLOR_NONE;
        log.Append("border=0x").AppendLine(Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, 4).ToString("X8"));
        uint corner = Native.DWMWCP_ROUND;
        log.Append("corner=0x").AppendLine(Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4).ToString("X8"));

        log.Append("dispatcherQueue=").AppendLine(EnsureSystemDispatcherQueue());
        log.Append("frame=0x").AppendLine(ExtendFrame().ToString("X8"));

        ApplyBrush();
        log.AppendLine("initial material: transparent brush");
        InfoText.Text = log.ToString();
        RootGrid.Loaded += (_, _) => { ExtendFrame(); Refresh(); };

        // The frame is stripped, so the window has no caption to drag; drag anywhere on the root grid instead.
        RootGrid.PointerPressed += (s, e) =>
        {
            if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
            Native.GetCursorPos(out dragCursorStart);
            dragWindowStart = AppWindow.Position;
            dragging = RootGrid.CapturePointer(e.Pointer);
        };
        RootGrid.PointerMoved += (s, e) =>
        {
            if (!dragging) return;
            Native.GetCursorPos(out var cursor);
            AppWindow.Move(new PointInt32(dragWindowStart.X + cursor.X - dragCursorStart.X, dragWindowStart.Y + cursor.Y - dragCursorStart.Y));
        };
        RootGrid.PointerReleased += (s, e) => { dragging = false; RootGrid.ReleasePointerCaptures(); Refresh(); };
        RootGrid.PointerCaptureLost += (s, e) => dragging = false;
    }

    private bool dragging;
    private Native.POINT dragCursorStart;
    private PointInt32 dragWindowStart;

    private void MoveToNextDisplay()
    {
        var areas = DisplayArea.FindAll();
        if (areas.Count == 0) return;
        var current = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var index = 0;
        for (var i = 0; i < areas.Count; i++)
        {
            if (areas[i].DisplayId.Value == current.DisplayId.Value) { index = (i + 1) % areas.Count; break; }
        }

        var bounds = areas[index].OuterBounds;
        AppWindow.Move(new PointInt32(bounds.X + 200, bounds.Y + 200));
    }

    private nint originalOwner;
    private bool desktopLayerAttached;

    /// <summary>
    /// Mirrors DeskBox WidgetLayerService.TryAttachToDesktopIconLayer: owner = existing SHELLDLL_DefView,
    /// then HWND_BOTTOM. Toggling lets the same window be compared in both Z-order positions on one screen.
    /// </summary>
    private string ToggleDesktopLayer()
    {
        if (desktopLayerAttached)
        {
            Native.SetWindowLongPtrW(hwnd, Native.GWLP_HWNDPARENT, originalOwner);
            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Native.SetWindowPos(hwnd, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            desktopLayerAttached = false;
            return "detached";
        }

        nint defView = 0;
        Native.EnumWindows((h, _) =>
        {
            defView = Native.FindWindowExW(h, 0, "SHELLDLL_DefView", null);
            return defView == 0;
        }, 0);
        if (defView == 0) return "SHELLDLL_DefView not found";

        originalOwner = Native.GetWindowLongPtrW(hwnd, Native.GWLP_HWNDPARENT);
        Native.SetWindowLongPtrW(hwnd, Native.GWLP_HWNDPARENT, defView);
        var actual = Native.GetWindowLongPtrW(hwnd, Native.GWLP_HWNDPARENT);
        if (actual != defView) return $"owner attach failed actual=0x{actual:X} err={Marshal.GetLastWin32Error()}";
        Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        desktopLayerAttached = true;
        return $"attached defView=0x{defView:X}";
    }

    private string OwnerInfo()
    {
        var owner = Native.GetWindowLongPtrW(hwnd, Native.GWLP_HWNDPARENT);
        var cls = new StringBuilder(256);
        if (owner != 0) Native.GetClassNameW(owner, cls, 256);
        return $"owner=0x{owner:X}({(owner == 0 ? "-" : cls.ToString())})";
    }

    private int ExtendFrame()
    {
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        return Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private void SetBackdropTypeNone()
    {
        uint none = Native.DWMSBT_NONE;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref none, 4);
    }

    private void ClearMaterials()
    {
        SystemBackdrop = null;
        brushBackdrop = null;
        acrylic?.Dispose();
        acrylic = null;
        mica?.Dispose();
        mica = null;
    }

    private void ApplyBrush()
    {
        ClearMaterials();
        // DeskBox solid mode: alpha = opacity*255, never 0. 0x60 keeps the desktop visible while the tint is still obvious.
        brushBackdrop = new BrushBackdrop(Windows.UI.Color.FromArgb(0x60, 0x20, 0x20, 0x20));
        SystemBackdrop = brushBackdrop;
        SetBackdropTypeNone();
    }

    private SystemBackdropConfiguration Config() => configuration ??= new SystemBackdropConfiguration
    {
        IsInputActive = true,
        Theme = RootGrid.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
    };

    private void ApplyAcrylic()
    {
        ClearMaterials();
        acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin, TintOpacity = 0f, LuminosityOpacity = 0.1f };
        acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        acrylic.SetSystemBackdropConfiguration(Config());
        SetBackdropTypeNone();
    }

    private void ApplyMica()
    {
        ClearMaterials();
        mica = new MicaController { Kind = MicaKind.BaseAlt };
        mica.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        mica.SetSystemBackdropConfiguration(Config());
        SetBackdropTypeNone();
    }

    private void Refresh()
    {
        var monitor = Native.MonitorFromWindow(hwnd, 2);
        var info = new Native.MONITORINFOEXW { cbSize = Marshal.SizeOf<Native.MONITORINFOEXW>() };
        Native.GetMonitorInfoW(monitor, ref info);
        string color;
        try
        {
            color = Microsoft.Graphics.Display.DisplayInformation.CreateForWindowId(AppWindow.Id).GetAdvancedColorInfo().CurrentAdvancedColorKind.ToString();
        }
        catch (Exception e) { color = e.GetType().Name; }

        var material = brushBackdrop is not null ? "brush" : acrylic is not null ? "acrylic" : mica is not null ? "mica" : "none";
        InfoText.Text =
            $"material={material}\nstyle=0x{(uint)(long)Native.GetWindowLongPtrW(hwnd, Native.GWL_STYLE):X8} exstyle=0x{(uint)(long)Native.GetWindowLongPtrW(hwnd, Native.GWL_EXSTYLE):X8}\n" +
            $"monitor={info.szDevice} primary={(info.dwFlags & 1) != 0} dpi={Native.GetDpiForWindow(hwnd)} color={color}\n" +
            $"frame=0x{ExtendFrame():X8} {OwnerInfo()} desktopLayer={desktopLayerAttached} {lastLayerResult}\n" +
            "观察：窗口背景是否透出桌面/其他窗口；边缘是否有白边。按住窗口空白处可拖动；「移到下一屏」在显示器间切换；「钉到桌面层」复刻 DeskBox 的 owner=SHELLDLL_DefView + HWND_BOTTOM（钉住后窗口在最底层，需 Win+D 或最小化其他窗口才能看到）。";
    }

    private void Brush_Click(object s, RoutedEventArgs e) { ApplyBrush(); Refresh(); }
    private void Acrylic_Click(object s, RoutedEventArgs e) { ApplyAcrylic(); Refresh(); }
    private void Mica_Click(object s, RoutedEventArgs e) { ApplyMica(); Refresh(); }
    private void None_Click(object s, RoutedEventArgs e) { ClearMaterials(); Refresh(); }
    private void Refresh_Click(object s, RoutedEventArgs e) => Refresh();
    private void NextDisplay_Click(object s, RoutedEventArgs e) { MoveToNextDisplay(); Refresh(); }
    private string lastLayerResult = "";
    private void DesktopLayer_Click(object s, RoutedEventArgs e) { lastLayerResult = ToggleDesktopLayer(); Refresh(); }
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private static string EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return "existing";
        var options = new Native.DispatcherQueueOptions { dwSize = Marshal.SizeOf<Native.DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
        var hr = Native.CreateDispatcherQueueController(options, out dispatcherQueueController);
        return hr < 0 ? $"failed 0x{hr:X8}" : "created";
    }

    /// <summary>Same mechanism as WinUIEx TransparentTintBackdrop / the MTP probe brush.</summary>
    private sealed partial class BrushBackdrop(Windows.UI.Color tint) : SystemBackdrop
    {
        private readonly Windows.UI.Composition.Compositor compositor = new();
        private Windows.UI.Composition.CompositionColorBrush? brush;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(target, xamlRoot);
            brush = compositor.CreateColorBrush(tint);
            target.SystemBackdrop = brush;
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            target.SystemBackdrop = null;
            brush?.Dispose();
            brush = null;
            base.OnTargetDisconnected(target);
        }
    }

    private static class Native
    {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
        public const nint HWND_TOP = 0, HWND_BOTTOM = 1, HWND_NOTOPMOST = -2;
        public const uint SWP_SHOWWINDOW = 0x40;
        public delegate bool EnumWindowsProc(nint h, nint l);
        public const nint WS_CAPTION = 0x00C00000, WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000, WS_THICKFRAME = 0x00040000;
        public const nint WS_EX_TOOLWINDOW = 0x00000080;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
        public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWA_SYSTEMBACKDROP_TYPE = 38;
        public const uint DWMWCP_ROUND = 2, DWMWA_COLOR_NONE = 0xFFFFFFFE, DWMSBT_NONE = 1;

        [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEXW
        {
            public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll", ExactSpelling = true)] public static extern nint GetWindowLongPtrW(nint h, int i);
        [DllImport("user32.dll", ExactSpelling = true)] public static extern nint SetWindowLongPtrW(nint h, int i, nint v);
        [DllImport("user32.dll", ExactSpelling = true)] public static extern bool SetWindowPos(nint h, nint a, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll", ExactSpelling = true)] public static extern uint GetDpiForWindow(nint h);
        [DllImport("user32.dll", ExactSpelling = true)] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll", ExactSpelling = true)] public static extern nint MonitorFromWindow(nint h, uint f);
        [DllImport("user32.dll", ExactSpelling = true)] public static extern bool EnumWindows(EnumWindowsProc p, nint l);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern nint FindWindowExW(nint parent, nint after, string cls, string? title);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint h, StringBuilder s, int n);
        [DllImport("user32.dll", ExactSpelling = true)] public static extern bool GetMonitorInfoW(nint m, ref MONITORINFOEXW i);
        [DllImport("dwmapi.dll", ExactSpelling = true)] public static extern int DwmSetWindowAttribute(nint h, uint a, ref uint v, uint s);
        [DllImport("dwmapi.dll", ExactSpelling = true)] public static extern int DwmExtendFrameIntoClientArea(nint h, ref MARGINS m);
        [DllImport("CoreMessaging.dll", ExactSpelling = true)] public static extern int CreateDispatcherQueueController(DispatcherQueueOptions o, out nint c);
    }
}
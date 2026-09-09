using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host;

/// <summary>
/// Experimental Windows adapter that reparents the probe window into the Explorer taskbar.
/// Everything Explorer-specific (window classes, reparenting, styles) is private to this class
/// and is a replaceable experiment, not an MTP contract. Every Win32 step is checked and any
/// failure removes the probe window again so the taskbar never keeps an orphaned child.
/// </summary>
public sealed class Win32ExplorerTaskbarEmbedAdapter : IExplorerTaskbarEmbedAdapter
{
    private const int ProbeWidthDip = 240;
    private const int ProbeHeightDip = 40;
    private const int ProbeMarginDip = 8;
    private const string TaskbarClassName = "Shell_TrayWnd";
    private const string TrayNotifyClassName = "TrayNotifyWnd";
    private const string SimulatedMissingClassName = "Mtp.Host.Probe.NoSuchTaskbarWindow";

    private static nint systemDispatcherQueueController;

    private readonly Func<int>? displayCountProvider;
    private ExplorerTaskbarProbeWindow? window;
    private nint windowHandle;
    private nint originalStyle;
    private bool styleChanged;
    private bool reparented;

    public Win32ExplorerTaskbarEmbedAdapter(Func<int>? displayCountProvider = null)
    {
        this.displayCountProvider = displayCountProvider;
    }

    public event EventHandler? Lost;

    public bool IsEmbedded => window is not null && reparented;

    public CoreResult<ExplorerTaskbarProbeReport> TryEmbed(HostComponentDisplayModel component, ExplorerTaskbarProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(request);

        if (window is not null)
        {
            Cleanup();
        }

        var steps = new List<ExplorerTaskbarProbeStep>();
        try
        {
            return Embed(component, request, steps);
        }
        catch (Exception exception)
        {
            Cleanup();
            return Failure("explorer_probe_unexpected_exception", "The probe stopped on an unexpected exception.", exception.GetType().Name);
        }
    }

    public CoreResult<bool> Detach()
    {
        if (window is null)
        {
            return CoreResult<bool>.Success(true);
        }

        try
        {
            Cleanup();
            return CoreResult<bool>.Success(true);
        }
        catch (Exception exception)
        {
            return CoreResult<bool>.Failure(
                new StructuredError("explorer_probe_detach_failed", "The probe window could not be removed from the taskbar.", exception.GetType().Name));
        }
    }

    /// <summary>
    /// Applies the experimental transparency stack to a probe window: a transparent (or tinted) system
    /// backdrop brush plus DWM frame extension over the whole client area, which turns the window's own
    /// surface into DWM glass. Public so the top-level control window in MainWindow uses the identical
    /// code path; the Win32 calls still live only in this file. DWM frame extension is a top-level window
    /// concept, so under WS_CHILD the recorded HRESULTs are themselves probe evidence.
    /// </summary>
    public static bool TryApplyWindowTransparency(ExplorerTaskbarProbeWindow window, ProbeTransparencyMode mode, out string detail)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            var parts = new List<string> { mode.ToString() };
            var hwnd = window.WindowHandle;
            if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            {
                detail = $"{mode}: window handle unavailable";
                return false;
            }

            // DeskBox order: frame styles and DWM glass first, then the backdrop brush.
            parts.Add(SuppressFrameDecorations(hwnd, out var frameResult));

            if (mode == ProbeTransparencyMode.AcrylicController)
            {
                if (!window.TryApplyAcrylicController(out var acrylicDetail))
                {
                    detail = $"{mode}: acrylic failed: {acrylicDetail}";
                    return false;
                }

                parts.Add(acrylicDetail ?? "acrylic");
            }
            else
            {
                var tint = mode switch
                {
                    ProbeTransparencyMode.TintDiagnostic => Windows.UI.Color.FromArgb(128, 255, 0, 0),
                    ProbeTransparencyMode.NearTransparentBackdrop => Windows.UI.Color.FromArgb(1, 0, 0, 0),
                    _ => Windows.UI.Color.FromArgb(0, 0, 0, 0),
                };
                if (!window.TryApplyTransparentBackdrop(tint, out var backdropFailure))
                {
                    detail = $"{mode}: backdrop failed: {backdropFailure}";
                    return false;
                }

                parts.Add(string.Create(CultureInfo.InvariantCulture, $"backdrop=argb({tint.A},{tint.R},{tint.G},{tint.B})"));
            }

            var exStyle = (uint)(long)NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"exstyle=0x{exStyle:X8} noredirectionbitmap={((exStyle & NativeMethods.WS_EX_NOREDIRECTIONBITMAP) != 0 ? "yes" : "no")}"));

            detail = string.Join(" ", parts);
            return frameResult >= 0;
        }
        catch (Exception exception)
        {
            detail = $"{mode}: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private CoreResult<ExplorerTaskbarProbeReport> Embed(
        HostComponentDisplayModel component,
        ExplorerTaskbarProbeRequest request,
        List<ExplorerTaskbarProbeStep> steps)
    {
        var className = request.SimulateTaskbarUnavailable ? SimulatedMissingClassName : TaskbarClassName;
        var taskbar = NativeMethods.FindWindowW(className, null);
        if (taskbar == 0)
        {
            steps.Add(new ExplorerTaskbarProbeStep("find_taskbar", false, request.SimulateTaskbarUnavailable ? "simulated" : null));
            return Failure("explorer_probe_taskbar_not_found", "The Explorer taskbar window was not found.", request.SimulateTaskbarUnavailable ? "simulated" : null);
        }

        steps.Add(new ExplorerTaskbarProbeStep("find_taskbar", true, null));

        if (!NativeMethods.GetClientRect(taskbar, out var clientRect) ||
            !NativeMethods.GetWindowRect(taskbar, out var taskbarScreen) ||
            clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            steps.Add(new ExplorerTaskbarProbeStep("measure_taskbar", false, null));
            return Failure("explorer_probe_taskbar_rect_invalid", "The taskbar rectangle could not be read.");
        }

        var taskbarClientSize = new SizeInt32(clientRect.Width, clientRect.Height);
        var taskbarScreenRect = taskbarScreen.ToRectInt32();
        steps.Add(new ExplorerTaskbarProbeStep("measure_taskbar", true, FormatRect(taskbarScreenRect)));

        var dpi = NativeMethods.GetDpiForWindow(taskbar);
        if (dpi == 0)
        {
            steps.Add(new ExplorerTaskbarProbeStep("read_dpi", false, null));
            return Failure("explorer_probe_dpi_unavailable", "The taskbar DPI could not be determined.");
        }

        steps.Add(new ExplorerTaskbarProbeStep("read_dpi", true, dpi.ToString(CultureInfo.InvariantCulture)));

        int? trayLeftEdgeClientX = null;
        var trayNotify = NativeMethods.FindWindowExW(taskbar, 0, TrayNotifyClassName, null);
        if (trayNotify != 0 && NativeMethods.GetWindowRect(trayNotify, out var trayRect))
        {
            var point = new NativeMethods.POINT { X = trayRect.Left, Y = trayRect.Top };
            if (NativeMethods.ScreenToClient(taskbar, ref point))
            {
                trayLeftEdgeClientX = point.X;
            }
        }

        steps.Add(new ExplorerTaskbarProbeStep(
            "find_tray_anchor",
            trayLeftEdgeClientX is not null,
            trayLeftEdgeClientX is int trayX ? trayX.ToString(CultureInfo.InvariantCulture) : "not found; using fixed inset"));

        var placement = ExplorerTaskbarProbePlacement.TryCalculate(
            taskbarClientSize,
            trayLeftEdgeClientX,
            new SizeInt32(ProbeWidthDip, ProbeHeightDip),
            dpi,
            ProbeMarginDip);
        if (!placement.IsSuccess)
        {
            steps.Add(new ExplorerTaskbarProbeStep("calculate_placement", false, placement.Error!.Code));
            return CoreResult<ExplorerTaskbarProbeReport>.Failure(placement.Error);
        }

        var target = placement.Value!;
        steps.Add(new ExplorerTaskbarProbeStep("calculate_placement", true, $"{target.AnchorKind} {FormatRect(target.ClientRect)}"));

        window = new ExplorerTaskbarProbeWindow(component);
        window.PrepareHidden(new SizeInt32(target.ClientRect.Width, target.ClientRect.Height));
        windowHandle = window.WindowHandle;
        if (windowHandle == 0 || !NativeMethods.IsWindow(windowHandle))
        {
            steps.Add(new ExplorerTaskbarProbeStep("create_window", false, null));
            Cleanup();
            return Failure("explorer_probe_window_create_failed", "The probe window handle could not be created.");
        }

        window.Closed += Window_Closed;
        steps.Add(new ExplorerTaskbarProbeStep("create_window", true, null));

        var dispatcherReady = EnsureSystemDispatcherQueue(out var dispatcherDetail);
        steps.Add(new ExplorerTaskbarProbeStep("system_dispatcher_queue", dispatcherReady, dispatcherDetail));

        // DWM per-window attributes and composition backdrops reject WS_CHILD windows (E_HANDLE / E_INVALIDARG),
        // and a material attached while top-level crashes the process once the window is reparented.
        // The embed path therefore applies no material; the recorded HRESULTs are the probe evidence.
        steps.Add(new ExplorerTaskbarProbeStep(
            "apply_transparency",
            false,
            $"{request.TransparencyMode}: skipped, DWM materials are not available to WS_CHILD windows"));

        originalStyle = NativeMethods.GetWindowLongPtrW(windowHandle, NativeMethods.GWL_STYLE);
        var childStyle = ((uint)(long)originalStyle & ~NativeMethods.TopLevelStyleMask) | NativeMethods.WS_CHILD;
        NativeMethods.SetLastError(0);
        var previousStyle = NativeMethods.SetWindowLongPtrW(windowHandle, NativeMethods.GWL_STYLE, unchecked((nint)(int)childStyle));
        if (previousStyle == 0 && Marshal.GetLastWin32Error() != 0)
        {
            var error = Marshal.GetLastWin32Error();
            steps.Add(new ExplorerTaskbarProbeStep("apply_child_style", false, FormatWin32Error(error)));
            Cleanup();
            return Failure("explorer_probe_style_change_failed", "The probe window style could not be changed to a child window.", FormatWin32Error(error));
        }

        styleChanged = true;
        steps.Add(new ExplorerTaskbarProbeStep("apply_child_style", true, null));

        NativeMethods.SetLastError(0);
        var previousParent = NativeMethods.SetParent(windowHandle, taskbar);
        if (previousParent == 0 && Marshal.GetLastWin32Error() != 0)
        {
            var error = Marshal.GetLastWin32Error();
            steps.Add(new ExplorerTaskbarProbeStep("set_parent", false, FormatWin32Error(error)));
            Cleanup();
            return Failure("explorer_probe_set_parent_failed", "The probe window could not be attached to the taskbar.", FormatWin32Error(error));
        }

        reparented = true;
        if (NativeMethods.GetAncestor(windowHandle, NativeMethods.GA_PARENT) != taskbar)
        {
            steps.Add(new ExplorerTaskbarProbeStep("set_parent", false, "parent mismatch"));
            Cleanup();
            return Failure("explorer_probe_parent_mismatch", "The taskbar did not become the parent of the probe window.");
        }

        steps.Add(new ExplorerTaskbarProbeStep("set_parent", true, null));

        if (!NativeMethods.SetWindowPos(
                windowHandle,
                NativeMethods.HWND_TOP,
                target.ClientRect.X,
                target.ClientRect.Y,
                target.ClientRect.Width,
                target.ClientRect.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED))
        {
            var error = Marshal.GetLastWin32Error();
            steps.Add(new ExplorerTaskbarProbeStep("position_window", false, FormatWin32Error(error)));
            Cleanup();
            return Failure("explorer_probe_position_failed", "The probe window could not be positioned inside the taskbar.", FormatWin32Error(error));
        }

        steps.Add(new ExplorerTaskbarProbeStep("position_window", true, null));
        steps.Add(new ExplorerTaskbarProbeStep("dwm_child_window_check", true, SuppressFrameDecorations(windowHandle)));

        if (!NativeMethods.IsWindowVisible(windowHandle))
        {
            steps.Add(new ExplorerTaskbarProbeStep("verify_visible", false, null));
            Cleanup();
            return Failure("explorer_probe_not_visible", "The probe window is attached but not visible.");
        }

        if (!NativeMethods.GetWindowRect(windowHandle, out var embeddedScreen) ||
            !IsWithin(embeddedScreen.ToRectInt32(), taskbarScreenRect))
        {
            steps.Add(new ExplorerTaskbarProbeStep("verify_visible", false, "outside taskbar"));
            Cleanup();
            return Failure("explorer_probe_geometry_mismatch", "The probe window is not inside the taskbar rectangle.");
        }

        var embeddedScreenRect = embeddedScreen.ToRectInt32();
        steps.Add(new ExplorerTaskbarProbeStep("verify_visible", true, FormatRect(embeddedScreenRect)));
        steps.Add(new ExplorerTaskbarProbeStep("backdrop_state", true, window.DescribeBackdropState()));
        steps.Add(new ExplorerTaskbarProbeStep("display_environment", true, DescribeDisplayEnvironment(windowHandle)));

        var report = new ExplorerTaskbarProbeReport(
            DateTimeOffset.Now,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            ReadDisplayCount(),
            dpi,
            taskbarClientSize,
            taskbarScreenRect,
            trayLeftEdgeClientX,
            target.AnchorKind,
            target.ClientRect,
            embeddedScreenRect,
            steps.ToArray());
        return CoreResult<ExplorerTaskbarProbeReport>.Success(report);
    }

    /// <summary>
    /// Strips top-level frame styles, extends DWM glass over the client area and disables DWM non-client
    /// rendering, corners and border color. Presenter changes and AppWindow.Show re-apply frame styles,
    /// so callers re-run this after the window is shown.
    /// </summary>
    public static string SuppressFrameDecorations(nint hwnd) => SuppressFrameDecorations(hwnd, out _);

    private static string SuppressFrameDecorations(nint hwnd, out int frameResult)
    {
        var stripped = false;
        var style = (uint)(long)NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_STYLE);
        var isChild = (style & NativeMethods.WS_CHILD) != 0;
        if (!isChild && (style & NativeMethods.TopLevelFrameMask) != 0)
        {
            _ = NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_STYLE, unchecked((nint)(int)(style & ~NativeMethods.TopLevelFrameMask)));
            stripped = true;
        }

        // DeskBox marks its widgets WS_EX_TOOLWINDOW and never uses TOPMOST; WS_EX_WINDOWEDGE is kept by the
        // system for as long as any dialog/thick frame bit is present, so it only disappears once GWL_STYLE is clean.
        var exStyle = (uint)(long)NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
        var wantedExStyle = (exStyle & ~NativeMethods.EdgeExStyleMask) | (isChild ? 0 : NativeMethods.WS_EX_TOOLWINDOW);
        if (wantedExStyle != exStyle)
        {
            _ = NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE, unchecked((nint)(int)wantedExStyle));
            stripped = true;
        }

        if (stripped)
        {
            _ = NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }

        var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        frameResult = NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        var backdropType = NativeMethods.DWMSBT_NONE;
        var backdropTypeResult = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(uint));
        var corner = NativeMethods.DWMWCP_ROUND;
        var cornerResult = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(uint));
        var border = NativeMethods.DWMWA_COLOR_NONE;
        var borderResult = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(uint));
        var finalStyle = (uint)(long)NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_STYLE);
        var finalExStyle = (uint)(long)NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(stripped ? "frame-styles-stripped " : string.Empty)}style=0x{finalStyle:X8} exstyle=0x{finalExStyle:X8} dwm frame=0x{frameResult:X8} backdroptype=0x{backdropTypeResult:X8} corner=0x{cornerResult:X8} border=0x{borderResult:X8}");
    }

    /// <summary>
    /// Records which monitor the window sits on and that monitor's advanced-color state. DWM composites
    /// HDR/WCG displays through a different pipeline, so the same glass window can render differently per screen.
    /// Must be read after the window is shown at its final position, otherwise it reports the creation monitor.
    /// </summary>
    public static string DescribeDisplayEnvironment(nint hwnd)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFOEXW { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEXW>() };
            var device = NativeMethods.GetMonitorInfoW(monitor, ref info) ? info.szDevice : "unknown";
            var primary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
            string colorKind;
            try
            {
                var displayInformation = Microsoft.Graphics.Display.DisplayInformation.CreateForWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
                colorKind = displayInformation.GetAdvancedColorInfo().CurrentAdvancedColorKind.ToString();
            }
            catch (Exception exception)
            {
                colorKind = $"unavailable:{exception.GetType().Name}";
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"monitor={device} primary={primary} dpi={NativeMethods.GetDpiForWindow(hwnd)} color={colorKind}");
        }
        catch (Exception exception)
        {
            return $"monitor=unavailable:{exception.GetType().Name}";
        }
    }

    /// <summary>
    /// The system compositor used by the transparent backdrop needs a Windows.System dispatcher queue on
    /// the UI thread, which WinUI 3 does not create by default. The controller is kept for the lifetime of
    /// the thread and is never released.
    /// </summary>
    private static bool EnsureSystemDispatcherQueue(out string? detail)
    {
        try
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
            {
                detail = systemDispatcherQueueController == 0 ? "existing" : "created earlier";
                return true;
            }

            var options = new NativeMethods.DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf<NativeMethods.DispatcherQueueOptions>(),
                threadType = NativeMethods.DQTYPE_THREAD_CURRENT,
                apartmentType = NativeMethods.DQTAT_COM_STA,
            };
            var hresult = NativeMethods.CreateDispatcherQueueController(options, out var controller);
            if (hresult < 0 || controller == 0)
            {
                detail = string.Create(CultureInfo.InvariantCulture, $"hresult:0x{hresult:X8}");
                return false;
            }

            systemDispatcherQueueController = controller;
            detail = "created";
            return true;
        }
        catch (Exception exception)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private int ReadDisplayCount()
    {
        try
        {
            return displayCountProvider?.Invoke() ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void Cleanup()
    {
        var current = window;
        if (current is null)
        {
            return;
        }

        current.Closed -= Window_Closed;
        try
        {
            if (windowHandle != 0 && NativeMethods.IsWindow(windowHandle))
            {
                _ = NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_HIDE);
                if (reparented)
                {
                    _ = NativeMethods.SetParent(windowHandle, 0);
                }

                if (styleChanged)
                {
                    _ = NativeMethods.SetWindowLongPtrW(windowHandle, NativeMethods.GWL_STYLE, originalStyle);
                }
            }
        }
        finally
        {
            window = null;
            windowHandle = 0;
            reparented = false;
            styleChanged = false;
            originalStyle = 0;
        }

        try
        {
            current.Close();
        }
        catch (Exception)
        {
            // The window may already have been destroyed by Explorer; nothing else can be released here.
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (window is null)
        {
            return;
        }

        window.Closed -= Window_Closed;
        window = null;
        windowHandle = 0;
        reparented = false;
        styleChanged = false;
        originalStyle = 0;
        Lost?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWithin(RectInt32 rectangle, RectInt32 bounds) =>
        rectangle.X >= bounds.X &&
        rectangle.Y >= bounds.Y &&
        rectangle.X + rectangle.Width <= bounds.X + bounds.Width &&
        rectangle.Y + rectangle.Height <= bounds.Y + bounds.Height;

    private static string FormatRect(RectInt32 rect) =>
        string.Create(CultureInfo.InvariantCulture, $"({rect.X},{rect.Y}) {rect.Width}x{rect.Height}");

    private static string FormatWin32Error(int error) =>
        string.Create(CultureInfo.InvariantCulture, $"win32:{error}");

    private static CoreResult<ExplorerTaskbarProbeReport> Failure(string code, string message, string? path = null) =>
        CoreResult<ExplorerTaskbarProbeReport>.Failure(new StructuredError(code, message, path));

    private static class NativeMethods
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        public const uint GA_PARENT = 1;
        public const nint HWND_TOP = 0;
        public const int SW_HIDE = 0;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint WS_CHILD = 0x40000000;

        public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const uint DWMWA_BORDER_COLOR = 34;
        public const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;
        public const uint DWMWCP_DONOTROUND = 1;
        public const uint DWMWCP_ROUND = 2;
        public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
        public const uint DWMSBT_NONE = 1;

        public const int DQTYPE_THREAD_CURRENT = 2;
        public const int DQTAT_COM_STA = 2;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint MONITORINFOF_PRIMARY = 1;

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_BORDER = 0x00800000;
        private const uint WS_DLGFRAME = 0x00400000;
        private const uint WS_THICKFRAME = 0x00040000;
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_MINIMIZEBOX = 0x00020000;
        private const uint WS_MAXIMIZEBOX = 0x00010000;

        public const uint TopLevelStyleMask = WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

        /// <summary>Frame styles removed from a top-level probe window (the DeskBox set) so no non-client edge is drawn.</summary>
        public const uint TopLevelFrameMask = WS_CAPTION | WS_BORDER | WS_DLGFRAME | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

        /// <summary>WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_TOPMOST.</summary>
        public const uint EdgeExStyleMask = 0x00000001 | 0x00000100 | 0x00000200 | 0x00020000 | 0x00000008;
        public const uint WS_EX_TOOLWINDOW = 0x00000080;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;

            public int Height => Bottom - Top;

            public RectInt32 ToRectInt32() => new(Left, Top, Width, Height);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DispatcherQueueOptions
        {
            public int dwSize;
            public int threadType;
            public int apartmentType;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEXW
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint FindWindowW(
            [MarshalAs(UnmanagedType.LPWStr)] string? lpClassName,
            [MarshalAs(UnmanagedType.LPWStr)] string? lpWindowName);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint FindWindowExW(
            nint hWndParent,
            nint hWndChildAfter,
            [MarshalAs(UnmanagedType.LPWStr)] string? lpszClass,
            [MarshalAs(UnmanagedType.LPWStr)] string? lpszWindow);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern uint GetDpiForWindow(nint hwnd);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint SetParent(nint hWndChild, nint hWndNewParent);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint GetAncestor(nint hwnd, uint gaFlags);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(nint hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(nint hWnd);

        [DllImport("dwmapi.dll", ExactSpelling = true)]
        public static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

        [DllImport("dwmapi.dll", ExactSpelling = true)]
        public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS pMarInset);

        [DllImport("CoreMessaging.dll", ExactSpelling = true)]
        public static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint dispatcherQueueController);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern void SetLastError(uint dwErrCode);
    }
}
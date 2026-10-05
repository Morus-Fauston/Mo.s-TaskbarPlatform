using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

/// <summary>A Host-owned ordinary hint. Native layered transparency passes input across process boundaries.</summary>
internal sealed class ShortHintWindow
{
    private Window? window;
    private readonly Grid root = new() { IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Action closed;
    private readonly Action<string, object?> record;
    private readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();
    private HostAppearancePreferences appearance = new();
    private MaterialSpec? appliedMaterial;
    private bool highContrast, highContrastSubscribed, closeRequested, closeObserved, released;
    private int appearanceQueued;
    private double appliedOpacity;

    internal nint Handle { get; private set; }
    internal bool IsAlive => Handle != 0 && FlyoutNative.IsWindow(Handle);
    internal bool IsVisible => IsAlive && !closeObserved && appliedOpacity > 0 && IsWindowVisible(Handle);
    internal PixelRect LastBounds { get; private set; }
    internal FrameworkElement RootForTesting => root;
    internal Action<Window>? CloseWindowForTesting { get; set; }

    internal ShortHintWindow(FrameworkElement content, Action closed, Action<string, object?> record)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(closed);
        ArgumentNullException.ThrowIfNull(record);
        this.closed = closed;
        this.record = record;
        try
        {
            window = new Window { Title = "MTP 短提示" };
            Handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            window.Closed += OnClosed;
            // The supplied visual is created by the restricted Host renderer; no focusable header is added.
            content.IsHitTestVisible = false;
            root.Children.Add(content);
            AutomationProperties.SetAutomationId(root, "MtpShortHint");
            window.Content = root;
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }
            // WS_EX_TRANSPARENT together with WS_EX_LAYERED is necessary: HTTRANSPARENT alone
            // only searches windows on the same thread and cannot satisfy cross-process pass-through.
            long style = FlyoutNative.GetWindowLongPtrW(Handle, -20).ToInt64();
            Marshal.SetLastPInvokeError(0);
            if (FlyoutNative.SetWindowLongPtrW(Handle, -20, new nint(style | 0x080800A0)) == 0 &&
                Marshal.GetLastPInvokeError() != 0) throw FlyoutNative.Error("SetHintWindowStyles");
            FlyoutNative.Opacity(Handle, 0);
            root.ActualThemeChanged += ThemeChanged;
            try { accessibility.HighContrastChanged += HighContrastChanged; highContrastSubscribed = true; }
            catch (COMException error) { Record("hint-high-contrast-notification-unavailable", error.HResult); }
            ApplyTheme();
        }
        catch (Exception error)
        {
            var cleanup = TryClose();
            if (!cleanup.IsSuccess)
                throw new ShortHintWindowCreationException(this, error);
            throw;
        }
    }

    internal void Apply(PixelRect bounds, double opacity)
    {
        if (!bounds.IsValid) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        if (!IsAlive || closeRequested || closeObserved) return;
        FlyoutNative.Position(Handle, bounds);
        FlyoutNative.Opacity(Handle, opacity);
        LastBounds = FlyoutNative.Bounds(Handle);
        uint dpi = GetDpiForWindow(Handle);
        if (dpi == 0) throw FlyoutNative.Error("GetDpiForWindow");
        root.Width = LastBounds.Width * 96d / dpi;
        root.Height = LastBounds.Height * 96d / dpi;
        // The layered API quantizes alpha to a byte. A positive sub-byte request is still invisible.
        appliedOpacity = FlyoutNative.ReadOpacity(Handle) / 255d;
        Record("hint-frame-applied", new { bounds = LastBounds, opacity });
    }

    internal void Show()
    {
        if (!IsAlive || closeRequested || closeObserved) return;
        window!.AppWindow.Show(false);
    }

    internal CoreResult<bool> TryClose()
    {
        if (window is null || !IsAlive) { ReleaseContent(); return CoreResult<bool>.Success(true); }
        try
        {
            if (!closeRequested)
            {
                closeRequested = true;
                if (CloseWindowForTesting is { } close) close(window); else window.Close();
            }
            if (IsAlive) return CoreResult<bool>.Failure(new("FlyoutCleanupPending", "短提示窗口尚未确认销毁"));
            ReleaseContent();
            return CoreResult<bool>.Success(true);
        }
        catch (Exception error)
        {
            if (!closeObserved) closeRequested = false;
            return CoreResult<bool>.Failure(new("FlyoutCleanupPending",
                string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message));
        }
    }

    internal void ApplyAppearance(HostAppearancePreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(value.Theme) || !new MaterialSpec(value.Material, value.Opacity).IsValid)
            throw new ArgumentException("Invalid Host appearance", nameof(value));
        if (appearance == value) return;
        appearance = value;
        root.RequestedTheme = value.Theme switch { HostTheme.Light => ElementTheme.Light,
            HostTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyTheme();
    }

    internal void RefreshAppearance()
    {
        if (!IsAlive || closeRequested) return;
        try { if (accessibility.HighContrast != highContrast) ApplyTheme(); }
        catch (COMException) { }
    }

    private void ThemeChanged(FrameworkElement sender, object args) => ApplyTheme();
    private void HighContrastChanged(Windows.UI.ViewManagement.AccessibilitySettings sender, object args)
    {
        if (Interlocked.Exchange(ref appearanceQueued, 1) != 0) return;
        if (!root.DispatcherQueue.TryEnqueue(() => { Interlocked.Exchange(ref appearanceQueued, 0); ApplyTheme(); }))
            Interlocked.Exchange(ref appearanceQueued, 0);
    }

    private void ApplyTheme()
    {
        if (window is null || closeRequested || released) return;
        try
        {
            try { highContrast = accessibility.HighContrast; } catch (COMException) { }
            var resolution = MaterialResolver.Resolve(new(appearance.Material, appearance.Opacity), new(true,
                Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported(),
                Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()));
            var effective = highContrast ? new MaterialSpec(MaterialKind.None, 1) : resolution.Effective;
            byte shade = root.ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
            root.Background = highContrast
                ? new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(effective.Kind == MaterialKind.None ? (byte)255 :
                    effective.Kind == MaterialKind.Solid ? (byte)(255 * effective.Opacity) : (byte)0, shade, shade, shade));
            if (appliedMaterial != effective)
            {
                window.SystemBackdrop = effective.Kind is MaterialKind.Acrylic or MaterialKind.Mica
                    ? new Islands.ConfiguredMaterialBackdrop(effective.Kind, effective.Opacity, Record) : null;
                appliedMaterial = effective;
                Record("hint-material-applied", new { requested = appearance.Material.ToString(), effective = effective.Kind.ToString(), effective.Opacity, highContrast });
            }
        }
        catch (Exception error)
        {
            window.SystemBackdrop = null;
            appliedMaterial = new(MaterialKind.None, 1);
            byte shade = root.ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
            root.Background = highContrast
                ? new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, shade, shade, shade));
            Record("hint-material-fallback", error.Message);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (closeObserved) return;
        closeObserved = true;
        try { closed(); } catch (Exception error) { Record("hint-closed-callback-failed", error.Message); }
    }

    private void ReleaseContent()
    {
        if (released) return;
        released = true;
        // Closing the WinUI Window disconnects and releases its SystemBackdrop target.
        // Do not touch that COM property after native destruction.
        if (window is not null) window.Closed -= OnClosed;
        root.ActualThemeChanged -= ThemeChanged;
        if (highContrastSubscribed)
        {
            highContrastSubscribed = false;
            try { accessibility.HighContrastChanged -= HighContrastChanged; }
            catch (COMException error) { Record("hint-high-contrast-notification-release-unavailable", error.HResult); }
        }
        root.Children.Clear();
        window = null;
        Handle = 0;
        appliedOpacity = 0;
    }

    private void Record(string kind, object? detail)
    { try { record(kind, detail); } catch { /* A diagnostic sink cannot own or interrupt a window. */ } }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetDpiForWindow(nint window);
}

internal sealed class ShortHintWindowCreationException(ShortHintWindow owner, Exception inner)
    : Exception("短提示创建失败且清理尚未确认；保留窗口所有权以供后续清理。", inner)
{
    internal ShortHintWindow Owner { get; } = owner;
}

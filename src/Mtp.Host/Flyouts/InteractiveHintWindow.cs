using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

/// <summary>One owner for the pass-through background and a control-shaped ordinary WinUI foreground.</summary>
internal sealed class InteractiveHintWindow
{
    private readonly TemplateRenderer foreground, background;
    private readonly Action closed;
    private readonly Action<string, object?> record;
    private readonly Action<string, bool> interaction;
    private readonly Grid root = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        TabFocusNavigation = KeyboardNavigationMode.Cycle };
    private readonly Dictionary<Control, Subscription> subscriptions = [];
    private readonly HashSet<Control> hovered = [], pressed = [], captured = [];
    private readonly Dictionary<string, bool> sources = new(StringComparer.Ordinal)
        { ["hover"] = false, ["pressed"] = false, ["capture"] = false, ["keyboard"] = false };
    private readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();
    private ShortHintWindow? backdrop;
    private Window? window;
    private HostAppearancePreferences appearance = new();
    private MaterialSpec? material;
    private PixelRect[] regionBounds = [];
    private bool stopped, closeRequested, closeObserved, callbackSent, updating, released, highContrast, subscribed, hideForeground, refreshingInteraction;
    private int refreshQueued, appearanceQueued;
    private double opacity;
    internal nint Handle { get; private set; }
    internal nint BackgroundHandle => backdrop?.Handle ?? 0;
    internal bool IsAlive => Handle != 0 && FlyoutNative.IsWindow(Handle) || backdrop?.IsAlive == true;
    internal bool IsVisible => backdrop?.IsVisible == true && opacity > 0;
    internal PixelRect LastBounds { get; private set; }
    internal Action<Window>? CloseForegroundForTesting { get; set; }
    internal FrameworkElement RootForTesting => root;
    internal IReadOnlyList<PixelRect> RegionBoundsForTesting => regionBounds;

    internal InteractiveHintWindow(TemplateRenderer foreground, TemplateRenderer background, Action closed,
        Action<string, object?> record, Action<string, bool> interaction)
    {
        this.foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        this.background = background ?? throw new ArgumentNullException(nameof(background));
        this.closed = closed ?? throw new ArgumentNullException(nameof(closed));
        this.record = record ?? throw new ArgumentNullException(nameof(record));
        this.interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        try
        {
            background.RefreshFromSnapshot(foreground.CapturedSnapshot);
            backdrop = new(background, PartClosed, Record);
            window = new Window { Title = "MTP 可交互提示" };
            Handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            window.Closed += OnClosed; window.Activated += OnActivated;
            root.Children.Add(foreground); window.Content = root;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(root, "MtpInteractiveHint");
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
            }
            FlyoutNative.NoActivate(Handle, true);
            FlyoutNative.Opacity(Handle, 0);
            SetRegion([]); // No full-window hit surface, including before the first layout.
            root.LayoutUpdated += LayoutUpdated;
            root.ActualThemeChanged += ThemeChanged;
            foreground.PresentationChanged += PresentationChanged;
            foreground.InteractiveGeometryChanged += UpdateRegions;
            try { accessibility.HighContrastChanged += HighContrastChanged; subscribed = true; }
            catch (COMException error) { Record("interactive-hint-high-contrast-notification-unavailable", error.HResult); }
            ApplyTheme();
        }
        catch (Exception error)
        {
            if (error is ShortHintWindowCreationException retained) backdrop = retained.Owner;
            if (!TryClose().IsSuccess) throw new InteractiveHintWindowCreationException(this, error);
            throw;
        }
    }

    internal void Apply(PixelRect bounds, double value)
    {
        if (!bounds.IsValid || !double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (window is null || closeRequested || !FlyoutNative.IsWindow(Handle)) return;
        backdrop!.Apply(bounds, value);
        FlyoutNative.Position(Handle, bounds);
        FlyoutNative.Opacity(Handle, value);
        LastBounds = FlyoutNative.Bounds(Handle);
        uint dpi = GetDpiForWindow(Handle);
        if (dpi == 0) throw FlyoutNative.Error("GetDpiForWindow");
        root.Width = LastBounds.Width * 96d / dpi; root.Height = LastBounds.Height * 96d / dpi;
        opacity = FlyoutNative.ReadOpacity(Handle) / 255d;
        root.UpdateLayout(); UpdateRegions();
        Record("interactive-hint-frame-applied", new { bounds = LastBounds, opacity, regions = regionBounds.Length });
    }
    internal void Show()
    {
        if (window is null || closeRequested) return;
        backdrop?.Show();
        if (hideForeground) return;
        window.AppWindow.Show(false);
        // Reassert the sibling order without activation after either AppWindow.Show.
        if (LastBounds.IsValid) FlyoutNative.Position(Handle, LastBounds);
        root.UpdateLayout(); UpdateRegions();
    }
    internal bool FocusFirst()
    {
        if (stopped || closeRequested || window is null) return false;
        nint before = FlyoutNative.GetForegroundWindow();
        FlyoutNative.NoActivate(Handle, false);
        window.Activate();
        nint afterActivate = FlyoutNative.GetForegroundWindow();
        // AppWindow's original no-activation presentation is not evidence that Activate
        // has transferred the native foreground. Only this explicit keyboard path may
        // request foreground, and failure is reported rather than claiming focus.
        bool nativeRequested = afterActivate != Handle;
        bool nativeAccepted = !nativeRequested || SetForegroundWindow(Handle);
        root.UpdateLayout(); UpdateRegions();
        bool focused = FlyoutNative.GetForegroundWindow() == Handle && foreground.FocusFirst();
        UpdateKeyboard();
        nint after = FlyoutNative.GetForegroundWindow();
        var element = foreground.XamlRoot is { } xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) as Control : null;
        Record("interactive-hint-keyboard-entry", new
        {
            before = before.ToInt64(), afterActivate = afterActivate.ToInt64(), nativeRequested, nativeAccepted,
            after = after.ToInt64(), owner = Handle.ToInt64(), focused,
            focusedType = element?.GetType().Name, focusState = element?.FocusState.ToString(),
            candidates = foreground.GetInteractiveRegions().Select(x => new
                { x.NodeId, x.Control.IsEnabled, x.Control.IsTabStop, state = x.Control.FocusState.ToString() }).ToArray()
        });
        if (after != Handle) FlyoutNative.NoActivate(Handle, true);
        return focused && after == Handle;
    }

    private void PresentationChanged()
    {
        if (stopped) return;
        background.RefreshFromSnapshot(foreground.CapturedSnapshot);
        root.UpdateLayout(); UpdateRegions();
    }
    private void LayoutUpdated(object? sender, object args) => UpdateRegions();
    private void UpdateRegions()
    {
        if (updating || stopped || closeRequested || window is null || Handle == 0 || !FlyoutNative.IsWindow(Handle)) return;
        updating = true;
        try
        {
            background.SynchronizeHintBackgroundLayout(foreground);
            var regions = foreground.GetInteractiveRegions();
            var eligible = regions.Select(x => x.Control).ToHashSet();
            foreach (var control in subscriptions.Keys.Where(x => !eligible.Contains(x)).ToArray()) Detach(control);
            foreach (var control in eligible) if (!subscriptions.ContainsKey(control)) Attach(control);
            uint dpi = GetDpiForWindow(Handle);
            if (dpi == 0) throw FlyoutNative.Error("GetDpiForWindow");
            double scale = dpi / 96d;
            var pixels = new List<PixelRect>();
            foreach (var region in regions)
            {
                var bounds = foreground.TransformToVisual(root).TransformBounds(region.Bounds);
                int left = (int)Math.Clamp(Math.Floor(bounds.Left * scale), 0, LastBounds.Width);
                int top = (int)Math.Clamp(Math.Floor(bounds.Top * scale), 0, LastBounds.Height);
                int right = (int)Math.Clamp(Math.Ceiling(bounds.Right * scale), 0, LastBounds.Width);
                int bottom = (int)Math.Clamp(Math.Ceiling(bounds.Bottom * scale), 0, LastBounds.Height);
                if (right > left && bottom > top) pixels.Add(new(left, top, right - left, bottom - top));
            }
            var next = pixels.ToArray();
            if (!regionBounds.SequenceEqual(next)) SetRegion(next);
            RefreshInteraction();
        }
        catch (Exception error)
        {
            // A stale/full hit region is unsafe. Fail closed and let the manager clean up this instance.
            StopInteraction();
            Record("interactive-hint-region-failed", error.Message);
            PartClosed();
        }
        finally { updating = false; }
    }
    private void SetRegion(PixelRect[] rectangles)
    {
        if (rectangles.Length > TemplateLimits.NodesPerTemplate) throw new InvalidOperationException("Interactive region budget exceeded.");
        nint combined = CreateRectRgn(0, 0, 0, 0);
        if (combined == 0) throw FlyoutNative.Error("CreateRectRgn");
        try
        {
            foreach (var rectangle in rectangles)
            {
                nint part = CreateRectRgn(rectangle.X, rectangle.Y, (int)rectangle.Right, (int)rectangle.Bottom);
                if (part == 0) throw FlyoutNative.Error("CreateRectRgn");
                try { if (CombineRgn(combined, combined, part, 2) == 0) throw FlyoutNative.Error("CombineRgn"); }
                finally { DeleteObject(part); }
            }
            if (SetWindowRgn(Handle, combined, true) == 0) throw FlyoutNative.Error("SetWindowRgn");
            combined = 0; // Windows owns the region after successful SetWindowRgn, including its replacement.
            regionBounds = rectangles;
        }
        finally { if (combined != 0) DeleteObject(combined); }
    }

    private void Attach(Control control)
    {
        PointerEventHandler enter = (_, _) => { hovered.Add(control); PublishSources(); };
        PointerEventHandler exit = (_, _) =>
        {
            hovered.Remove(control);
            if (!HasCapture(control)) { pressed.Remove(control); captured.Remove(control); }
            PublishSources();
        };
        PointerEventHandler down = (_, _) =>
        { pressed.Add(control); if (HasCapture(control)) captured.Add(control); PublishSources(); };
        PointerEventHandler up = (_, _) => { pressed.Remove(control); captured.Remove(control); PublishSources(); };
        PointerEventHandler lost = (_, _) => { captured.Remove(control); pressed.Remove(control); PublishSources(); };
        RoutedEventHandler focus = (_, _) => QueueFocusRefresh();
        control.AddHandler(UIElement.PointerEnteredEvent, enter, true);
        control.AddHandler(UIElement.PointerExitedEvent, exit, true);
        control.AddHandler(UIElement.PointerPressedEvent, down, true);
        control.AddHandler(UIElement.PointerReleasedEvent, up, true);
        control.AddHandler(UIElement.PointerCaptureLostEvent, lost, true);
        control.GotFocus += focus; control.LostFocus += focus;
        subscriptions.Add(control, new(enter, exit, down, up, lost, focus));
    }
    private void Detach(Control control)
    {
        if (!subscriptions.Remove(control, out var item)) return;
        control.RemoveHandler(UIElement.PointerEnteredEvent, item.Enter);
        control.RemoveHandler(UIElement.PointerExitedEvent, item.Exit);
        control.RemoveHandler(UIElement.PointerPressedEvent, item.Down);
        control.RemoveHandler(UIElement.PointerReleasedEvent, item.Up);
        control.RemoveHandler(UIElement.PointerCaptureLostEvent, item.Lost);
        control.GotFocus -= item.Focus; control.LostFocus -= item.Focus;
        foreach (var element in Descendants(control).OfType<UIElement>()) element.ReleasePointerCaptures();
        hovered.Remove(control); pressed.Remove(control); captured.Remove(control);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject owner)
    {
        var pending = new Stack<DependencyObject>(); pending.Push(owner);
        for (int count = 0; pending.Count > 0 && count < 4096; count++)
        {
            var current = pending.Pop(); yield return current;
            for (int i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0 && pending.Count < 4096; i--)
                pending.Push(VisualTreeHelper.GetChild(current, i));
        }
    }
    private static bool HasCapture(Control control) => Descendants(control).OfType<UIElement>().Any(x => x.PointerCaptures?.Count > 0);
    /// <summary>Called by the manager's one driver, also after native geometry changes. No hook or private timer.</summary>
    internal void RefreshInteraction()
    {
        if (stopped || closeRequested || refreshingInteraction || window is null || !FlyoutNative.IsWindow(Handle)) return;
        refreshingInteraction = true;
        try
        {
            // The existing owner driver also reconciles geometry: control Loaded/Unloaded
            // and native layout can occur after the last changed animation frame.
            // UpdateRegions calls back here, where refreshingInteraction prevents recursion.
            UpdateRegions();
            if (stopped || closeRequested) return;
            // WinUI's routed PointerExited is not guaranteed when crossing a native window-region hole.
            // Reconcile hover with the real HWND hit and current control geometry, without intercepting input.
            if (!FlyoutNative.GetCursorPos(out var point)) throw new InvalidOperationException("无法读取可交互提示的原生指针位置");
            bool overWindow = opacity > 0 && IsWindowVisible(Handle) &&
                FlyoutNative.Root(FlyoutNative.WindowFromPoint(point)) == Handle;
            uint dpi = GetDpiForWindow(Handle);
            if (dpi == 0) throw FlyoutNative.Error("GetDpiForWindow");
            double x = (point.X - LastBounds.X) * 96d / dpi;
            double y = (point.Y - LastBounds.Y) * 96d / dpi;
            bool mouseDown = (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0 ||
                (GetAsyncKeyState(4) & 0x8000) != 0;
            hovered.Clear(); captured.Clear();
            foreach (var control in subscriptions.Keys)
            {
                bool enabled = control.IsEnabled && control.IsHitTestVisible && control.Visibility == Visibility.Visible && control.IsLoaded;
                bool ownsCapture = enabled && HasCapture(control);
                if (ownsCapture) captured.Add(control);
                if (!enabled || !mouseDown && !ownsCapture) pressed.Remove(control);
                if (!enabled || !overWindow) continue;
                var bounds = control.TransformToVisual(root).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
                if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom) hovered.Add(control);
            }
            UpdateKeyboard(); PublishSources();
        }
        catch (Exception error)
        {
            Record("interactive-hint-interaction-refresh-failed", error.Message);
            StopInteraction(); PartClosed();
        }
        finally { refreshingInteraction = false; }
    }
    private void QueueFocusRefresh()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        if (!root.DispatcherQueue.TryEnqueue(() => { Interlocked.Exchange(ref refreshQueued, 0); if (!stopped) UpdateKeyboard(); }))
            Interlocked.Exchange(ref refreshQueued, 0);
    }
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        { SetSource("keyboard", false); if (!closeRequested && Handle != 0) FlyoutNative.NoActivate(Handle, true); }
        else QueueFocusRefresh();
        RefreshInteraction();
    }
    private void UpdateKeyboard()
    {
        bool focused = false;
        if (!stopped && FlyoutNative.GetForegroundWindow() == Handle && foreground.XamlRoot is { } xamlRoot &&
            FocusManager.GetFocusedElement(xamlRoot) is DependencyObject current)
            for (int depth = 0; current is not null && depth < 64; depth++, current = VisualTreeHelper.GetParent(current))
                if (current is Control control && subscriptions.ContainsKey(control) && control.FocusState == FocusState.Keyboard)
                { focused = true; break; }
        SetSource("keyboard", focused);
    }
    private void PublishSources()
    {
        SetSource("hover", !stopped && hovered.Count > 0);
        SetSource("pressed", !stopped && pressed.Count > 0);
        SetSource("capture", !stopped && captured.Count > 0);
    }
    private void SetSource(string source, bool active)
    {
        if (sources[source] == active) return;
        sources[source] = active;
        try { interaction(source, active); } catch (Exception error) { Record("interactive-hint-interaction-callback-failed", error.Message); }
    }

    internal void StopInteraction()
    {
        if (stopped) return;
        stopped = true;
        foreach (var control in subscriptions.Keys.ToArray()) Detach(control);
        foreground.StopInteraction(); background.StopInteraction();
        foreach (var source in sources.Keys.ToArray()) SetSource(source, false);
        if (Handle != 0 && FlyoutNative.IsWindow(Handle))
        {
            try
            {
                // Keep the clipped visual surface throughout the exit animation; layered
                // transparency disables input across processes without removing painted controls.
                long style = FlyoutNative.GetWindowLongPtrW(Handle, -20).ToInt64();
                Marshal.SetLastPInvokeError(0);
                if (FlyoutNative.SetWindowLongPtrW(Handle, -20, new nint(style | 0x08080020)) == 0 &&
                    Marshal.GetLastPInvokeError() != 0) throw FlyoutNative.Error("StopHintInput");
            }
            catch (Exception error)
            {
                hideForeground = true;
                FlyoutNative.ShowWindow(Handle, 0);
                Record("interactive-hint-stop-input-failed", error.Message);
            }
        }
    }
    internal CoreResult<bool> TryClose()
    {
        StopInteraction();
        CoreResult<bool> result = CoreResult<bool>.Success(true);
        if (window is not null && Handle != 0 && FlyoutNative.IsWindow(Handle))
        {
            try
            {
                if (!closeRequested)
                { closeRequested = true; if (CloseForegroundForTesting is { } close) close(window); else window.Close(); }
                if (FlyoutNative.IsWindow(Handle)) result = CoreResult<bool>.Failure(new("FlyoutCleanupPending", "可交互提示前景窗口尚未确认销毁"));
            }
            catch (Exception error)
            {
                if (!closeObserved) closeRequested = false;
                result = CoreResult<bool>.Failure(new("FlyoutCleanupPending", error.Message));
            }
        }
        var backgroundClosed = backdrop?.TryClose() ?? CoreResult<bool>.Success(true);
        if (!backgroundClosed.IsSuccess) result = backgroundClosed;
        if (!IsAlive) { Release(); return CoreResult<bool>.Success(true); }
        return result;
    }
    private void OnClosed(object sender, WindowEventArgs args) { closeObserved = true; PartClosed(); }
    private void PartClosed()
    {
        if (callbackSent) return;
        callbackSent = true;
        try { closed(); } catch (Exception error) { Record("interactive-hint-closed-callback-failed", error.Message); }
    }
    private void Release()
    {
        if (released) return;
        released = true;
        foreground.PresentationChanged -= PresentationChanged;
        foreground.InteractiveGeometryChanged -= UpdateRegions;
        root.LayoutUpdated -= LayoutUpdated; root.ActualThemeChanged -= ThemeChanged;
        if (window is not null) { window.Closed -= OnClosed; window.Activated -= OnActivated; }
        if (subscribed)
        {
            subscribed = false;
            try { accessibility.HighContrastChanged -= HighContrastChanged; }
            catch (COMException error) { Record("interactive-hint-high-contrast-release-unavailable", error.HResult); }
        }
        root.Children.Clear(); window = null; Handle = 0; backdrop = null;
    }

    internal void ApplyAppearance(HostAppearancePreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(value.Theme) || !new MaterialSpec(value.Material, value.Opacity).IsValid)
            throw new ArgumentException("Invalid Host appearance", nameof(value));
        appearance = value; backdrop?.ApplyAppearance(value);
        root.RequestedTheme = value.Theme switch { HostTheme.Dark => ElementTheme.Dark, HostTheme.Light => ElementTheme.Light, _ => ElementTheme.Default };
        ApplyTheme();
    }
    internal void RefreshAppearance()
    {
        backdrop?.RefreshAppearance();
        try { if (accessibility.HighContrast != highContrast) ApplyTheme(); } catch (COMException) { }
        UpdateRegions();
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
            root.Background = highContrast ? new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(effective.Kind == MaterialKind.None ? (byte)255 :
                    effective.Kind == MaterialKind.Solid ? (byte)(255 * effective.Opacity) : (byte)0, shade, shade, shade));
            if (material != effective)
            {
                window.SystemBackdrop = effective.Kind is MaterialKind.Acrylic or MaterialKind.Mica
                    ? new Islands.ConfiguredMaterialBackdrop(effective.Kind, effective.Opacity, Record) : null;
                material = effective;
            }
        }
        catch (Exception error)
        {
            window.SystemBackdrop = null;
            root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
            material = new(MaterialKind.None, 1);
            Record("interactive-hint-material-fallback", error.Message);
        }
    }
    private void Record(string kind, object? detail) { try { record(kind, detail); } catch { } }
    private sealed record Subscription(PointerEventHandler Enter, PointerEventHandler Exit, PointerEventHandler Down,
        PointerEventHandler Up, PointerEventHandler Lost, RoutedEventHandler Focus);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}

internal sealed class InteractiveHintWindowCreationException(InteractiveHintWindow owner, Exception inner)
    : Exception("可交互提示创建失败且窗口清理尚未确认。", inner)
{
    internal InteractiveHintWindow Owner { get; } = owner;
}

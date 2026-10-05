using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mtp.Host.Templates;
using Mtp.Platform.Core;
using Windows.Foundation;
using Windows.System;

namespace Mtp.Host.Flyouts;

/// <summary>A normal WinUI Window; the group retains this owner until HWND destruction is confirmed.</summary>
internal sealed class TaskbarFlyoutWindow
{
    private Window? window;
    private readonly Action close, back;
    private readonly Action<string> open;
    private readonly Action closed;
    private readonly Action<string, object?> record;
    private readonly Grid root = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        TabFocusNavigation = KeyboardNavigationMode.Cycle };
    private readonly Grid body = new() { Margin = new Thickness(8, 0, 8, 8) };
    private readonly Button closeButton = new() { Content = "×", MinWidth = 24, MinHeight = 32, Padding = new Thickness(0) };
    private readonly Button backButton = new() { Content = "←", MinWidth = 24, MinHeight = 32, Padding = new Thickness(0) };
    private readonly Button panelsButton = new() { Content = "…", MinWidth = 24, MinHeight = 32, Padding = new Thickness(0) };
    private readonly List<Transition> transitions = [];
    private TemplateRenderer? renderer;
    private string[] menuPanels = [];
    private bool closeRequested, closeObserved;
    private bool focusPending;
    private string? pendingFocusNode;
    private HostAppearancePreferences appearance = new();
    private MaterialSpec? appliedMaterial;
    private readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();
    private bool highContrast;
    private bool highContrastSubscribed;
    private int appearanceQueued;
    internal bool SimulateCloseFailure { get; set; }
    internal Action<Window>? CloseWindowForTesting { get; set; }
    internal FrameworkElement Root => root;
    internal nint Handle { get; private set; }
    internal string TemplateId { get; private set; } = "";
    internal PixelRect LastBounds { get; private set; }
    internal bool IsClosing => closeRequested;

    internal TaskbarFlyoutWindow(Action close, Action back, Action<string> open, Action closed, Action<string, object?> record)
    { this.close = close; this.back = back; this.open = open; this.closed = closed; this.record = record; }

    internal void Initialize()
    {
        window = new Window { Title = "MTP 任务栏面板" };
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Closed += OnClosed;
        root.RowDefinitions.Add(new() { Height = new GridLength(FlyoutGroupLayout.HeaderHeightDip) });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(4, 8, 4, 8) };
        header.Children.Add(backButton); header.Children.Add(panelsButton); header.Children.Add(closeButton);
        root.Children.Add(header); Grid.SetRow(body, 1); root.Children.Add(body);
        closeButton.Click += CloseClicked; backButton.Click += BackClicked;
        root.KeyDown += KeyDown;
        root.ActualThemeChanged += ThemeChanged;
        try
        {
            accessibility.HighContrastChanged += HighContrastChanged;
            highContrastSubscribed = true;
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            record("flyout-high-contrast-notification-unavailable", new { error.HResult,
                fallback = "Read current high contrast during Host refresh" });
        }
        AutomationProperties.SetAutomationId(root, "MtpTaskbarFlyout");
        AutomationProperties.SetName(closeButton, "关闭浮窗组");
        AutomationProperties.SetName(backButton, "返回上一面板");
        AutomationProperties.SetName(panelsButton, "打开关联面板");
        window.Content = root;
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false; presenter.IsMinimizable = false; presenter.IsMaximizable = false;
        }
        FlyoutNative.NoActivate(Handle, true);
        ApplyTheme();
    }
    private void ThemeChanged(FrameworkElement sender, object args) => ApplyTheme();
    private void HighContrastChanged(Windows.UI.ViewManagement.AccessibilitySettings sender, object args)
    {
        if (Interlocked.Exchange(ref appearanceQueued, 1) != 0) return;
        if (!root.DispatcherQueue.TryEnqueue(() => { Interlocked.Exchange(ref appearanceQueued, 0); ApplyTheme(); }))
            Interlocked.Exchange(ref appearanceQueued, 0);
    }
    internal void ApplyAppearance(HostAppearancePreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(value.Theme) || !new MaterialSpec(value.Material, value.Opacity).IsValid)
            throw new ArgumentException("Invalid Host appearance", nameof(value));
        if (appearance == value) return;
        appearance = value;
        root.RequestedTheme = value.Theme switch { HostTheme.Light => ElementTheme.Light, HostTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyTheme();
    }
    internal void RefreshAppearance()
    {
        if (window is null || closeRequested) return;
        try { if (accessibility.HighContrast != highContrast) ApplyTheme(); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
    private void ApplyTheme()
    {
        if (window is null || closeRequested) return;
        try
        {
            try { highContrast = accessibility.HighContrast; } catch (System.Runtime.InteropServices.COMException) { }
            var resolution = MaterialResolver.Resolve(new(appearance.Material, appearance.Opacity), new(true,
                Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported(),
                Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()));
            var effective = highContrast ? new MaterialSpec(MaterialKind.None, 1) : resolution.Effective;
            byte shade = root.ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
            root.Background = highContrast
                ? new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(effective.Kind == MaterialKind.None ? (byte)255 :
                    effective.Kind == MaterialKind.Solid ? (byte)(255 * effective.Opacity) : (byte)0, shade, shade, shade));
            // Theme updates flow through SystemBackdrop's configuration callback; only material/opacity changes replace its controller.
            if (appliedMaterial != effective)
            {
                window.SystemBackdrop = effective.Kind is MaterialKind.Acrylic or MaterialKind.Mica
                    ? new Islands.ConfiguredMaterialBackdrop(effective.Kind, effective.Opacity, record) : null;
                appliedMaterial = effective;
                record("flyout-material-applied", new { requested = appearance.Material.ToString(), effective = effective.Kind.ToString(), effective.Opacity, highContrast });
            }
        }
        catch (Exception error)
        {
            window.SystemBackdrop = null;
            appliedMaterial = new(MaterialKind.None, 1);
            byte shade = root.ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
            root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, shade, shade, shade));
            record("flyout-material-fallback", error.Message);
        }
    }
    internal bool SetRenderer(string templateId, TemplateRenderer next, bool canBack, IEnumerable<string> panelIds)
    {
        backButton.IsEnabled = canBack;
        closeButton.IsEnabled = true;
        panelsButton.IsEnabled = true;
        var nextPanels = panelIds.Take(FlyoutGroupLayout.MaximumPanels).ToArray();
        if (!menuPanels.SequenceEqual(nextPanels, StringComparer.Ordinal))
        {
            var menu = new MenuFlyout();
            foreach (var id in nextPanels)
            {
                var item = new MenuFlyoutItem { Text = id };
                item.Click += (_, _) => { if (!closeRequested) open(id); };
                menu.Items.Add(item);
            }
            panelsButton.Flyout = menu;
            menuPanels = nextPanels;
        }
        panelsButton.Visibility = nextPanels.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        next.Refresh();
        if (ReferenceEquals(renderer, next))
        {
            // A parallel panel can return while its exit animation is still running.
            foreach (var view in body.Children.OfType<ScrollViewer>())
                if (ReferenceEquals(view.Content, next)) view.IsEnabled = true;
            return false;
        }
        renderer = next; TemplateId = templateId;
        ScrollViewer? incoming = null;
        foreach (var child in body.Children.OfType<ScrollViewer>())
            if (ReferenceEquals(child.Content, next)) incoming = child;
        if (incoming is null)
        {
            if (next.Parent is ScrollViewer previous) previous.Content = null;
            incoming = new ScrollViewer { Content = next, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Opacity = 0, RenderTransform = new TranslateTransform { X = 8 } };
            body.Children.Add(incoming);
        }
        transitions.Clear();
        foreach (var child in body.Children.OfType<ScrollViewer>())
        {
            bool current = ReferenceEquals(child, incoming);
            child.IsHitTestVisible = current;
            child.IsTabStop = current;
            child.IsEnabled = current;
            transitions.Add(new(child, child.Opacity, (child.RenderTransform as TranslateTransform)?.X ?? 0, current));
        }
        return true;
    }
    internal void Apply(FlyoutRectangleTarget frame, double progress, uint dpi, bool closing)
    {
        if (window is null || closeRequested || !FlyoutNative.IsWindow(Handle)) return;
        var pixels = FlyoutNative.ToPixels(frame.Bounds, dpi);
        FlyoutNative.Position(Handle, pixels);
        FlyoutNative.Opacity(Handle, frame.Opacity);
        LastBounds = pixels;
        root.Width = pixels.Width * 96d / dpi; root.Height = pixels.Height * 96d / dpi;
        root.Opacity = frame.Opacity;
        root.IsHitTestVisible = !closing && frame.Opacity > 0;
        foreach (var transition in transitions)
        {
            transition.View.Opacity = transition.Opacity + ((transition.Current ? 1 : 0) - transition.Opacity) * progress;
            transition.View.RenderTransform = new TranslateTransform { X = transition.X + ((transition.Current ? 0 : -8) - transition.X) * progress };
        }
        if (progress >= 1)
        {
            foreach (var transition in transitions.Where(x => !x.Current)) { transition.View.Content = null; body.Children.Remove(transition.View); }
            transitions.Clear();
        }
        window.AppWindow.Show(false);
    }
    internal void RetargetContent()
    {
        for (int i = 0; i < transitions.Count; i++)
        {
            var value = transitions[i];
            transitions[i] = value with { Opacity = value.View.Opacity, X = (value.View.RenderTransform as TranslateTransform)?.X ?? 0 };
        }
    }
    internal void EnterKeyboard(string? nodeId = null)
    {
        if (window is null || closeRequested) return;
        FlyoutNative.NoActivate(Handle, false);
        window.Activate();
        pendingFocusNode = nodeId;
        if (!focusPending) { focusPending = true; root.LayoutUpdated += FocusAfterLayout; }
        root.UpdateLayout();
        FocusAfterLayout(null, null!);
    }
    private void FocusAfterLayout(object? sender, object args)
    {
        if (!focusPending || closeRequested || !root.IsLoaded || renderer is { IsLoaded: false }) return;
        bool focused = pendingFocusNode is not null && renderer?.FocusNode(pendingFocusNode) == true;
        focused |= !focused && renderer?.FocusFirst() == true;
        if (!focused) focused = closeButton.Focus(FocusState.Keyboard);
        if (!focused) return;
        focusPending = false; pendingFocusNode = null; root.LayoutUpdated -= FocusAfterLayout;
    }
    internal void DisableInput()
    {
        root.IsHitTestVisible = false;
        focusPending = false; pendingFocusNode = null; root.LayoutUpdated -= FocusAfterLayout;
        backButton.IsEnabled = false; panelsButton.IsEnabled = false; closeButton.IsEnabled = false;
        foreach (var view in body.Children.OfType<ScrollViewer>()) view.IsEnabled = false;
    }
    internal void DetachRenderer(TemplateRenderer value)
    {
        foreach (var child in body.Children.OfType<ScrollViewer>().Where(x => ReferenceEquals(x.Content, value)).ToArray())
        { child.Content = null; body.Children.Remove(child); transitions.RemoveAll(x => ReferenceEquals(x.View, child)); }
        if (ReferenceEquals(renderer, value)) renderer = null;
    }
    internal CoreResult<bool> TryClose()
    {
        if (SimulateCloseFailure) return CoreResult<bool>.Failure(new("FlyoutCleanupPending", "注入的窗口关闭失败"));
        if (window is null || Handle == 0 || !FlyoutNative.IsWindow(Handle)) { ReleaseContent(); return CoreResult<bool>.Success(true); }
        try
        {
            if (!closeRequested)
            {
                closeRequested = true; DisableInput();
                if (CloseWindowForTesting is { } closeWindow) closeWindow(window); else window.Close();
            }
            if (FlyoutNative.IsWindow(Handle)) return CoreResult<bool>.Failure(new("FlyoutCleanupPending", "窗口尚未确认销毁"));
            ReleaseContent(); return CoreResult<bool>.Success(true);
        }
        catch (Exception error)
        {
            // A throwing Close call is not an accepted close request. Keep the owner and allow
            // the next explicit cleanup attempt to call the native boundary again.
            if (!closeObserved) closeRequested = false;
            return CoreResult<bool>.Failure(new("FlyoutCleanupPending",
                string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message));
        }
    }
    private void ReleaseContent()
    {
        if (window is not null) window.Closed -= OnClosed;
        foreach (var view in body.Children.OfType<ScrollViewer>()) view.Content = null;
        body.Children.Clear(); transitions.Clear(); renderer = null;
        root.KeyDown -= KeyDown; root.ActualThemeChanged -= ThemeChanged;
        if (highContrastSubscribed)
        {
            highContrastSubscribed = false;
            try { accessibility.HighContrastChanged -= HighContrastChanged; }
            catch (System.Runtime.InteropServices.COMException error)
            { record("flyout-high-contrast-notification-release-unavailable", new { error.HResult }); }
        }
        root.LayoutUpdated -= FocusAfterLayout; focusPending = false; pendingFocusNode = null;
        closeButton.Click -= CloseClicked; backButton.Click -= BackClicked;
        panelsButton.Flyout = null; window = null; Handle = 0;
    }
    private void OnClosed(object sender, WindowEventArgs args) { closeObserved = true; closed(); }
    private void CloseClicked(object sender, RoutedEventArgs args) => close();
    private void BackClicked(object sender, RoutedEventArgs args) => back();
    private void KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || closeObserved) return;
        if (args.Key == VirtualKey.Escape) { args.Handled = true; close(); }
    }
    internal static double Measure(TemplateRenderer view, double width)
    {
        view.Refresh(); view.Measure(new Size(Math.Max(1, width - 16), double.PositiveInfinity));
        return Math.Max(FlyoutGroupLayout.MinimumHeightDip, Math.Min(1e9, view.DesiredSize.Height + FlyoutGroupLayout.HeaderHeightDip + 8));
    }
    private sealed record Transition(ScrollViewer View, double Opacity, double X, bool Current);
}

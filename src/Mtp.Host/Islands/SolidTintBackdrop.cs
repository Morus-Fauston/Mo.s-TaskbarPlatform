using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mtp.Host.Islands;

/// <summary>Composition tint and post-connection surface initialization for ordinary top-level Windows.</summary>
internal sealed class SolidTintBackdrop(Window? window, Color tint, Action<string, object?> record) : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? sharedCompositor;
    private static Windows.UI.Composition.Compositor SharedCompositor => sharedCompositor ??= new();
    private Windows.UI.Composition.CompositionColorBrush? brush;
    private ICompositionSupportsSystemBackdrop? connected;
    private FrameworkElement? fallbackContent;
    private BackdropThemeSubscription? theme;
    private bool opaqueFallback;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // We own configuration subscriptions. Base initialization creates WinUI's
        // per-target default tracker, whose theme callback can outlive its target (#11535).
        connected = target;
        fallbackContent = root.Content as FrameworkElement;
        try
        {
            WindowSurfaceAdapter.EnsureCompositionQueue();
            brush = SharedCompositor.CreateColorBrush(tint);
            target.SystemBackdrop = brush;
            theme = new BackdropThemeSubscription(root, UpdateTint, record);
            UpdateTint();
            if (window is null)
            {
                Record("solid-island-tint-connected", new { state = "composition tint connected; visual-pending-human", tint = tint.ToString() });
                return; // Content islands use their composition target; top-level GDI does not apply.
            }
            // The brush must be connected before the GDI initialization, including first show.
            if (!window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!ReferenceEquals(connected, target)) return;
                try { Record("solid-surface-ready", new { state = WindowSurfaceAdapter.PrepareAndPaint(WinRT.Interop.WindowNative.GetWindowHandle(window)), tint = tint.ToString() }); }
                catch (Exception error) { Fallback(error); }
            })) Fallback(new InvalidOperationException("窗口表面初始化无法排队"));
        }
        catch (Exception error) { Fallback(error); }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // A framework notification may have a null or retired target. Never forward
        // it to the native default tracker; refresh only our still-connected root.
        theme?.Refresh();
    }

    private void UpdateTint()
    {
        if (brush is null || theme is null) return;
        if (theme.Configuration.IsHighContrast && theme.Configuration.HighContrastBackgroundColor is { } contrast) { brush.Color = contrast; return; }
        byte shade = theme.Configuration.Theme == Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark ? (byte)32 : (byte)243;
        brush.Color = Color.FromArgb(opaqueFallback ? (byte)255 : tint.A, shade, shade, shade);
    }

    private void Fallback(Exception error)
    {
        opaqueFallback = true;
        var opaque = tint; opaque.A = 255;
        if (brush is not null) brush.Color = opaque;
        else if (fallbackContent is Microsoft.UI.Xaml.Controls.Panel panel) panel.Background = new SolidColorBrush(opaque);
        else if (fallbackContent is Microsoft.UI.Xaml.Controls.Control control) control.Background = new SolidColorBrush(opaque);
        Record("solid-surface-fallback", new { state = "不透明回退：" + error.ToString(), requestedTint = tint.ToString() });
    }
    private void Record(string kind, object? value) { try { record(kind, value); } catch (Exception) { } }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        connected = null;
        theme?.Dispose(); theme = null;
        fallbackContent = null;
        try { target.SystemBackdrop = null; }
        finally { brush?.Dispose(); brush = null; }
    }
}

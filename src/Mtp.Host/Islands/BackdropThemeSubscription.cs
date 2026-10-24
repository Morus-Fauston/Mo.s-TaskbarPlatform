using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace Mtp.Host.Islands;

/// <summary>Owns theme notifications for one connected target, including queued-event cancellation.</summary>
internal sealed class BackdropThemeSubscription : IDisposable
{
    private readonly FrameworkElement content;
    private readonly DispatcherQueue dispatcher;
    private readonly AccessibilitySettings accessibility = new();
    private readonly UISettings colors = new();
    private readonly Action changed;
    private readonly Action<string, object?> record;
    private bool disposed;
    internal SystemBackdropConfiguration Configuration { get; private set; } = new() { IsInputActive = true };

    internal BackdropThemeSubscription(XamlRoot root, Action changed, Action<string, object?> record)
    {
        content = root.Content as FrameworkElement ?? throw new InvalidOperationException("Backdrop requires a framework root.");
        dispatcher = content.DispatcherQueue;
        this.changed = changed;
        this.record = record;
        UpdateConfiguration();
        content.ActualThemeChanged += ThemeChanged;
        try
        {
            // UISettings supports unpackaged desktop windows; the UWP
            // AccessibilitySettings.HighContrastChanged can return E_NOTFOUND.
            colors.ColorValuesChanged += ColorsChanged;
        }
        catch { content.ActualThemeChanged -= ThemeChanged; throw; }
    }

    internal void Refresh()
    {
        if (disposed) return;
        try { UpdateConfiguration(); }
        catch (System.Runtime.InteropServices.COMException error)
        {
            // A failed OS policy read must not escape a XAML event delegate.
            // Keep the last complete configuration and expose the failure.
            try { record("material-theme-policy-read-failed", new { error = error.ToString(), retainedTheme = Configuration.Theme.ToString() }); }
            catch (Exception) { }
            return;
        }
        changed();
    }

    private void UpdateConfiguration()
    {
        // Publish a complete configuration snapshot. Rebinding a native controller
        // avoids mixing its old customized palette with a mutable theme notification.
        Configuration = new()
        {
            IsInputActive = true,
            Theme = content.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            IsHighContrast = accessibility.HighContrast,
            HighContrastBackgroundColor = colors.GetColorValue(UIColorType.Background),
        };
    }

    private void ThemeChanged(FrameworkElement sender, object args) => Refresh();
    private void ColorsChanged(UISettings sender, object args) => dispatcher.TryEnqueue(Refresh);

    public void Dispose()
    {
        disposed = true;
        content.ActualThemeChanged -= ThemeChanged;
        colors.ColorValuesChanged -= ColorsChanged;
    }
}

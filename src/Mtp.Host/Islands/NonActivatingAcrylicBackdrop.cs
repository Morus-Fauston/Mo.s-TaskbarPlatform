using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mtp.Host.Islands;

// One instance per island/window. Visual activity must not require foreground activation.
internal sealed class NonActivatingAcrylicBackdrop(Action<string, object?> record) : SystemBackdrop
{
    private DesktopAcrylicController? controller;
    private BackdropThemeSubscription? theme;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        controller = new DesktopAcrylicController();
        try
        {
            theme = new BackdropThemeSubscription(root, ApplyConfiguration, record);
            ApplyConfiguration();
            if (!controller.AddSystemBackdropTarget(target))
                throw new InvalidOperationException("Desktop Acrylic rejected the island target.");
            controller.StateChanged += OnStateChanged;
            RecordState();
        }
        catch
        {
            theme?.Dispose(); theme = null;
            controller.Dispose();
            controller = null;
            throw;
        }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        theme?.Refresh();
    }

    private void ApplyConfiguration()
    {
        if (controller is null || theme is null) return;
        var configuration = theme.Configuration;
        var foreground = NativeWindows.GetForegroundWindow();
        controller!.SetSystemBackdropConfiguration(configuration);
        record("acrylic-input-policy", new
        {
            materialActive = configuration.IsInputActive,
            configuration.IsHighContrast,
            theme = configuration.Theme.ToString(),
            focusChanged = NativeWindows.GetForegroundWindow() != foreground,
        });
    }

    private void OnStateChanged(ISystemBackdropController sender, object args) => RecordState();
    private void RecordState() { if (controller is not null) record("acrylic-controller-state", new { state = controller.State.ToString(), appearance = "pending-human" }); }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        theme?.Dispose(); theme = null;
        if (controller != null)
        {
            controller.StateChanged -= OnStateChanged;
            try { controller.RemoveSystemBackdropTarget(target); }
            finally { controller.Dispose(); controller = null; }
        }
    }
}


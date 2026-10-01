using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TaskbarIslandLab.Windows;

// One instance per island/window. Visual activity must not require foreground activation.
internal sealed class NonActivatingAcrylicBackdrop(Action<string, object?> record) : SystemBackdrop
{
    private DesktopAcrylicController? controller;
    private readonly SystemBackdropConfiguration configuration = new() { IsInputActive = true };

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        controller = new DesktopAcrylicController();
        try
        {
            ApplyConfiguration(target, root);
            if (!controller.AddSystemBackdropTarget(target))
                throw new InvalidOperationException("Desktop Acrylic rejected the island target.");
            controller.StateChanged += OnStateChanged;
            RecordState();
        }
        catch
        {
            controller.Dispose();
            controller = null;
            throw;
        }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, root);
        if (controller != null) ApplyConfiguration(target, root);
    }

    private void ApplyConfiguration(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        var defaults = GetDefaultSystemBackdropConfiguration(target, root);
        var foreground = NativeWindows.GetForegroundWindow();
        configuration.Theme = defaults.Theme;
        configuration.IsHighContrast = defaults.IsHighContrast;
        configuration.HighContrastBackgroundColor = defaults.HighContrastBackgroundColor;
        controller!.SetSystemBackdropConfiguration(configuration);
        record("acrylic-input-policy", new
        {
            materialActive = configuration.IsInputActive,
            defaultInputActive = defaults.IsInputActive,
            configuration.IsHighContrast,
            theme = configuration.Theme.ToString(),
            focusChanged = NativeWindows.GetForegroundWindow() != foreground,
        });
    }

    private void OnStateChanged(ISystemBackdropController sender, object args) => RecordState();
    private void RecordState() => record("acrylic-controller-state", new { state = controller!.State.ToString(), appearance = "pending-human" });

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        if (controller != null)
        {
            controller.StateChanged -= OnStateChanged;
            try { controller.RemoveSystemBackdropTarget(target); }
            finally { controller.Dispose(); controller = null; }
        }
        base.OnTargetDisconnected(target);
    }
}

using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Mtp.Platform.Core;

namespace Mtp.Host.Islands;

/// <summary>Owns a single native backdrop controller and its controlled material value.</summary>
internal sealed class ConfiguredMaterialBackdrop(MaterialKind kind, double opacity, Action<string, object?> record) : SystemBackdrop
{
    private DesktopAcrylicController? acrylic;
    private MicaController? mica;
    private readonly SystemBackdropConfiguration configuration = new() { IsInputActive = true };
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        try
        {
            if (kind == MaterialKind.Acrylic)
            {
                acrylic = new() { TintOpacity = (float)opacity };
                Configure(target, root);
                if (!acrylic.AddSystemBackdropTarget(target)) throw new InvalidOperationException("Acrylic目标不可用");
                acrylic.StateChanged += StateChanged;
            }
            else
            {
                mica = new() { LuminosityOpacity = (float)opacity };
                Configure(target, root);
                if (!mica.AddSystemBackdropTarget(target)) throw new InvalidOperationException("Mica目标不可用");
                mica.StateChanged += StateChanged;
            }
        }
        catch { Release(); throw; }
    }
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, root);
        if (acrylic is not null || mica is not null) Configure(target, root);
    }
    private void Configure(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        var defaults = GetDefaultSystemBackdropConfiguration(target, root);
        configuration.Theme = defaults.Theme;
        configuration.IsHighContrast = defaults.IsHighContrast;
        configuration.HighContrastBackgroundColor = defaults.HighContrastBackgroundColor;
        acrylic?.SetSystemBackdropConfiguration(configuration);
        mica?.SetSystemBackdropConfiguration(configuration);
    }
    private void StateChanged(ISystemBackdropController sender, object args) =>
        record("production-material-state", new { requested = kind.ToString(), state = acrylic?.State.ToString() ?? mica?.State.ToString() ?? "Closed" });
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        try { acrylic?.RemoveSystemBackdropTarget(target); mica?.RemoveSystemBackdropTarget(target); }
        finally { Release(); base.OnTargetDisconnected(target); }
    }
    private void Release()
    {
        if (acrylic is not null) { acrylic.StateChanged -= StateChanged; acrylic.Dispose(); acrylic = null; }
        if (mica is not null) { mica.StateChanged -= StateChanged; mica.Dispose(); mica = null; }
    }
}

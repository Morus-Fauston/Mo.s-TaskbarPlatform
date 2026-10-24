using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Mtp.Platform.Core;

namespace Mtp.Host.Islands;

/// <summary>Owns the current native controller; theme changes retain the window and content.</summary>
internal sealed class ConfiguredMaterialBackdrop(MaterialKind kind, double opacity, Action<string, object?> record) : SystemBackdrop
{
    private DesktopAcrylicController? acrylic;
    private MicaController? mica;
    private ICompositionSupportsSystemBackdrop? connected;
    private BackdropThemeSubscription? theme;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // Own subscriptions instead of WinUI's default per-target tracker (#11535).
        connected = target;
        try { theme = new BackdropThemeSubscription(root, Configure, record); Configure(); }
        catch { Release(); throw; }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root) => theme?.Refresh();

    private void Configure()
    {
        if (theme is null || connected is null) return;
        var configuration = theme.Configuration;
        // Custom strength freezes native automatic colors. A fresh controller obtains
        // the native palette for this theme, without hardcoding a competing palette.
        // Only the material controller changes; the HWND, island and controls persist.
        ReleaseControllers();
        try
        {
            if (kind == MaterialKind.Acrylic)
            {
                acrylic = new();
                if (!acrylic.AddSystemBackdropTarget(connected)) throw new InvalidOperationException("Acrylic目标不可用");
                acrylic.SetSystemBackdropConfiguration(configuration);
                acrylic.StateChanged += StateChanged;
            }
            else
            {
                mica = new();
                if (!mica.AddSystemBackdropTarget(connected)) throw new InvalidOperationException("Mica目标不可用");
                mica.SetSystemBackdropConfiguration(configuration);
                mica.StateChanged += StateChanged;
            }
            var currentAcrylic = acrylic; var currentMica = mica;
            if (!DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (theme is null || !ReferenceEquals(acrylic, currentAcrylic) || !ReferenceEquals(mica, currentMica)) return;
                if (acrylic is not null) acrylic.TintOpacity = (float)opacity;
                if (mica is not null) mica.LuminosityOpacity = (float)opacity;
                record("production-material-configuration", new
                {
                    requested = kind.ToString(), requestedOpacity = opacity,
                    tintOpacity = acrylic?.TintOpacity, luminosityOpacity = acrylic?.LuminosityOpacity ?? mica?.LuminosityOpacity,
                    tintColor = acrylic?.TintColor.ToString() ?? mica?.TintColor.ToString(),
                    theme = configuration.Theme.ToString(), configuration.IsHighContrast, configuration.IsInputActive,
                    appearance = "pending-human",
                });
            })) throw new InvalidOperationException("材质强度初始化无法排队");
        }
        catch { ReleaseControllers(); throw; }
    }

    private void StateChanged(ISystemBackdropController sender, object args)
    {
        try { record("production-material-state", new { requested = kind.ToString(), requestedOpacity = opacity, state = acrylic?.State.ToString() ?? mica?.State.ToString() ?? "Closed", tintOpacity = acrylic?.TintOpacity, appearance = "pending-human" }); }
        catch (Exception) { }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target) => Release();

    private void Release()
    {
        theme?.Dispose(); theme = null;
        try { ReleaseControllers(); }
        finally { connected = null; }
    }

    private void ReleaseControllers()
    {
        if (acrylic is not null)
        {
            var previous = acrylic; acrylic = null;
            previous.StateChanged -= StateChanged;
            try { if (connected is not null) previous.RemoveSystemBackdropTarget(connected); }
            finally { previous.Dispose(); }
        }
        if (mica is not null)
        {
            var previous = mica; mica = null;
            previous.StateChanged -= StateChanged;
            try { if (connected is not null) previous.RemoveSystemBackdropTarget(connected); }
            finally { previous.Dispose(); }
        }
    }
}

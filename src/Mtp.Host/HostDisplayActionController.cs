using System;
using System.Collections.Generic;
using System.Linq;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// Result of one complete Host display action, including the persisted preference and presentation errors.
/// </summary>
public sealed class HostDisplayActionResult
{
    public HostDisplayActionResult(
        HostComponentDisplayModel? component,
        IndependentDockWindowState dockWindow,
        bool preferenceChanged,
        IEnumerable<StructuredError>? errors = null)
    {
        Component = component;
        DockWindow = dockWindow ?? throw new ArgumentNullException(nameof(dockWindow));
        PreferenceChanged = preferenceChanged;
        Errors = Array.AsReadOnly((errors ?? Enumerable.Empty<StructuredError>()).ToArray());
    }

    public HostComponentDisplayModel? Component { get; }

    public IndependentDockWindowState DockWindow { get; }

    public bool PreferenceChanged { get; }

    public IReadOnlyList<StructuredError> Errors { get; }

    public bool IsSuccess => Errors.Count == 0;
}

/// <summary>
/// Coordinates a user's display intent with preference persistence and the active Host presentation.
/// </summary>
public sealed class HostDisplayActionController : IDisposable
{
    private readonly HostDisplayController displayController;
    private readonly IndependentDockWindowController dockWindowController;
    private readonly ExplorerTaskbarProbeController probeController;
    private readonly bool preferEmbedded;

    public HostDisplayActionController(
        HostDisplayController displayController,
        IndependentDockWindowController dockWindowController,
        ExplorerTaskbarProbeController probeController,
        bool preferEmbedded = false)
    {
        this.displayController = displayController ?? throw new ArgumentNullException(nameof(displayController));
        this.dockWindowController = dockWindowController ?? throw new ArgumentNullException(nameof(dockWindowController));
        this.probeController = probeController ?? throw new ArgumentNullException(nameof(probeController));
        this.preferEmbedded = preferEmbedded;
        this.probeController.StateChanged += ProbeController_StateChanged;
    }

    public event EventHandler? ProbeStateChanged;

    public ExplorerTaskbarProbeState ProbeState => probeController.State;

    public HostDisplayActionResult RestoreCurrent()
    {
        var component = displayController.CurrentComponents.FirstOrDefault(item => item.IsVisible);
        return component is not null
            ? ApplyPresentation(component, preferenceChanged: false)
            : new HostDisplayActionResult(
                displayController.CurrentComponents.FirstOrDefault(),
                dockWindowController.State,
                preferenceChanged: false);
    }

    public HostDisplayActionResult SetVisibility(StableIdentity identity, bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var preferenceResult = displayController.SetVisibility(identity, isVisible);
        if (!preferenceResult.IsSuccess)
        {
            var current = displayController.CurrentComponents.FirstOrDefault(component => component.Identity == identity);
            return new HostDisplayActionResult(
                current,
                dockWindowController.State,
                preferenceChanged: false,
                new[] { preferenceResult.Error! });
        }

        return ApplyPresentation(preferenceResult.Value!, preferenceChanged: true);
    }

    public ExplorerTaskbarProbeOutcome RunProbe(
        HostComponentDisplayModel component,
        ExplorerTaskbarProbeRequest request) => probeController.Run(component, request);

    public CoreResult<bool> DetachProbe() => probeController.Detach();

    public CoreResult<bool> RefreshPresentation() => probeController.Refresh();

    public HostDisplayActionResult Shutdown()
    {
        var errors = new List<StructuredError>();
        var detachResult = probeController.Shutdown();
        if (!detachResult.IsSuccess)
        {
            errors.Add(detachResult.Error!);
        }

        var closeResult = dockWindowController.Close();
        if (!closeResult.IsSuccess)
        {
            errors.Add(closeResult.Error!);
        }

        return new HostDisplayActionResult(
            displayController.CurrentComponents.FirstOrDefault(),
            dockWindowController.State,
            preferenceChanged: false,
            errors);
    }

    public void Dispose()
    {
        probeController.StateChanged -= ProbeController_StateChanged;
        probeController.Dispose();
        dockWindowController.Dispose();
    }

    private void ProbeController_StateChanged(object? sender, EventArgs args) =>
        ProbeStateChanged?.Invoke(this, EventArgs.Empty);

    private HostDisplayActionResult ApplyPresentation(
        HostComponentDisplayModel component,
        bool preferenceChanged)
    {
        var errors = new List<StructuredError>();
        if (component.IsVisible)
        {
            if (preferEmbedded && !probeController.State.IsEmbedded)
            {
                // Keep the embedded surface visibly translucent over the taskbar instead of
                // turning the dark solid brush into an opaque-looking gray block.
                var embedResult = probeController.Run(component, new ExplorerTaskbarProbeRequest(
                    false, ProbeTransparencyMode.SolidPaint, MaterialKind.Solid, 0.1, 0xFFFFFF));
                if (!embedResult.FallbackShown && !embedResult.Succeeded)
                {
                    errors.Add(embedResult.Error!);
                }
            }
            else if (!preferEmbedded)
            {
                var showResult = dockWindowController.Show(component);
                if (!showResult.IsSuccess) errors.Add(showResult.Error!);
            }
        }
        else
        {
            var detachResult = probeController.Shutdown();
            if (!detachResult.IsSuccess)
            {
                errors.Add(detachResult.Error!);
            }

            var closeResult = dockWindowController.Close();
            if (!closeResult.IsSuccess)
            {
                errors.Add(closeResult.Error!);
            }
        }

        return new HostDisplayActionResult(component, dockWindowController.State, preferenceChanged, errors);
    }
}

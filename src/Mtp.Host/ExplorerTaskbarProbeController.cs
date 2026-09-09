using System;
using System.Linq;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// The Host-owned boundary for the experimental Explorer taskbar embed probe.
/// Explorer window classes, window trees and reparenting stay behind this interface.
/// </summary>
public interface IExplorerTaskbarEmbedAdapter
{
    /// <summary>Raised only when the embedded window disappears without a Host request.</summary>
    event EventHandler? Lost;

    bool IsEmbedded { get; }

    CoreResult<ExplorerTaskbarProbeReport> TryEmbed(HostComponentDisplayModel component, ExplorerTaskbarProbeRequest request);

    CoreResult<bool> Detach();
}

public sealed record ExplorerTaskbarProbeState(
    bool IsEmbedded,
    HostComponentDisplayModel? Component,
    ExplorerTaskbarProbeReport? Report,
    StructuredError? Error);

/// <summary>
/// The result of one probe run, including whether the independent dock window took over.
/// </summary>
public sealed record ExplorerTaskbarProbeOutcome(
    bool Succeeded,
    ExplorerTaskbarProbeReport? Report,
    StructuredError? Error,
    bool FallbackShown,
    StructuredError? FallbackError)
{
    public const string FallbackMessage = "任务栏嵌入当前不可用，已切换独立贴靠";
}

/// <summary>
/// Runs the embed probe against the current validated component and always keeps the
/// independent dock window as the fallback. It never reads or writes display preferences.
/// </summary>
public sealed class ExplorerTaskbarProbeController : IDisposable
{
    private readonly HostDisplayController displayController;
    private readonly IExplorerTaskbarEmbedAdapter adapter;
    private readonly IndependentDockWindowController dockFallback;

    public ExplorerTaskbarProbeController(
        HostDisplayController displayController,
        IExplorerTaskbarEmbedAdapter adapter,
        IndependentDockWindowController dockFallback)
    {
        this.displayController = displayController ?? throw new ArgumentNullException(nameof(displayController));
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.dockFallback = dockFallback ?? throw new ArgumentNullException(nameof(dockFallback));
        this.adapter.Lost += Adapter_Lost;
        State = new ExplorerTaskbarProbeState(false, null, null, null);
    }

    public event EventHandler? StateChanged;

    public ExplorerTaskbarProbeState State { get; private set; }

    public ExplorerTaskbarProbeOutcome RunCurrent(ExplorerTaskbarProbeRequest request)
    {
        var component = displayController.CurrentComponents.FirstOrDefault(item => item.IsVisible);
        return component is null
            ? Rejected(new StructuredError("explorer_probe_component_not_visible", "No visible validated component is available for the probe."))
            : Run(component, request);
    }

    public ExplorerTaskbarProbeOutcome Run(HostComponentDisplayModel component, ExplorerTaskbarProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(request);

        var declaredComponent = displayController.CurrentComponents
            .FirstOrDefault(item => item.Identity == component.Identity);
        if (declaredComponent is null)
        {
            return Rejected(new StructuredError("explorer_probe_component_not_declared", "Only a component from the current validated declaration can be probed.", component.Identity.ToString()));
        }

        if (!declaredComponent.IsVisible)
        {
            return Rejected(new StructuredError("explorer_probe_component_not_visible", "A hidden component cannot be probed.", declaredComponent.Identity.ToString()));
        }

        if (adapter.IsEmbedded)
        {
            _ = adapter.Detach();
        }

        var embedResult = adapter.TryEmbed(declaredComponent, request);
        if (embedResult.IsSuccess)
        {
            var closeResult = dockFallback.Close();
            State = new ExplorerTaskbarProbeState(true, declaredComponent, embedResult.Value, closeResult.IsSuccess ? null : closeResult.Error);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return new ExplorerTaskbarProbeOutcome(true, embedResult.Value, null, false, null);
        }

        var fallbackResult = dockFallback.Show(declaredComponent);
        State = new ExplorerTaskbarProbeState(false, null, null, embedResult.Error);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new ExplorerTaskbarProbeOutcome(
            false,
            null,
            embedResult.Error,
            fallbackResult.IsSuccess,
            fallbackResult.IsSuccess ? null : fallbackResult.Error);
    }

    public CoreResult<bool> Detach()
    {
        var component = State.Component;
        var detachResult = adapter.Detach();
        if (!detachResult.IsSuccess)
        {
            State = State with { Error = detachResult.Error };
            StateChanged?.Invoke(this, EventArgs.Empty);
            return detachResult;
        }

        State = new ExplorerTaskbarProbeState(false, null, null, null);
        RestoreDockWindow(component);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return detachResult;
    }

    public void Dispose()
    {
        adapter.Lost -= Adapter_Lost;
    }

    private void Adapter_Lost(object? sender, EventArgs args)
    {
        var component = State.Component;
        State = new ExplorerTaskbarProbeState(
            false,
            null,
            null,
            new StructuredError("explorer_probe_host_window_lost", "The embedded probe window disappeared without a Host request."));
        RestoreDockWindow(component);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreDockWindow(HostComponentDisplayModel? component)
    {
        if (component is null)
        {
            return;
        }

        var current = displayController.CurrentComponents
            .FirstOrDefault(item => item.Identity == component.Identity);
        if (current is { IsVisible: true })
        {
            var showResult = dockFallback.Show(current);
            if (!showResult.IsSuccess)
            {
                State = State with { Error = showResult.Error };
            }
        }
    }

    private ExplorerTaskbarProbeOutcome Rejected(StructuredError error)
    {
        State = State with { Error = error };
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new ExplorerTaskbarProbeOutcome(false, null, error, false, null);
    }
}

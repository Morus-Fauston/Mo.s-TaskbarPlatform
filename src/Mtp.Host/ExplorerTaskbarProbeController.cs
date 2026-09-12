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

    /// <summary>Raised whenever requested cleanup reaches a confirmed closed state, before or after the caller returns.</summary>
    event EventHandler? Detached;

    ExplorerTaskbarProbeLifecycle Lifecycle { get; }

    CoreResult<ExplorerTaskbarProbeReport> TryEmbed(HostComponentDisplayModel component, ExplorerTaskbarProbeRequest request);

    CoreResult<bool> Detach();

    CoreResult<bool> Refresh();
}

public enum ExplorerTaskbarProbeLifecycle
{
    Detached,
    Embedded,
    CleanupPending,
}

public sealed record ExplorerTaskbarProbeState(
    ExplorerTaskbarProbeLifecycle Lifecycle,
    HostComponentDisplayModel? Component,
    ExplorerTaskbarProbeReport? Report,
    StructuredError? Error,
    StructuredError? FallbackError)
{
    public bool IsEmbedded => Lifecycle == ExplorerTaskbarProbeLifecycle.Embedded;

    public bool IsRecoveryPending =>
        Lifecycle == ExplorerTaskbarProbeLifecycle.CleanupPending || FallbackError is not null;
}

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

internal static class ExplorerTaskbarEmbedFailure
{
    public static bool IsBindingFailure(StructuredError? error) => error?.Code is
        "explorer_probe_taskbar_not_found" or
        "explorer_probe_window_create_failed" or
        "explorer_probe_style_change_failed" or
        "explorer_probe_set_parent_failed" or
        "explorer_probe_parent_mismatch" or
        "explorer_probe_parent_lost" or
        "explorer_probe_window_lost";
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
    private bool adapterOperationInProgress;
    private PendingDetachIntent pendingDetachIntent;
    private ExplorerTaskbarProbeRequest? lastRequest;

    public ExplorerTaskbarProbeController(
        HostDisplayController displayController,
        IExplorerTaskbarEmbedAdapter adapter,
        IndependentDockWindowController dockFallback)
    {
        this.displayController = displayController ?? throw new ArgumentNullException(nameof(displayController));
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.dockFallback = dockFallback ?? throw new ArgumentNullException(nameof(dockFallback));
        this.adapter.Lost += Adapter_Lost;
        this.adapter.Detached += Adapter_Detached;
        State = new ExplorerTaskbarProbeState(ExplorerTaskbarProbeLifecycle.Detached, null, null, null, null);
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

        if (adapter.Lifecycle != ExplorerTaskbarProbeLifecycle.Detached)
        {
            pendingDetachIntent = PendingDetachIntent.RestoreDock;
            var detachResult = ExecuteAdapterOperation(adapter.Detach);
            if (!detachResult.IsSuccess)
            {
                State = new ExplorerTaskbarProbeState(
                    adapter.Lifecycle,
                    declaredComponent,
                    State.Report,
                    detachResult.Error,
                    null);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return new ExplorerTaskbarProbeOutcome(false, null, detachResult.Error, false, null);
            }

            pendingDetachIntent = PendingDetachIntent.None;
        }

        pendingDetachIntent = PendingDetachIntent.RestoreDock;
        var embedResult = ExecuteAdapterOperation(() => adapter.TryEmbed(declaredComponent, request));
        if (embedResult.IsSuccess)
        {
            lastRequest = request;
            pendingDetachIntent = PendingDetachIntent.None;
            var closeResult = dockFallback.Close();
            State = new ExplorerTaskbarProbeState(
                ExplorerTaskbarProbeLifecycle.Embedded,
                declaredComponent,
                embedResult.Value,
                closeResult.IsSuccess ? null : closeResult.Error,
                null);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return new ExplorerTaskbarProbeOutcome(
                closeResult.IsSuccess,
                embedResult.Value,
                closeResult.IsSuccess ? null : closeResult.Error,
                false,
                null);
        }

        if (!ExplorerTaskbarEmbedFailure.IsBindingFailure(embedResult.Error))
        {
            State = new ExplorerTaskbarProbeState(adapter.Lifecycle, declaredComponent, null, embedResult.Error, null);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return new ExplorerTaskbarProbeOutcome(false, null, embedResult.Error, false, null);
        }

        var fallbackResult = dockFallback.Show(declaredComponent);
        if (fallbackResult.IsSuccess || adapter.Lifecycle == ExplorerTaskbarProbeLifecycle.Detached)
        {
            pendingDetachIntent = PendingDetachIntent.None;
        }

        State = new ExplorerTaskbarProbeState(
            adapter.Lifecycle,
            fallbackResult.IsSuccess && adapter.Lifecycle == ExplorerTaskbarProbeLifecycle.Detached ? null : declaredComponent,
            null,
            embedResult.Error,
            fallbackResult.IsSuccess ? null : fallbackResult.Error);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new ExplorerTaskbarProbeOutcome(
            false,
            null,
            embedResult.Error,
            fallbackResult.IsSuccess,
            fallbackResult.IsSuccess ? null : fallbackResult.Error);
    }

    public CoreResult<bool> Detach() => Detach(restoreDockWindow: true);

    public CoreResult<bool> Refresh()
    {
        if (!adapter.Lifecycle.Equals(ExplorerTaskbarProbeLifecycle.Embedded))
            return CoreResult<bool>.Success(true);

        var result = ExecuteAdapterOperation(adapter.Refresh);
        if (!result.IsSuccess && State.Component is { } component && ExplorerTaskbarEmbedFailure.IsBindingFailure(result.Error))
        {
            // A changed Explorer parent is a binding failure, so attempt one fresh bind. The
            // adapter owns cleanup; a failed rebind leaves the independent fallback available.
            var rebound = Run(component, lastRequest ?? new ExplorerTaskbarProbeRequest(false, ProbeTransparencyMode.SolidPaint));
            if (rebound.Succeeded || rebound.FallbackShown)
                return CoreResult<bool>.Success(true);

            State = State with { Error = rebound.Error ?? result.Error };
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return result.IsSuccess ? result : CoreResult<bool>.Failure(result.Error!);
    }

    public CoreResult<bool> Shutdown() => Detach(restoreDockWindow: false);

    private CoreResult<bool> Detach(bool restoreDockWindow)
    {
        var component = State.Component;
        pendingDetachIntent = restoreDockWindow ? PendingDetachIntent.RestoreDock : PendingDetachIntent.Shutdown;
        var detachResult = ExecuteAdapterOperation(adapter.Detach);

        if (!detachResult.IsSuccess)
        {
            State = State with
            {
                Lifecycle = adapter.Lifecycle,
                Component = component,
                Error = detachResult.Error,
            };
            StateChanged?.Invoke(this, EventArgs.Empty);
            return detachResult;
        }

        pendingDetachIntent = PendingDetachIntent.None;
        State = new ExplorerTaskbarProbeState(ExplorerTaskbarProbeLifecycle.Detached, null, null, null, null);
        if (restoreDockWindow)
        {
            var restoreResult = RestoreDockWindow(component);
            if (!restoreResult.IsSuccess)
            {
                State = State with { Component = component, FallbackError = restoreResult.Error };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return restoreResult;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return detachResult;
    }

    public void Dispose()
    {
        adapter.Lost -= Adapter_Lost;
        adapter.Detached -= Adapter_Detached;
    }

    private void Adapter_Lost(object? sender, EventArgs args)
    {
        var component = State.Component;
        State = new ExplorerTaskbarProbeState(
            ExplorerTaskbarProbeLifecycle.Detached,
            null,
            null,
            new StructuredError("explorer_probe_host_window_lost", "The embedded probe window disappeared without a Host request."),
            null);
        var restoreResult = RestoreDockWindow(component);
        if (!restoreResult.IsSuccess)
        {
            State = State with { Component = component, FallbackError = restoreResult.Error };
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Adapter_Detached(object? sender, EventArgs args)
    {
        if (adapterOperationInProgress)
        {
            return;
        }

        var component = State.Component;
        var intent = pendingDetachIntent;
        pendingDetachIntent = PendingDetachIntent.None;
        State = new ExplorerTaskbarProbeState(ExplorerTaskbarProbeLifecycle.Detached, null, null, null, null);
        if (intent == PendingDetachIntent.RestoreDock)
        {
            var restoreResult = RestoreDockWindow(component);
            if (!restoreResult.IsSuccess)
            {
                State = State with { Component = component, FallbackError = restoreResult.Error };
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private CoreResult<T> ExecuteAdapterOperation<T>(Func<CoreResult<T>> operation)
    {
        adapterOperationInProgress = true;
        try
        {
            return operation();
        }
        finally
        {
            adapterOperationInProgress = false;
        }
    }

    private CoreResult<bool> RestoreDockWindow(HostComponentDisplayModel? component)
    {
        if (component is null)
        {
            return CoreResult<bool>.Success(true);
        }

        var current = displayController.CurrentComponents
            .FirstOrDefault(item => item.Identity == component.Identity);
        if (current is { IsVisible: true })
        {
            var showResult = dockFallback.Show(current);
            if (!showResult.IsSuccess)
            {
                return CoreResult<bool>.Failure(showResult.Error!);
            }
        }

        return CoreResult<bool>.Success(true);
    }

    private ExplorerTaskbarProbeOutcome Rejected(StructuredError error)
    {
        State = State with { Error = error };
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new ExplorerTaskbarProbeOutcome(false, null, error, false, null);
    }

    private enum PendingDetachIntent
    {
        None,
        RestoreDock,
        Shutdown,
    }
}

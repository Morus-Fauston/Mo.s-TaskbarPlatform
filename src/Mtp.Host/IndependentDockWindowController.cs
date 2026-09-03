using System;
using System.Linq;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// The Host-owned boundary for an independent top-level dock window.
/// </summary>
public interface IIndependentDockWindowAdapter
{
    event EventHandler? Closed;

    bool IsOpen { get; }

    CoreResult<HostComponentDisplayModel> Show(HostComponentDisplayModel component);

    CoreResult<bool> Close();
}

/// <summary>
/// Tracks independent-window lifecycle without exposing a window object to Core or Contracts.
/// </summary>
public sealed record IndependentDockWindowState(
    bool IsOpen,
    HostComponentDisplayModel? Component,
    StructuredError? Error);

public sealed class IndependentDockWindowController : IDisposable
{
    private readonly HostDisplayController displayController;
    private readonly IIndependentDockWindowAdapter adapter;

    public IndependentDockWindowController(
        HostDisplayController displayController,
        IIndependentDockWindowAdapter adapter)
    {
        this.displayController = displayController ?? throw new ArgumentNullException(nameof(displayController));
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.adapter.Closed += Adapter_Closed;
        State = new IndependentDockWindowState(false, null, null);
    }

    public IndependentDockWindowState State { get; private set; }

    public CoreResult<HostComponentDisplayModel> ShowCurrent()
    {
        var component = displayController.CurrentComponents.FirstOrDefault(item => item.IsVisible);
        return component is null
            ? Failure<HostComponentDisplayModel>(
                new StructuredError("dock_component_not_visible", "No visible validated component is available for the dock window."))
            : Show(component);
    }

    public CoreResult<HostComponentDisplayModel> Show(HostComponentDisplayModel component)
    {
        ArgumentNullException.ThrowIfNull(component);

        var declaredComponent = displayController.CurrentComponents
            .FirstOrDefault(item => item.Identity == component.Identity);
        if (declaredComponent is null)
        {
            return Failure<HostComponentDisplayModel>(
                new StructuredError("dock_component_not_declared", "Only a component from the current validated declaration can be shown in the dock window.", component.Identity.ToString()));
        }

        if (!declaredComponent.IsVisible)
        {
            return Failure<HostComponentDisplayModel>(
                new StructuredError("dock_component_not_visible", "A hidden component cannot be shown in the dock window.", declaredComponent.Identity.ToString()));
        }

        var result = adapter.Show(declaredComponent);
        if (!result.IsSuccess)
        {
            State = adapter.IsOpen
                ? State with { Error = result.Error }
                : new IndependentDockWindowState(false, null, result.Error);
            return result;
        }

        State = new IndependentDockWindowState(true, result.Value, null);
        return result;
    }

    public CoreResult<bool> Close()
    {
        var result = adapter.Close();
        if (!result.IsSuccess)
        {
            State = State with { Error = result.Error };
            return result;
        }

        State = new IndependentDockWindowState(false, null, null);
        return result;
    }

    public void Dispose()
    {
        adapter.Closed -= Adapter_Closed;
    }

    private void Adapter_Closed(object? sender, EventArgs args)
    {
        State = new IndependentDockWindowState(false, null, null);
    }

    private static CoreResult<T> Failure<T>(StructuredError error) => CoreResult<T>.Failure(error);
}

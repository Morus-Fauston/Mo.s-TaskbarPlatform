using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;

namespace Mtp.Host;

internal interface IIndependentDockWindowResource
{
    event EventHandler? Closed;

    void Initialize();

    void Configure(HostComponentDisplayModel component, DisplayArea? displayArea);

    void Show();

    void Close();
}

internal sealed class WinUiIndependentDockWindowResource : IIndependentDockWindowResource
{
    private readonly IndependentDockWindow window;
    private bool initialized;

    public WinUiIndependentDockWindowResource()
    {
        window = new IndependentDockWindow();
        window.Closed += Window_Closed;
    }

    public event EventHandler? Closed;

    public void Initialize()
    {
        if (initialized)
        {
            return;
        }

        window.InitializeView();
        initialized = true;
    }

    public void Configure(HostComponentDisplayModel component, DisplayArea? displayArea) =>
        window.Configure(component, displayArea);

    public void Show() => window.ShowWithoutActivation();

    public void Close() => window.Close();

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        window.Closed -= Window_Closed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// WinUI adapter for the Host-owned independent top-level dock window.
/// </summary>
public sealed class WinUiIndependentDockWindowAdapter : IIndependentDockWindowAdapter
{
    private readonly Func<DisplayArea?>? displayAreaProvider;
    private readonly Func<IIndependentDockWindowResource> resourceFactory;
    private IIndependentDockWindowResource? resource;

    public WinUiIndependentDockWindowAdapter(Func<DisplayArea?>? displayAreaProvider = null)
        : this(displayAreaProvider, () => new WinUiIndependentDockWindowResource())
    {
    }

    internal WinUiIndependentDockWindowAdapter(
        Func<DisplayArea?>? displayAreaProvider,
        Func<IIndependentDockWindowResource> resourceFactory)
    {
        this.displayAreaProvider = displayAreaProvider;
        this.resourceFactory = resourceFactory ?? throw new ArgumentNullException(nameof(resourceFactory));
    }

    public event EventHandler? Closed;

    public bool IsOpen => resource is not null;

    public CoreResult<HostComponentDisplayModel> Show(HostComponentDisplayModel component)
    {
        ArgumentNullException.ThrowIfNull(component);

        try
        {
            var displayArea = displayAreaProvider?.Invoke();
            var current = resource;
            if (current is null)
            {
                current = resourceFactory();
                resource = current;
                current.Closed += Resource_Closed;
            }

            current.Initialize();
            current.Configure(component, displayArea);
            current.Show();
            return CoreResult<HostComponentDisplayModel>.Success(component);
        }
        catch (IndependentDockWindow.DockWindowPlacementException exception)
        {
            TryCleanupAfterFailedShow();
            return CoreResult<HostComponentDisplayModel>.Failure(exception.Error);
        }
        catch (Exception exception)
        {
            TryCleanupAfterFailedShow();
            return CoreResult<HostComponentDisplayModel>.Failure(
                new StructuredError("dock_window_show_failed", "The independent dock window could not be shown.", exception.GetType().Name));
        }
    }

    public CoreResult<bool> Close()
    {
        var current = resource;
        if (current is null)
        {
            return CoreResult<bool>.Success(true);
        }

        try
        {
            current.Close();
            return ReferenceEquals(resource, current)
                ? CoreResult<bool>.Failure(new StructuredError(
                    "dock_window_close_unconfirmed",
                    "The independent dock window did not confirm that it closed."))
                : CoreResult<bool>.Success(true);
        }
        catch (Exception exception)
        {
            return CoreResult<bool>.Failure(
                new StructuredError("dock_window_close_failed", "The independent dock window could not be closed.", exception.GetType().Name));
        }
    }

    private void TryCleanupAfterFailedShow()
    {
        var current = resource;
        if (current is null)
        {
            return;
        }

        try
        {
            current.Close();
        }
        catch (Exception)
        {
            // Ownership stays here so Close can be retried after the original show failure is reported.
        }
    }

    private void Resource_Closed(object? sender, EventArgs args)
    {
        if (sender is not IIndependentDockWindowResource closed || !ReferenceEquals(resource, closed))
        {
            return;
        }

        ReleaseIfCurrent(closed);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseIfCurrent(IIndependentDockWindowResource current)
    {
        if (!ReferenceEquals(resource, current))
        {
            return;
        }

        current.Closed -= Resource_Closed;
        resource = null;
    }
}

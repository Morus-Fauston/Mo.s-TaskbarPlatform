using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// WinUI adapter for the Host-owned independent top-level dock window.
/// </summary>
public sealed class WinUiIndependentDockWindowAdapter : IIndependentDockWindowAdapter
{
    private readonly Func<DisplayArea?>? displayAreaProvider;
    private IndependentDockWindow? window;

    public WinUiIndependentDockWindowAdapter(Func<DisplayArea?>? displayAreaProvider = null)
    {
        this.displayAreaProvider = displayAreaProvider;
    }

    public event EventHandler? Closed;

    public bool IsOpen => window is not null;

    public CoreResult<HostComponentDisplayModel> Show(HostComponentDisplayModel component)
    {
        ArgumentNullException.ThrowIfNull(component);

        try
        {
            var displayArea = displayAreaProvider?.Invoke();
            if (window is null)
            {
                window = new IndependentDockWindow(component, displayArea);
                window.Closed += Window_Closed;
            }
            else
            {
                window.SetDisplay(component, displayArea);
            }

            window.ShowWithoutActivation();
            return CoreResult<HostComponentDisplayModel>.Success(component);
        }
        catch (IndependentDockWindow.DockWindowPlacementException exception)
        {
            if (window is not null)
            {
                window.Closed -= Window_Closed;
                try
                {
                    window.Close();
                }
                catch (Exception)
                {
                    // Preserve the original placement failure.
                }

                window = null;
            }

            return CoreResult<HostComponentDisplayModel>.Failure(exception.Error);
        }
        catch (Exception exception)
        {
            if (window is not null)
            {
                window.Closed -= Window_Closed;
                try
                {
                    window.Close();
                }
                catch (Exception)
                {
                    // Preserve the original structured creation failure.
                }

                window = null;
            }

            return CoreResult<HostComponentDisplayModel>.Failure(
                new StructuredError("dock_window_show_failed", "The independent dock window could not be shown.", exception.GetType().Name));
        }
    }

    public CoreResult<bool> Close()
    {
        if (window is null)
        {
            return CoreResult<bool>.Success(true);
        }

        try
        {
            window.Close();
            return CoreResult<bool>.Success(true);
        }
        catch (Exception exception)
        {
            return CoreResult<bool>.Failure(
                new StructuredError("dock_window_close_failed", "The independent dock window could not be closed.", exception.GetType().Name));
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (window is not null)
        {
            window.Closed -= Window_Closed;
            window = null;
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }
}

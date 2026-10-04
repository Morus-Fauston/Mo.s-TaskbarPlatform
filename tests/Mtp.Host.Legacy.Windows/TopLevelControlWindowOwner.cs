using System;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;

namespace Mtp.Host;

internal interface ITopLevelControlWindowResource
{
    event EventHandler? Closed;

    void Close();
}

internal sealed class ExplorerTopLevelControlWindowResource : ITopLevelControlWindowResource
{
    private readonly ExplorerTaskbarProbeWindow window;

    public ExplorerTopLevelControlWindowResource(ExplorerTaskbarProbeWindow window)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.window.Closed += Window_Closed;
    }

    public event EventHandler? Closed;

    public void Close() => window.Close();

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        window.Closed -= Window_Closed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Retains the diagnostic top-level window until its Closed event confirms release.
/// </summary>
internal sealed class TopLevelControlWindowOwner
{
    private ITopLevelControlWindowResource? resource;

    public bool HasResource => resource is not null;

    public void TakeOwnership(ITopLevelControlWindowResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (this.resource is not null)
        {
            throw new InvalidOperationException("The previous top-level control window must close before another is created.");
        }

        this.resource = resource;
        resource.Closed += Resource_Closed;
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
        }
        catch (Exception exception)
        {
            return CoreResult<bool>.Failure(new StructuredError(
                "top_level_control_window_close_failed",
                "The diagnostic top-level window could not be closed.",
                exception.GetType().Name));
        }

        return ReferenceEquals(resource, current)
            ? CoreResult<bool>.Failure(new StructuredError(
                "top_level_control_window_close_unconfirmed",
                "The diagnostic top-level window did not confirm that it closed."))
            : CoreResult<bool>.Success(true);
    }

    private void Resource_Closed(object? sender, EventArgs args)
    {
        if (sender is not ITopLevelControlWindowResource closed || !ReferenceEquals(resource, closed))
        {
            return;
        }

        closed.Closed -= Resource_Closed;
        resource = null;
    }
}

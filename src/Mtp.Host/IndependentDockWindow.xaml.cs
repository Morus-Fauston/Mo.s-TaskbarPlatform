using System;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host;

/// <summary>
/// A small top-level window positioned at the lower edge of the primary work area.
/// It is an MTP window and is never parented to Explorer.
/// </summary>
public sealed partial class IndependentDockWindow : Window
{
    private const int WindowWidth = 340;
    private const int WindowHeight = 84;
    private const int WorkAreaMargin = 8;

    public IndependentDockWindow(HostComponentDisplayModel display, DisplayArea? targetDisplayArea = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        InitializeComponent();

        var presenter = OverlappedPresenter.CreateForToolWindow();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));

        SetDisplay(display, targetDisplayArea);
    }

    public void SetDisplay(HostComponentDisplayModel display, DisplayArea? targetDisplayArea = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        ComponentText.Text = display.Text;
        IdentityText.Text = string.Join(" / ", display.Identity.Segments.Select(segment => segment.Value));
        StatusText.Text = display.StatusLabel;
        ComponentText.Opacity = display.Status == CapabilityStatus.Failed ? 0.75 : 1;
        PositionNearWorkArea(targetDisplayArea);
    }

    public void ShowWithoutActivation() => AppWindow.Show(false);

    private void PositionNearWorkArea(DisplayArea? targetDisplayArea)
    {
        var displayArea = targetDisplayArea ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
            ?? throw new InvalidOperationException("No display area is available for the independent dock window.");
        var placement = IndependentDockWindowPlacement.TryCalculate(
            displayArea.OuterBounds,
            displayArea.WorkArea,
            new SizeInt32(WindowWidth, WindowHeight),
            WorkAreaMargin);
        if (!placement.IsSuccess)
        {
            throw new DockWindowPlacementException(placement.Error!);
        }

        // This overload consumes screen coordinates. Do not pass the DisplayArea again,
        // otherwise a non-zero monitor offset can be applied twice.
        AppWindow.MoveAndResize(placement.Value!);
    }

    internal sealed class DockWindowPlacementException(StructuredError error) : Exception(error.Message)
    {
        public StructuredError Error { get; } = error;
    }
}

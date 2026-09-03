using Microsoft.UI.Xaml;
using System.Linq;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// The ordinary Host window for the minimum display baseline.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly HostDisplayController displayController;
    private readonly IndependentDockWindowController dockWindowController;
    private HostComponentDisplayModel? selectedComponent;
    private bool applyingVisibility;

    public MainWindow(
        HostDisplayController displayController,
        HostDisplayLoadResult displayLoad,
        IndependentDockWindowController dockWindowController)
    {
        this.displayController = displayController ?? throw new ArgumentNullException(nameof(displayController));
        ArgumentNullException.ThrowIfNull(displayLoad);
        this.dockWindowController = dockWindowController ?? throw new ArgumentNullException(nameof(dockWindowController));
        InitializeComponent();
        Closed += MainWindow_Closed;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 300));

        selectedComponent = displayLoad.Components.FirstOrDefault();
        if (selectedComponent is not null)
        {
            ApplyDisplay(selectedComponent);
            VisibilityToggle.IsEnabled = true;
            applyingVisibility = true;
            VisibilityToggle.IsOn = selectedComponent.IsVisible;
            applyingVisibility = false;
        }
        else
        {
            var fallback = new Component(
                new StableIdentity(new StableId("mtp"))
                    .CreateChild(new StableId("declaration-error")),
                CapabilityState.Failed(displayLoad.DeclarationError?.Message ?? "声明未加载。"));
            ApplyDisplay(HostComponentDisplayModel.From(fallback));
        }

        var error = displayLoad.DeclarationError ?? displayLoad.PreferenceError;
        if (error is not null)
        {
            ErrorText.Text = FormatError(error);
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ApplyDisplay(HostComponentDisplayModel display)
    {
        ComponentText.Text = display.Text;
        IdentityText.Text = $"声明组件：{string.Join(" / ", display.Identity.Segments.Select(segment => segment.Value))}";
        StatusText.Text = display.StatusLabel;
        ComponentCard.Visibility = display.IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VisibilityToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (applyingVisibility || selectedComponent is null)
        {
            return;
        }

        var result = displayController.SetVisibility(selectedComponent.Identity, VisibilityToggle.IsOn);
        if (result.IsSuccess)
        {
            selectedComponent = result.Value!;
            ApplyDisplay(selectedComponent);
            if (selectedComponent.IsVisible)
            {
                var showResult = dockWindowController.Show(selectedComponent);
                if (!showResult.IsSuccess)
                {
                    ShowHostError(showResult.Error!);
                }
            }
            else
            {
                var closeResult = dockWindowController.Close();
                if (!closeResult.IsSuccess)
                {
                    ShowHostError(closeResult.Error!);
                }
            }
            return;
        }

        applyingVisibility = true;
        VisibilityToggle.IsOn = selectedComponent.IsVisible;
        applyingVisibility = false;
        ErrorText.Text = FormatError(result.Error!);
        ErrorText.Visibility = Visibility.Visible;
    }

    public void ShowHostError(StructuredError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ErrorText.Text = FormatError(error);
        ErrorText.Visibility = Visibility.Visible;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _ = dockWindowController.Close();
        dockWindowController.Dispose();
    }

    private static string FormatError(StructuredError error) =>
        string.IsNullOrWhiteSpace(error.Path)
            ? $"{error.Code}: {error.Message}"
            : $"{error.Code}: {error.Message} ({error.Path})";
}

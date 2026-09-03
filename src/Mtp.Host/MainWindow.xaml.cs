using Microsoft.UI.Xaml;
using System.Linq;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// The ordinary Host window for the minimum display baseline.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(DeclarationLoadResult declarationLoad)
    {
        ArgumentNullException.ThrowIfNull(declarationLoad);
        InitializeComponent();

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 240));

        if (declarationLoad.Current is not null)
        {
            ApplyDisplay(HostComponentDisplayModel.From(declarationLoad.Current));
        }
        else
        {
            var fallback = new Component(
                new StableIdentity(new StableId("mtp"))
                    .CreateChild(new StableId("declaration-error")),
                CapabilityState.Failed(declarationLoad.Error?.Message ?? "声明未加载。"));
            ApplyDisplay(HostComponentDisplayModel.From(fallback));
        }

        if (declarationLoad.Error is not null)
        {
            ErrorText.Text = FormatError(declarationLoad.Error);
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ApplyDisplay(HostComponentDisplayModel display)
    {
        ComponentText.Text = display.Text;
        IdentityText.Text = $"声明组件：{string.Join(" / ", display.Identity.Segments.Select(segment => segment.Value))}";
        StatusText.Text = display.StatusLabel;
    }

    private static string FormatError(StructuredError error) =>
        string.IsNullOrWhiteSpace(error.Path)
            ? $"{error.Code}: {error.Message}"
            : $"{error.Code}: {error.Message} ({error.Path})";
}

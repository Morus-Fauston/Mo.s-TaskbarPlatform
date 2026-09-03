using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.IO;
using System.Linq;

namespace Mtp.Host;

/// <summary>
/// Starts the ordinary WinUI window used by the first Host display slice.
/// </summary>
public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var declarationPath = Path.Combine(AppContext.BaseDirectory, "declaration.json");
        var preferencePath = Path.Combine(AppContext.BaseDirectory, "display-preferences.json");
        var controller = new HostDisplayController(
            new LocalJsonDeclarationSource(declarationPath),
            new LocalComponentDisplayPreferenceStore(preferencePath));
        var displayLoad = controller.Load();
        MainWindow? ownerWindow = null;
        var dockWindowController = new IndependentDockWindowController(
            controller,
            new WinUiIndependentDockWindowAdapter(() => ownerWindow is null
                ? null
                : DisplayArea.GetFromWindowId(ownerWindow.AppWindow.Id, DisplayAreaFallback.Primary)));
        ownerWindow = new MainWindow(controller, displayLoad, dockWindowController);
        window = ownerWindow;
        window.Activate();

        if (displayLoad.Components.Any(component => component.IsVisible))
        {
            var dockResult = dockWindowController.ShowCurrent();
            if (!dockResult.IsSuccess)
            {
                window.ShowHostError(dockResult.Error!);
            }
        }
    }
}

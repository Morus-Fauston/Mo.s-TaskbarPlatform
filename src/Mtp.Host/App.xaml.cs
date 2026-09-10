using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.IO;

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
        var probeController = new ExplorerTaskbarProbeController(
            controller,
            new Win32ExplorerTaskbarEmbedAdapter(() => DisplayArea.FindAll().Count),
            dockWindowController);
        var displayActions = new HostDisplayActionController(controller, dockWindowController, probeController);
        ownerWindow = new MainWindow(displayLoad, displayActions);
        window = ownerWindow;
        window.Activate();

        var restoreResult = displayActions.RestoreCurrent();
        foreach (var error in restoreResult.Errors)
        {
            window.ShowHostError(error);
        }
    }
}

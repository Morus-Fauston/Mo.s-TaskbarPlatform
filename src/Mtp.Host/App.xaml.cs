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
        var taskbarEnvironment = new Win32TaskbarDockEnvironment();
        var taskbarDock = new TaskbarDockWindowAdapter(
            new LocalTaskbarDockPreferenceStore(Path.Combine(AppContext.BaseDirectory, "taskbar-dock-preferences.json")),
            taskbarEnvironment,
            () => new WinUiTaskbarDockWindow());
        var dockWindowController = new IndependentDockWindowController(
            controller,
            taskbarDock);
        var probeController = new ExplorerTaskbarProbeController(
            controller,
            new Win32ExplorerTaskbarEmbedAdapter(() => DisplayArea.FindAll().Count),
            dockWindowController);
        var displayActions = new HostDisplayActionController(controller, dockWindowController, probeController);
        window = new MainWindow(displayLoad, displayActions, taskbarDock, taskbarEnvironment);
        var launchEnvironment = taskbarEnvironment.Capture(taskbarDock.Preferences.TargetDisplayId);
        if (launchEnvironment.Value?.Visibility == Mtp.Platform.Core.TaskbarVisibility.Allowed)
            window.Activate();
        else
            window.AppWindow.Show(false);

        var restoreResult = displayActions.RestoreCurrent();
        foreach (var error in restoreResult.Errors)
        {
            window.ShowHostError(error);
        }
    }
}

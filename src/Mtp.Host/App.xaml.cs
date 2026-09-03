using Microsoft.UI.Xaml;
using System.IO;

namespace Mtp.Host;

/// <summary>
/// Starts the ordinary WinUI window used by the first Host display slice.
/// </summary>
public partial class App : Application
{
    private Window? window;

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
        window = new MainWindow(controller, controller.Load());
        window.Activate();
    }
}

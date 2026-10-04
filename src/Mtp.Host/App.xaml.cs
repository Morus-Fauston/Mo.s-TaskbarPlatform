using Microsoft.UI.Xaml;
using Mtp.Host.Islands;
using System.IO;

namespace Mtp.Host;

public partial class App : Application
{
    private MainWindow? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var preferences = HostPreferenceStorage.Initialize(AppContext.BaseDirectory);
        var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")), preferences.DisplayStore);
        var loaded = display.Load();
        var environment = new Win32TaskbarDockEnvironment();
        var evidence = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MTP", "HostEvidence");
        var console = new HostConsoleController(display, loaded, preferences.DockStore,
            preferences => IslandDisplayAdapter.CaptureExplorer(environment, preferences), evidence, environment);
        window = new MainWindow(console, environment.GetDisplays);
        window.Activate();
        console.Refresh();
    }
}

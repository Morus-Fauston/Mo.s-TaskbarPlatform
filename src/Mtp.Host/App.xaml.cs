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
        var templates = Environment.GetCommandLineArgs().Contains("--template-demo", StringComparer.Ordinal);
        if (templates || Environment.GetCommandLineArgs().Contains("--counter-demo", StringComparer.Ordinal))
            _ = StartCounterAsync(console, templates);
    }
    private static async Task StartCounterAsync(HostConsoleController console, bool templates)
    {
        try
        {
            await console.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker", "Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService", "Mtp.CounterService.dll"), templates);
        }
        catch (Exception error) { console.Execute(() => throw new InvalidOperationException("计数器启动失败：" + error.Message, error)); }
    }
}

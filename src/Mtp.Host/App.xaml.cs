using Microsoft.UI.Xaml;
using Mtp.Host.Islands;
using System.IO;

namespace Mtp.Host;

public partial class App : Application
{
    private Window? window;
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
        if (Environment.GetCommandLineArgs().Contains("--diagnostics", StringComparer.Ordinal))
            window = new MainWindow(console, environment.GetDisplays);
        else
        {
            var settings = new HostSettingsController(display, preferences.SettingsStore, () => console.Applications,
                console.RetryAsync, () => console.MaterialStatus, console.ApplyAppearance, console.RequestRefresh);
            console.AttachSettings(settings);
            window = new SettingsWindow(settings, console, environment.GetDisplays);
        }
        window.Activate();
        console.Refresh();
        var templates = Environment.GetCommandLineArgs().Contains("--template-demo", StringComparer.Ordinal);
        var dynamic = Environment.GetCommandLineArgs().Contains("--dynamic-demo", StringComparer.Ordinal);
        var timers = Environment.GetCommandLineArgs().Contains("--timer-demo", StringComparer.Ordinal);
        var presets = Environment.GetCommandLineArgs().Contains("--preset-demo", StringComparer.Ordinal);
        var flyouts = Environment.GetCommandLineArgs().Contains("--flyout-demo", StringComparer.Ordinal);
        var organization = Environment.GetCommandLineArgs().Contains("--organization-demo", StringComparer.Ordinal);
        if (templates || dynamic || timers || presets || flyouts || organization || Environment.GetCommandLineArgs().Contains("--counter-demo", StringComparer.Ordinal))
            _ = StartCounterAsync(console, templates, dynamic, timers, presets, flyouts, organization);
    }
    private static async Task StartCounterAsync(HostConsoleController console, bool templates, bool dynamic, bool timers, bool presets, bool flyouts, bool organization)
    {
        try
        {
            await console.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker", "Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService", "Mtp.CounterService.dll"), templates, dynamic, timers, presets, flyouts, organization);
        }
        catch (Exception error) { console.Execute(() => throw new InvalidOperationException("计数器启动失败：" + error.Message, error)); }
    }
}

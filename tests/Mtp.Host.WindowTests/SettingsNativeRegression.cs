using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class SettingsNativeRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "settings-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "host-settings.json");
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        using var target = new OwnedIslandTarget();
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")), target.Capture, root);
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(path), () => host.Applications,
            host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        var window = new SettingsWindow(settings, host, () => [new("owned-primary", true, new(-30000, -30000, 1000, 800), new(-30000, -30000, 1000, 720), 96)]);
        var processes = new List<Process>();
        try
        {
            window.AppWindow.Move(new(-30000, -30000));
            window.AppWindow.Show(false);
            await Until(() => ((FrameworkElement)window.Content).IsLoaded, "Settings window did not load.");
            log("settings-step: loaded");
            var navigation = Find<NavigationView>(window, "mtp-settings-navigation");
            Check(!Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Fresh preferences must not show a historical missing-file error.");
            Check(!navigation.IsBackEnabled, "Empty navigation history should disable Back.");
            Navigate(window, navigation, "global");
            await Until(() => settings.GetSnapshot().Navigation.Current == SettingsPage.Global, "Native navigation did not reach Global.");
            Find<ComboBox>(window, "mtp-settings-theme").SelectedIndex = 2;
            await Until(() => settings.GetSnapshot().Preferences.Appearance.Theme == HostTheme.Dark, "Native theme choice did not save.");
            SelectTag(Find<ComboBox>(window, "mtp-settings-material"), "Solid");
            Find<NumberBox>(window, "mtp-settings-opacity").Value = 0.6;
            await Until(() => settings.GetSnapshot().Preferences.Appearance is { Material: MaterialKind.Solid, Opacity: 0.6 }, "Native material choice did not save.");
            log("settings-step: native appearance saved");
            Check(navigation.IsBackEnabled, "Navigation did not enable Back.");
            var back = Descendants(window.Content).OfType<Button>().FirstOrDefault(button =>
                button.Name == "NavigationViewBackButton" || AutomationProperties.GetAutomationId(button) == "NavigationViewBackButton");
            if (back is null) log("settings-buttons: " + string.Join(";", Descendants(window.Content).OfType<Button>().Select(button =>
                $"{button.Name}/{AutomationProperties.GetAutomationId(button)}/{AutomationProperties.GetName(button)}")));
            Check(back is not null, "Native NavigationView Back button is missing.");
            Invoke(back!);
            await Until(() => settings.GetSnapshot().Navigation.Current == SettingsPage.Applications, "Native Back did not restore Applications.");
            log("settings-step: native Back restored Applications");
            await host.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), templates: true).WaitAsync(TimeSpan.FromSeconds(12));
            var session = Field<HostBrokerSession>(host, "communication");
            foreach (int pid in session.ServiceProcessIds.Prepend(session.BrokerProcessId))
            { var process = Process.GetProcessById(pid); _ = process.Handle; processes.Add(process); }
            await Until(() => settings.GetSnapshot().Components.Any(value => value.Identity.Segments[0].Value == "counter"), "Connected component missing from settings.");
            Navigate(window, navigation, "layout");
            await Until(() => settings.GetSnapshot().Navigation.Current == SettingsPage.Layout, "Layout did not open.");
            var toggle = Find<ToggleSwitch>(window, "mtp-settings-visible-counter/main/counter");
            ((IToggleProvider)new ToggleSwitchAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
            await Until(() => host.Session.State == IslandDisplayState.Embedded, "Settings display toggle did not create production island.");
            log("settings-step: display created production island");
            var adapter = Field<IslandDisplayAdapter>(host, "adapter");
            nint hwnd = adapter.Handle;
            var rows = settings.GetSnapshot().Components;
            int counterIndex = rows.ToList().FindIndex(value => value.Identity.Segments[0].Value == "counter");
            Check(rows.Count >= 2, "Fixture must include multiple stable components.");
            string movement = counterIndex == 0 ? "down" : "up";
            Invoke(Find<Button>(window, "mtp-settings-" + movement + "-counter/main/counter"));
            await Until(() => settings.GetSnapshot().Components.ToList().FindIndex(value => value.Identity.Segments[0].Value == "counter") != counterIndex,
                "Native reorder button did not change stable order.");
            Check(ReferenceEquals(toggle, Find<ToggleSwitch>(window, "mtp-settings-visible-counter/main/counter")), "Settings refresh replaced the active toggle.");
            log("settings-step: stable row reordered");
            Navigate(window, navigation, "global");
            await Until(() => settings.GetSnapshot().Navigation.Current == SettingsPage.Global, "Global did not reopen.");
            Find<ComboBox>(window, "mtp-settings-theme").SelectedIndex = 1;
            await Until(() => settings.GetSnapshot().Preferences.Appearance.Theme == HostTheme.Light, "Light preference not saved.");
            Check(adapter.Handle == hwnd, "Theme preference rebuilt the content island.");
            await Until(() => ((FrameworkElement)window.Content).RequestedTheme == ElementTheme.Light, "Settings theme did not update.");
            await ConsoleScreenshot.SaveAsync(window, Path.Combine(root, "settings-light.png"));
            Find<ComboBox>(window, "mtp-settings-theme").SelectedIndex = 2;
            await Until(() => ((FrameworkElement)window.Content).RequestedTheme == ElementTheme.Dark, "Dark settings theme did not update.");
            await ConsoleScreenshot.SaveAsync(window, Path.Combine(root, "settings-dark.png"));
            using (var blocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Find<NumberBox>(window, "mtp-settings-opacity").Value = 0.2;
                await Until(() => settings.GetSnapshot().Error?.Code == "settings_read_failed" &&
                    Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Failed save lacked its current visible explanation.");
                Check(settings.GetSnapshot().Preferences.Appearance.Opacity == 0.6, "Failed save replaced last complete preference.");
            }
            var expected = settings.GetSnapshot().Preferences;
            using var child = new Process { StartInfo = new(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden } };
            child.StartInfo.ArgumentList.Add("--settings-readback"); child.StartInfo.ArgumentList.Add(path);
            Check(child.Start(), "Readback child did not start.");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await child.WaitForExitAsync(deadline.Token);
            Check(child.ExitCode == 0, "Independent settings reload failed.");
            var restored = JsonSerializer.Deserialize<HostSettingsPreferences>(File.ReadAllText(path + ".readback.json"))!;
            Check(restored.Appearance == expected.Appearance && restored.ComponentOrder.SequenceEqual(expected.ComponentOrder), "Independent process did not restore complete settings.");
            string damaged = Path.Combine(root, "damaged-settings.json");
            File.WriteAllText(damaged, "{broken");
            using (var fallback = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(damaged), () => host.Applications,
                host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh))
            {
                host.AttachSettings(fallback);
                Check(fallback.GetSnapshot().Error is not null && host.Appearance == fallback.GetSnapshot().Preferences.Appearance,
                    "Unreadable settings did not apply the same temporary appearance to production Host.");
                Check(File.ReadAllText(damaged) == "{broken", "Temporary defaults overwrote damaged settings.");
                Check(adapter.Handle == hwnd, "Temporary appearance fallback recreated the island.");
            }
            host.AttachSettings(settings);
            log($"PASS: native settings navigation/back, stable rows/display/reorder, theme/material/opacity, failure rollback; independentReadbackPid={child.Id}; sameIsland={hwnd}; screenshots={root}");
            log("PASS: damaged settings retain original bytes and explanatory error; Host applies temporary appearance without replacing island.");
        }
        finally
        {
            Check(host.Shutdown(), "Settings Host cleanup failed.");
            window.Close();
            foreach (var process in processes)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(deadline.Token);
                log($"settingsOwnedPid={process.Id}; exited={process.HasExited}"); process.Dispose();
            }
        }
    }
    private static void SelectTag(ComboBox combo, string tag) => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, tag));
    private static void Navigate(Window window, NavigationView navigation, string page) => navigation.SelectedItem = Find<NavigationViewItem>(window, "mtp-settings-nav-" + page);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static T Find<T>(Window window, string id) where T : FrameworkElement => Descendants(window.Content).OfType<T>().First(value => AutomationProperties.GetAutomationId(value) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var item in Descendants(VisualTreeHelper.GetChild(root, index))) yield return item; }
    private static T Field<T>(object value, string name) where T : class => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static async Task Until(Func<bool> predicate, string message)
    { var watch = Stopwatch.StartNew(); while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException(message); await Task.Delay(25); } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

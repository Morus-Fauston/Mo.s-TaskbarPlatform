using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>Real settings controls with accepted declaration state; SDK request/display integration is tested separately.</summary>
internal static class ShortHintSettingsNativeTests
{
    internal static async Task RunAsync(Action<string> log)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "hint-settings-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "host-settings.json");
        var states = DeclaredHints();
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        using var target = new OwnedIslandTarget();
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")), target.Capture, root);
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(path), () => states.Snapshots,
            host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        var window = new SettingsWindow(settings, host, () => [new("owned-primary", true,
            new(-30000, -30000, 1000, 800), new(-30000, -30000, 1000, 720), 96)]);
        try
        {
            window.AppWindow.Move(new(-30000, -30000));
            window.AppWindow.Show(false);
            await Until(() => ((FrameworkElement)window.Content).IsLoaded, "Hint settings window did not load.");
            Navigate(window, "global");
            var position = Find<ComboBox>(window, "mtp-settings-hint-position");
            var allow = Find<ToggleSwitch>(window, "mtp-settings-hint-app-position");
            FlyoutPosition[] expectedPositions = [FlyoutPosition.TopLeft, FlyoutPosition.TopCenter, FlyoutPosition.TopRight,
                FlyoutPosition.BottomLeft, FlyoutPosition.BottomCenter, FlyoutPosition.BottomRight, FlyoutPosition.Center, FlyoutPosition.LowerCenter];
            Check(position.Items.Count == 8 && position.Items.OfType<ComboBoxItem>().Select(item => item.Tag?.ToString())
                .SequenceEqual(expectedPositions.Select(value => value.ToString())), "Hint position ComboBox must expose exactly the eight supported positions.");
            Check(((ComboBoxItem)position.SelectedItem).Tag?.ToString() == "LowerCenter" && !allow.IsOn,
                "Initial hint controls do not reflect LowerCenter and disabled application override.");
            foreach (var value in expectedPositions)
            {
                position.SelectedItem = position.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, value.ToString()));
                await Until(() => settings.GetSnapshot().Preferences.Hints?.DefaultPosition == value,
                    $"Native ComboBox selection did not reach the settings controller: {value}.");
                Check(Read(path).Hints == new HostHintPreferences(value, false), $"Native position was not persisted: {value}.");
                log($"hint-settings-position: {value}; controller=true; persisted=true");
            }
            Toggle(allow);
            await Until(() => settings.GetSnapshot().Preferences.Hints?.AllowApplicationPosition == true,
                "Native application-position toggle did not save.");
            Check(Read(path).Hints == new HostHintPreferences(FlyoutPosition.LowerCenter, true), "Application override did not persist.");
            using (var blocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Toggle(allow);
                await Until(() => Find<InfoBar>(window, "mtp-settings-error").IsOpen,
                    "Failed native hint-preference save lacked its visible explanation.");
                Check(allow.IsOn && settings.GetSnapshot().Preferences.Hints?.AllowApplicationPosition == true,
                    "Failed native hint save did not restore the control and committed preference.");
            }
            Toggle(allow);
            Check(!Read(path).Hints!.AllowApplicationPosition, "Application override could not be disabled after failed save.");
            Toggle(allow);
            Check(Read(path).Hints!.AllowApplicationPosition, "Application override could not be enabled again.");
            log("hint-settings-override: enabled/disabled persisted; failed save restores actual toggle");

            Navigate(window, "flyouts");
            var rows = settings.GetSnapshot().HintEntries;
            Check(rows.Count == 2 && rows.All(row => row.IsAvailable && row.IsVisible), "Accepted declaration did not project both hint kinds.");
            Check(Find<TextBlock>(window, "mtp-settings-hints-empty").Visibility == Visibility.Collapsed,
                "Declared hints still show the empty state.");
            foreach (var row in rows)
            {
                string controlId = "mtp-settings-hint-visible-" + string.Join("/", row.Identity.Segments.Select(segment => Uri.EscapeDataString(segment.Value)));
                var visible = Find<ToggleSwitch>(window, controlId);
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(visible)), "Hint toggle has no accessible name.");
                Toggle(visible);
                await Until(() => !settings.GetSnapshot().HintEntries.Single(entry => entry.Identity == row.Identity).IsVisible,
                    "Native row toggle did not reach the settings controller.");
                Check(!Read(path).HintVisibility![HostSettingsController.IdentityKey(row.Identity)],
                    "Native row toggle did not persist under the full stable identity.");
                Check(ReferenceEquals(visible, Find<ToggleSwitch>(window, controlId)), "Hint refresh replaced the active native toggle.");
                Toggle(visible);
                Check(Read(path).HintVisibility![HostSettingsController.IdentityKey(row.Identity)], "Hint row could not be re-enabled.");
                Toggle(visible);
                Check(!Read(path).HintVisibility![HostSettingsController.IdentityKey(row.Identity)], "Final disabled hint value did not persist.");
                log($"hint-settings-row: {row.Identity}; kind={row.Kind}; native-toggle=true; stable-row=true; persisted=false");
            }
            Check(Descendants(window.Content).OfType<ToggleSwitch>().Count(value =>
                AutomationProperties.GetAutomationId(value).StartsWith("mtp-settings-hint-visible-", StringComparison.Ordinal)) == 2,
                "Taskbar or event declarations leaked into the hint settings controls.");
            await IndependentReadback(path, settings.GetSnapshot().Preferences, log);
            Check(!Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Successful hint save did not clear the earlier failure explanation.");
            log($"PASS: hint settings actual WinUI ComboBox eight positions, native Toggle providers, rollback, stable declared rows, independent preference readback; evidence={root}");
        }
        finally
        {
            bool cleaned = host.Shutdown();
            window.Close();
            Check(cleaned, "Hint settings Host cleanup failed.");
            log("hint-settings-cleanup: hostStopped=true; settingsClosed=true");
        }
    }

    private static BrokerStateStore DeclaredHints()
    {
        var states = new BrokerStateStore(["hint-settings"]);
        states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "hint-settings", SessionId = "settings-session" });
        var result = states.Handle(new()
        {
            Kind = MessageKind.Declare, ApplicationId = "hint-settings", SessionId = "settings-session",
            Declaration = new("hint-settings", [new("main", [new("component", [new("activate")])], [new("panel", [new("activate")])],
                Hints: [new("notice", FlyoutKind.ShortHint), new("interactive", FlyoutKind.InteractiveHint)],
                EventChannels: [new("event", EventClosePolicy.AutoClose)])]),
            State = new(0, [new("main", "component", "value")])
        }).Result;
        Check(result?.Accepted == true, "Native hint settings fixture declaration was rejected: " + result);
        return states;
    }

    private static async Task IndependentReadback(string path, HostSettingsPreferences expected, Action<string> log)
    {
        using var child = new Process { StartInfo = new(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } };
        child.StartInfo.ArgumentList.Add("--settings-readback");
        child.StartInfo.ArgumentList.Add(path);
        Check(child.Start(), "Hint settings readback child did not start.");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync(deadline.Token);
            Check(child.ExitCode == 0, "Independent hint settings reload failed.");
            var restored = JsonSerializer.Deserialize<HostSettingsPreferences>(File.ReadAllText(path + ".readback.json"));
            Check(restored is not null && restored.Hints == expected.Hints && restored.HintVisibility is not null &&
                expected.HintVisibility is not null && restored.HintVisibility.Count == expected.HintVisibility.Count &&
                expected.HintVisibility.All(pair => restored.HintVisibility.TryGetValue(pair.Key, out bool value) && value == pair.Value),
                "Independent process did not restore complete hint preferences and full-identity visibility.");
            log($"hint-settings-readback: pid={child.Id}; exit={child.ExitCode}; hints={restored!.HintVisibility!.Count}");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await child.WaitForExitAsync(cleanup.Token);
                log($"hint-settings-readback-cleanup: pid={child.Id}; forcedExit=true");
            }
        }
    }

    private static HostSettingsPreferences Read(string path)
    {
        var result = new LocalHostSettingsPreferenceStore(path).Load();
        Check(result.IsSuccess, "Native hint settings readback failed: " + result.Error);
        return result.Value!;
    }
    private static void Navigate(Window window, string page) => Find<NavigationView>(window, "mtp-settings-navigation").SelectedItem =
        Find<NavigationViewItem>(window, "mtp-settings-nav-" + page);
    private static void Toggle(ToggleSwitch value) => ((IToggleProvider)new ToggleSwitchAutomationPeer(value).GetPattern(PatternInterface.Toggle)).Toggle();
    private static T Find<T>(Window window, string id) where T : FrameworkElement => Descendants(window.Content).OfType<T>()
        .Single(value => AutomationProperties.GetAutomationId(value) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var item in Descendants(VisualTreeHelper.GetChild(root, index))) yield return item; }
    private static async Task Until(Func<bool> predicate, string message)
    { var watch = Stopwatch.StartNew(); while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException(message); await Task.Delay(25); } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

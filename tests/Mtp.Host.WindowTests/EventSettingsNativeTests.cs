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

/// <summary>Production WinUI settings controls. Accepted declaration fixtures do not claim SDK event presentation.</summary>
internal static class EventSettingsNativeTests
{
    internal static async Task RunAsync(Action<string> log)
    {
        var clock = Stopwatch.StartNew();
        string root = Path.Combine(AppContext.BaseDirectory, "event-settings-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "host-settings.json");
        var states = DeclaredEvents();
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        using var target = new OwnedIslandTarget();
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")), target.Capture, root);
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(path), () => states.Snapshots,
            host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        var window = new SettingsWindow(settings, host, () => [new("owned-primary", true,
            new(-30000, -30000, 1000, 800), new(-30000, -30000, 1000, 720), 96)]);
        Exception? failure = null;
        try
        {
            window.AppWindow.Move(new(-30000, -30000));
            window.AppWindow.Show(false);
            await Until(() => ((FrameworkElement)window.Content).IsLoaded, "Event settings window did not load.");
            Navigate(window, "global");
            var position = Find<ComboBox>(window, "mtp-settings-event-position");
            var allow = Find<ToggleSwitch>(window, "mtp-settings-event-app-position");
            var limit = Find<NumberBox>(window, "mtp-settings-event-limit");
            var range = (IRangeValueProvider)new NumberBoxAutomationPeer(limit).GetPattern(PatternInterface.RangeValue);
            Check(range.Minimum == 1 && range.Maximum == 10 && !range.IsReadOnly && range.Value == 5,
                "Event limit native range must expose 1..10 and default 5.");
            Check(((ComboBoxItem)position.SelectedItem).Tag?.ToString() == "BottomLeft" && !allow.IsOn,
                "Event controls do not reflect default BottomLeft with application override disabled.");
            Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(limit)), "Event limit has no accessible name.");
            FlyoutPosition[] positions = [FlyoutPosition.TopLeft, FlyoutPosition.TopCenter, FlyoutPosition.TopRight,
                FlyoutPosition.BottomLeft, FlyoutPosition.BottomCenter, FlyoutPosition.BottomRight, FlyoutPosition.Center, FlyoutPosition.LowerCenter];
            Check(position.Items.Count == 8 && position.Items.OfType<ComboBoxItem>().Select(item => item.Tag?.ToString())
                .SequenceEqual(positions.Select(value => value.ToString())), "Event position control must expose exactly eight supported positions.");
            foreach (var value in positions)
            {
                await SelectPosition(position, value);
                await Until(() => settings.GetSnapshot().Preferences.Events?.DefaultPosition == value,
                    "Native event position did not save: " + value);
                Check(Read(path).Events == new HostEventPreferences(value, false, 5), "Selected event position did not persist: " + value);
                log($"event-settings-position: {value}; selectionItemProvider=true; persisted=true");
            }
            Toggle(allow);
            await Until(() => settings.GetSnapshot().Preferences.Events?.AllowApplicationPosition == true,
                "Native event application-position toggle did not save.");
            Check(Read(path).Events!.AllowApplicationPosition, "Event application preference override did not persist.");
            byte[] beforeLockedOverride = File.ReadAllBytes(path);
            using (var blocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Toggle(allow);
                await Until(() => Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Failed event override save lacked a visible explanation.");
                Check(allow.IsOn && settings.GetSnapshot().Preferences.Events!.AllowApplicationPosition,
                    "Failed event override save did not restore the actual toggle and committed preference.");
            }
            Check(beforeLockedOverride.SequenceEqual(File.ReadAllBytes(path)), "Failed event override save changed the settings file.");
            Toggle(allow);
            Check(!Read(path).Events!.AllowApplicationPosition, "Event override could not be disabled after a failed save.");
            Toggle(allow);
            Check(Read(path).Events!.AllowApplicationPosition, "Event override could not be enabled again.");
            log("event-settings-override: toggleProvider=true; enabled/disabled=true; locked-save-restored=true");

            foreach (int value in new[] { 1, 5, 10 })
            {
                range.SetValue(value);
                await Until(() => settings.GetSnapshot().Preferences.Events?.MaximumGroupsPerScreen == value,
                    "Native event range did not save: " + value);
                Check(range.Value == value && Read(path).Events!.MaximumGroupsPerScreen == value, "Native event group limit did not persist.");
                log($"event-settings-limit: {value}; rangeValueProvider=true; persisted=true");
            }
            byte[] beforeInvalid = File.ReadAllBytes(path);
            range.SetValue(1.5); // Within the native numeric range, but invalid for the product's integral group count.
            await Until(() => Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Fractional event limit lacked a visible validation explanation.");
            Check(range.Value == 10 && settings.GetSnapshot().Preferences.Events!.MaximumGroupsPerScreen == 10 &&
                beforeInvalid.SequenceEqual(File.ReadAllBytes(path)), "Invalid fractional count was rounded, persisted or failed to restore its native value.");
            using (var blocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                range.SetValue(5);
                Check(range.Value == 10 && settings.GetSnapshot().Preferences.Events!.MaximumGroupsPerScreen == 10 &&
                    Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Locked event limit save did not restore the range control and explain failure.");
            }
            Check(beforeInvalid.SequenceEqual(File.ReadAllBytes(path)), "Locked event limit save changed stored state.");
            range.SetValue(5);
            Check(range.Value == 5 && Read(path).Events!.MaximumGroupsPerScreen == 5, "Valid event limit did not recover after rejection.");
            Check(!Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Successful event limit retry did not clear the error.");
            log("event-settings-limit-validation: fractional-rejected=true; native-value-restored=true; locked-save-restored=true; retry-clears-error=true");

            Navigate(window, "flyouts");
            var rows = settings.GetSnapshot().EventEntries;
            Check(rows.Count == 4 && rows.All(row => row.IsAvailable && row.IsVisible) &&
                rows.Select(row => row.ClosePolicy).Distinct().Count() == 2, "Accepted declarations did not project both close policies for both apps.");
            Check(Find<TextBlock>(window, "mtp-settings-events-empty").Visibility == Visibility.Collapsed,
                "Declared event channels still show the empty state.");
            foreach (var row in rows)
            {
                string controlId = "mtp-settings-event-visible-" + string.Join("/", row.Identity.Segments.Select(segment => Uri.EscapeDataString(segment.Value)));
                var visible = Find<ToggleSwitch>(window, controlId);
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(visible)), "Event visibility toggle lacks an accessible name.");
                Toggle(visible);
                await Until(() => !settings.GetSnapshot().EventEntries.Single(entry => entry.Identity == row.Identity).IsVisible,
                    "Event row native toggle did not reach the settings controller.");
                Check(!Read(path).EventVisibility![HostSettingsController.IdentityKey(row.Identity)], "Event visibility did not persist with complete identity.");
                Check(ReferenceEquals(visible, Find<ToggleSwitch>(window, controlId)), "Settings refresh replaced the active event row toggle.");
                Toggle(visible);
                Check(Read(path).EventVisibility![HostSettingsController.IdentityKey(row.Identity)], "Event row could not be re-enabled.");
                byte[] original = File.ReadAllBytes(path);
                using (var blocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Toggle(visible);
                    Check(visible.IsOn && settings.GetSnapshot().EventEntries.Single(entry => entry.Identity == row.Identity).IsVisible &&
                        Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Failed visibility save did not restore its actual native toggle.");
                }
                Check(original.SequenceEqual(File.ReadAllBytes(path)), "Failed event visibility save changed stored state.");
                Toggle(visible);
                Check(!Read(path).EventVisibility![HostSettingsController.IdentityKey(row.Identity)], "Event row retry did not persist its disabled value.");
                log($"event-settings-row: {row.Identity}; closePolicy={row.ClosePolicy}; toggleProvider=true; stable-row=true; locked-save-restored=true; finalVisible=false");
            }
            Check(Descendants(window.Content).OfType<ToggleSwitch>().Count(value =>
                AutomationProperties.GetAutomationId(value).StartsWith("mtp-settings-event-visible-", StringComparison.Ordinal)) == 4,
                "Hint or taskbar declarations leaked into the event visibility rows.");
            Check(settings.GetSnapshot().HintEntries.All(row => row.IsVisible), "Event toggles changed unrelated hint visibility.");
            Check(!Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Successful event visibility save did not clear prior failure.");
            await IndependentReadback(path, settings.GetSnapshot().Preferences, log);
            Check(clock.Elapsed < TimeSpan.FromSeconds(45), "Event settings scenario exceeded its 45-second budget.");
            log($"event-settings-pass: positions=8; limits=1/5/10; nativeAutomationProviders=true; fractionalAndLockedSaveRollback=true; stableRows=4; independentReadback=true; elapsedMs={clock.ElapsedMilliseconds}; evidence={root}");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            bool cleaned = host.Shutdown();
            window.Close();
            if (failure is null) Check(cleaned, "Event settings Host cleanup failed.");
            log($"event-settings-cleanup: hostStopped={cleaned}; settingsClosed=true");
        }

        async Task Until(Func<bool> predicate, string message)
        {
            var wait = Stopwatch.StartNew();
            while (!predicate())
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(8) || clock.Elapsed > TimeSpan.FromSeconds(35)) throw new InvalidOperationException(message);
                await Task.Delay(25);
            }
        }
    }

    private static async Task SelectPosition(ComboBox combo, FlyoutPosition position)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(combo) as ComboBoxAutomationPeer
            ?? throw new InvalidOperationException("Event position ComboBox did not expose its native automation peer.");
        var expand = peer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
            ?? throw new InvalidOperationException("Event position ComboBox did not expose ExpandCollapse.");
        expand.Expand();
        try
        {
            await Task.Delay(30); // Let the real ComboBox popup realize its item containers.
            var item = combo.Items.OfType<ComboBoxItem>().Single(value => Equals(value.Tag, position.ToString()));
            // Selection is a data-item pattern associated with the owning selector.
            // A manually constructed container peer has no ItemsControl owner and may return null.
            var itemPeer = peer.CreateItemAutomationPeer(item);
            var selection = itemPeer?.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                ?? throw new InvalidOperationException($"Event position {position} did not expose SelectionItem through its ComboBox item peer ({itemPeer?.GetType().Name ?? "null"}).");
            selection.Select();
            Check(selection.IsSelected && ReferenceEquals(combo.SelectedItem, item), "Native event position SelectionItem provider did not select its item.");
        }
        finally { expand.Collapse(); }
    }

    private static BrokerStateStore DeclaredEvents()
    {
        var states = new BrokerStateStore(["event-settings", "event-settings-other"]);
        foreach (var app in new[] { "event-settings", "event-settings-other" })
        {
            states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = app, SessionId = "settings-session" });
            var result = states.Handle(new()
            {
                Kind = MessageKind.Declare, ApplicationId = app, SessionId = "settings-session",
                Declaration = new(app, [new("main", [new("component", [new("activate")])], [new("panel", [new("activate")])],
                    Hints: [new("notice", FlyoutKind.ShortHint)],
                    EventChannels: [new("event", EventClosePolicy.AutoClose), new("persistent", EventClosePolicy.Persistent)])]),
                State = new(0, [new("main", "component", "value")])
            }).Result;
            Check(result?.Accepted == true, "Native event settings declaration was rejected: " + result);
        }
        return states;
    }

    private static async Task IndependentReadback(string path, HostSettingsPreferences expected, Action<string> log)
    {
        using var child = new Process { StartInfo = new(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } };
        child.StartInfo.ArgumentList.Add("--settings-readback");
        child.StartInfo.ArgumentList.Add(path);
        Check(child.Start(), "Event settings readback child did not start.");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync(deadline.Token);
            Check(child.ExitCode == 0, "Independent event settings process failed.");
            var restored = JsonSerializer.Deserialize<HostSettingsPreferences>(File.ReadAllText(path + ".readback.json"));
            Check(restored is not null && restored.Events == expected.Events && restored.EventVisibility is not null &&
                expected.EventVisibility is not null && restored.EventVisibility.Count == expected.EventVisibility.Count &&
                expected.EventVisibility.All(pair => restored.EventVisibility.TryGetValue(pair.Key, out bool visible) && visible == pair.Value),
                "Independent process did not restore complete event preferences and full-identity visibility.");
            log($"event-settings-readback: pid={child.Id}; exit={child.ExitCode}; eventRows={restored!.EventVisibility!.Count}");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await child.WaitForExitAsync(cleanup.Token);
                log($"event-settings-readback-cleanup: pid={child.Id}; forcedExit=true");
            }
        }
    }
    private static HostSettingsPreferences Read(string path)
    {
        var result = new LocalHostSettingsPreferenceStore(path).Load();
        Check(result.IsSuccess, "Native event settings readback failed: " + result.Error);
        return result.Value!;
    }
    private static void Navigate(Window window, string page) => Find<NavigationView>(window, "mtp-settings-navigation").SelectedItem =
        Find<NavigationViewItem>(window, "mtp-settings-nav-" + page);
    private static void Toggle(ToggleSwitch value) => ((IToggleProvider)new ToggleSwitchAutomationPeer(value).GetPattern(PatternInterface.Toggle)).Toggle();
    private static T Find<T>(Window window, string id) where T : FrameworkElement => Descendants(window.Content).OfType<T>()
        .Single(value => AutomationProperties.GetAutomationId(value) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var item in Descendants(VisualTreeHelper.GetChild(root, index))) yield return item; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

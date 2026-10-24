using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Host.Flyouts;
using Mtp.Host.Islands;
using Mtp.Platform.Core;
using Mtp.Contracts;

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
        IReadOnlyList<TaskbarDockDisplay> OwnedDisplays() => [new("owned-primary", true,
            new(-30000, -30000, 1000, 800), new(-30000, -30000, 1000, 720), NativeWindows.GetDpiForWindow(target.Parent))];
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")),
            target.Capture, root, displayProvider: OwnedDisplays);
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(path), () => host.Applications,
            host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        var window = new SettingsWindow(settings, host, OwnedDisplays);
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
            await Until(() => ((FrameworkElement)window.Content).ActualTheme == ElementTheme.Dark, "Dark theme did not reach the settings root.");
            var solidRoot = (Grid)window.Content;
            log("ticket16-settings-surface: " + JsonSerializer.Serialize(new
            {
                requested = settings.GetSnapshot().Preferences.Appearance,
                actualTheme = solidRoot.ActualTheme.ToString(),
                rootBackground = (solidRoot.Background as SolidColorBrush)?.Color.ToString(),
                backdrop = window.SystemBackdrop?.GetType().Name,
                titleForeground = (Descendants(solidRoot).OfType<TextBlock>().First(value => value.Name == "PageTitle").Foreground as SolidColorBrush)?.Color.ToString(),
            }));
            Check(window.SystemBackdrop is null, "Settings shell inherited taskbar/flyout material");
            Check(solidRoot.Background is SolidColorBrush { Color.A: 255 }, "Settings page must remain opaque while global material is semi-transparent");
            foreach (var theme in new[] { HostTheme.Light, HostTheme.Dark })
            foreach (var material in MaterialResolver.SelectionOrder)
            foreach (double opacity in material == MaterialKind.None ? new[] { 1d } : new[] { 0d, 0.8d })
            {
                Check(settings.SetAppearance(new(theme, material, opacity)).IsSuccess, "Global appearance setup failed");
                await Until(() => solidRoot.ActualTheme == (theme == HostTheme.Dark ? ElementTheme.Dark : ElementTheme.Light), "Settings theme did not update");
                await Task.Delay(25);
                Check(window.SystemBackdrop is null && solidRoot.Background is SolidColorBrush { Color.A: 255 }, "Global material changed the opaque settings page");
                var color = ((SolidColorBrush)solidRoot.Background).Color;
                Check(theme == HostTheme.Dark ? color.R < 80 : color.R > 200, "Settings surface is inconsistent with its actual theme");
            }
            Check(settings.SetAppearance(new(HostTheme.Dark, MaterialKind.Solid, 0.6)).IsSuccess, "Restore fixture appearance failed");
            await Until(() => solidRoot.ActualTheme == ElementTheme.Dark, "Restore fixture theme failed");
            log("followup-settings-pass: opaque native theme page unaffected by all four global materials, zero opacity, and live theme switching");
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
            var nativeHost = Field<ContentIslandHost>(adapter, "host");
            var source = Field<Microsoft.UI.Xaml.Hosting.DesktopWindowXamlSource>(nativeHost, "source");
            log("ticket16-island-surface: " + JsonSerializer.Serialize(new { actualTheme = nativeHost.ContentRoot?.ActualTheme.ToString(),
                material = nativeHost.Configuration, backdrop = source.SystemBackdrop?.GetType().Name }));
            Check(source.SystemBackdrop is not null, "Solid island material must tint the compositor instead of blending XAML alpha with its white base.");
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
            await Until(() => nativeHost.ContentRoot is not null && Descendants(nativeHost.ContentRoot).OfType<Slider>()
                .Any(value => AutomationProperties.GetAutomationId(value) == "mtp-template-level-slider" && value.IsLoaded),
                "Production component slider did not load.");
            var slider = Descendants(nativeHost.ContentRoot!).OfType<Slider>()
                .Single(value => AutomationProperties.GetAutomationId(value) == "mtp-template-level-slider");
            double lastConfirmed = slider.Value;
            ((IRangeValueProvider)new SliderAutomationPeer(slider).GetPattern(PatternInterface.RangeValue)).SetValue(95);
            await Until(() => session.Actions.GetLastErrorHint("counter")?.Result.Code == "DemoValueRejected",
                "Production slider did not retain its business rejection.");
            var rejected = session.Actions.GetLastErrorHint("counter")!.Result;
            Check(!rejected.Accepted && rejected.Code == "DemoValueRejected", "Business rejection fixture did not reject 95.");
            await Until(() => slider.Value == lastConfirmed && slider.IsEnabled, "Production rejection did not restore the last confirmed slider value.");
            var coordinator = Field<TaskbarFlyoutCoordinator>(host, "flyouts");
            await Until(() => coordinator.Hints.Inspect().Any(hint => !hint.Closing && hint.Text == rejected.Message),
                "Production slider rejection did not create its transient error hint.");
            var rejectionHint = coordinator.Hints.Inspect().Single(hint => !hint.Closing && hint.Text == rejected.Message);
            await Until(() => coordinator.Hints.WindowForTesting(rejectionHint.Generation) is { IsVisible: true } &&
                FlyoutNative.ReadOpacity(rejectionHint.Handle) == 255,
                "Production business rejection hint never became natively visible.");
            log("settings-business-short-hint: " + JsonSerializer.Serialize(new { rejectionHint.Generation, handle = rejectionHint.Handle.ToInt64(), rejectionHint.Text, nativeVisible = true, slider.Value }));
            await Until(() => host.CurrentError == rejected.Message, "Business rejection lost its diagnostic history.");
            Navigate(window, navigation, "global");
            await Until(() => settings.GetSnapshot().Navigation.Current == SettingsPage.Global, "Global did not reopen.");
            CheckNoBusinessNotice(window, rejected.Message);
            Check(!Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Business rejection must not be labelled as a failed settings save.");
            ((IRangeValueProvider)new SliderAutomationPeer(slider).GetPattern(PatternInterface.RangeValue)).SetValue(60);
            await Until(() => session.States.GetSnapshot("counter")!.State!.TemplateEntries!
                .Single(entry => entry.Entry is { FeatureGroupId: "main", Kind: TemplateEntryKind.Component, EntryId: "counter" })
                .Fields.Single(field => field.FieldId == "level").Value.Number == 60 && slider.Value == 60 && slider.IsEnabled,
                "Production slider did not confirm business recovery to 60.");
            Navigate(window, navigation, "applications");
            Navigate(window, navigation, "global");
            CheckNoBusinessNotice(window, rejected.Message);
            Check(host.CurrentError == rejected.Message, "Successful business action unexpectedly erased historical diagnostics.");
            log("settings-business-notice: rejected95/accepted60; globalBusinessNotice=false; historicalDiagnostic=true");
            Find<ComboBox>(window, "mtp-settings-theme").SelectedIndex = 1;
            await Until(() => settings.GetSnapshot().Preferences.Appearance.Theme == HostTheme.Light, "Light preference not saved.");
            Check(adapter.Handle == hwnd, "Theme preference rebuilt the content island.");
            Check(!Find<InfoBar>(window, "mtp-settings-error").IsOpen, "Successful preference change retained a false settings error.");
            Check(host.CurrentError == rejected.Message, "Changing settings unexpectedly cleared the historical business diagnostic.");
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
            // A real adapter initialization failure must remain explainable in the
            // settings shell; suppressing business notices cannot hide platform errors.
            foreach (var row in settings.GetSnapshot().Components)
                Check(settings.SetVisibility(row.Identity, false).IsSuccess, "Platform failure fixture could not hide components.");
            await Until(() => host.Session.State == IslandDisplayState.Hidden, "Platform failure fixture did not release the island.");
            adapter.FailAfter = "source";
            var counterIdentity = rows.Single(value => value.Identity.Segments[0].Value == "counter" && value.Identity.LocalId.Value == "counter").Identity;
            Check(settings.SetVisibility(counterIdentity, true).IsSuccess, "Platform failure fixture could not show counter.");
            await Until(() => host.Session.Error?.Code == "island_embed_failed", "Real adapter failure was not exposed.");
            Navigate(window, navigation, "applications");
            Check(Descendants(window.Content).OfType<InfoBar>().Any(bar => bar.IsOpen && bar.Message.Contains(host.Session.Error!.Message, StringComparison.Ordinal)),
                "Settings shell hid a real platform failure while suppressing business notices.");
            adapter.FailAfter = null;
            host.Retry();
            await Until(() => host.Session.State == IslandDisplayState.Embedded, "Platform fixture did not recover after explicit retry.");
            Navigate(window, navigation, "global");
            CheckNoBusinessNotice(window, rejected.Message);
            log("settings-platform-notice: actualAdapterFailure=visible; explicitRetry=recovered; businessNotice=false");
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
    private static void CheckNoBusinessNotice(Window window, string reason) => Check(
        !Descendants(window.Content).OfType<InfoBar>().Any(bar => bar.IsOpen && bar.Message.Contains(reason, StringComparison.Ordinal)),
        "Business rejection was projected into the settings shell instead of remaining a transient short hint.");
    private static T Find<T>(Window window, string id) where T : FrameworkElement => Descendants(window.Content).OfType<T>().First(value => AutomationProperties.GetAutomationId(value) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var item in Descendants(VisualTreeHelper.GetChild(root, index))) yield return item; }
    private static T Field<T>(object value, string name) where T : class => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static async Task Until(Func<bool> predicate, string message)
    { var watch = Stopwatch.StartNew(); while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException(message); await Task.Delay(25); } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

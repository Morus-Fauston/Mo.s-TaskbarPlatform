using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Mtp.Host.Islands;
using Mtp.Platform.Core;
using System.Text.Json;

namespace Mtp.Host.WindowTests;

internal static class HostConsoleRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "console-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        var loaded = display.Load();
        using var target = new OwnedIslandTarget();
        var controller = new HostConsoleController(display, loaded, new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")), target.Capture, Path.Combine(root, "evidence"));
        var window = new MainWindow(controller, () => [new("owned-primary", true, new(-30000, -30000, 1000, 800), new(-30000, -30000, 1000, 720), 96)]);
        var view = (FrameworkElement)window.Content;
        T Find<T>(string name) => (T)view.FindName(name);
        void Click(string name)
        {
            var button = Find<Button>(name);
            Check(button.IsEnabled, "Button disabled: " + name);
            new ButtonAutomationPeer(button).Invoke();
        }
        async Task ClickAsync(string name) { Click(name); await Task.Delay(100); }
        try
        {
            window.AppWindow.Move(new(-30000, -30000));
            window.AppWindow.Show(false);
            Find<ToggleSwitch>("VisibilityToggle").IsOn = true;
            controller.Refresh();
            await Task.Delay(200);
            Check(controller.Session.State == IslandDisplayState.Embedded, "Declaration did not reach real island: " + string.Join(";", controller.Errors));
            var nav = Find<NavigationView>("Navigation");
            for (var i = 0; i < 4; i++) { nav.SelectedItem = nav.MenuItems[i]; await Task.Delay(50); }
            nav.SelectedItem = nav.MenuItems[0];
            await ClickAsync("SimulateButton");
            Check(controller.Session.State != IslandDisplayState.Embedded, "Failure injection did not remove island");
            await ClickAsync("ClearSimulationButton");
            Check(controller.Session.State == IslandDisplayState.Embedded, "Clear injection did not recover");
            Check(Find<TextBlock>("ErrorText").Text.Contains("island_test_unavailable"), "Recovery erased error history");
            await ClickAsync("RetryButton");
            Check(controller.Session.State == IslandDisplayState.Embedded, "Manual retry failed");
            target.Destroy(); controller.Refresh(); await Task.Delay(100);
            target.Recreate(); controller.Refresh(); await Task.Delay(100);
            Check(controller.Session.State == IslandDisplayState.Embedded, "Parent replacement did not recover");
            log("PASS: real Host console injection, retry, parent loss/recovery and persistent errors.");
            await IslandPreviewRegression.RunAsync(window, controller, root, log);

            foreach (var theme in new[] { 0, 1, 2 })
            {
                foreach (var variant in new[] { (0, 0), (0, 1), (0, 2), (1, 2), (2, 2) })
                {
                    nav.SelectedItem = nav.MenuItems[1];
                    Find<ComboBox>("ThemeCombo").SelectedIndex = theme;
                    Find<ComboBox>("MaterialCombo").SelectedIndex = variant.Item1;
                    Find<ComboBox>("AlphaCombo").SelectedIndex = variant.Item2;
                    await ClickAsync("StartCaseButton");
                    var materialRun = controller.Tests.Run!;
                    Check(controller.Session.State == IslandDisplayState.Embedded, "Material case lost island");
                    await ClickAsync("StopCaseButton");
                    var events = File.ReadAllText(Path.Combine(materialRun.DirectoryPath, "events.jsonl"));
                    Check(events.Contains("island-observed"), "Actual native host state not captured");
                    using var native = JsonDocument.Parse(File.ReadLines(Path.Combine(materialRun.DirectoryPath, "events.jsonl")).Single(line => line.Contains("island-observed")));
                    var observed = native.RootElement.GetProperty("Value");
                    Check(observed.GetProperty("IsAlive").GetBoolean() && observed.GetProperty("Configuration").GetProperty("Material").GetString() == materialRun.Configuration.Material,
                        "Evidence did not bind actual host material");
                }
            }
            log("PASS: all 15 material/alpha/theme combinations route through actual Host island; appearance remains untested.");
            Find<ComboBox>("ThemeCombo").SelectedIndex = 0;
            Find<ComboBox>("MaterialCombo").SelectedIndex = 0;
            Find<ComboBox>("AlphaCombo").SelectedIndex = 0;
            nav.SelectedItem = nav.MenuItems[2];
            Find<ComboBox>("DurationCombo").SelectedIndex = 1;
            for (var load = 0; load < 4; load++)
            {
                Find<ComboBox>("LoadCombo").SelectedIndex = load;
                nav.SelectedItem = nav.MenuItems[1];
                await ClickAsync("OpenControlsButton");
                Check(controller.Tests.Configuration.DurationSeconds == 1800, "Long preset not routed");
                nav.SelectedItem = nav.MenuItems[2];
                await ClickAsync("StartSamplingButton");
                await Task.Delay(1150);
                await ClickAsync("StopSamplingButton");
                var samples = controller.Tests.Run!.Samples;
                Check(load == 0 ? samples[^1].Updates == 0 : samples[^1].Updates > 0, "Requested load not applied");
                nav.SelectedItem = nav.MenuItems[1];
                await ClickAsync("StopCaseButton");
            }
            Find<ComboBox>("DurationCombo").SelectedIndex = 0;
            controller.Tests.Start(new(DurationSeconds: 1, Controls: true));
            controller.Tests.StartSampling();
            await Task.Delay(1300);
            Check(!controller.Tests.IsSampling, "Duration limit failed to stop real sampling timer");
            controller.Tests.Stop("duration-fixture");
            log("PASS: 0/1/30/60 Hz load routes, long preset selection and real timer duration/stop; no 30-minute claim.");

            Find<ComboBox>("LoadCombo").SelectedIndex = 1;
            await ClickAsync("OpenControlsButton");
            var run = controller.Tests.Run!;
            Check(run.Configuration.Controls && run.Configuration.Hertz == 1, "UI configuration did not reach island case");
            controller.Tests.Start(new("mica", 1));
            Check(ReferenceEquals(run, controller.Tests.Run), "Repeated start replaced active run");
            Check(!Find<Button>("StartCaseButton").IsEnabled && !Find<ComboBox>("MaterialCombo").IsEnabled, "Conflicting controls enabled");
            await ClickAsync("OpenPopupButton");
            Check(controller.PopupOpen, "Popup not opened on actual island");
            await ClickAsync("ClosePopupButton");
            Check(!controller.PopupOpen, "Popup not closed");
            nav.SelectedItem = nav.MenuItems[2];
            await ClickAsync("StartSamplingButton");
            controller.Tests.StartSampling();
            await Task.Delay(1200);
            // These records are fixture actions, explicitly not a human visibility observation.
            await ClickAsync("MarkHiddenButton");
            await ClickAsync("MarkRestoredButton");
            await ClickAsync("StopSamplingButton");
            Check(!controller.Tests.IsSampling && run.Samples.Count >= 3, "Sampling failed or still running");
            var count = run.Samples.Count;
            await Task.Delay(1100);
            Check(run.Samples.Count == count, "Stopped sampling accepted late timer tick");
            nav.SelectedItem = nav.MenuItems[3];
            Find<TextBox>("ResultNote").Text = "AUTOMATED FIXTURE ONLY; real appearance untested";
            await ClickAsync("RecordResultButton");
            await ClickAsync("ExportButton");
            string? opened = null;
            controller.OpenDirectory = path => opened = path;
            await ClickAsync("OpenEvidenceButton");
            Check(opened == run.DirectoryPath, "Evidence button targets wrong directory");
            using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(opened!, "report.json"))))
            {
                Check(json.RootElement.GetProperty("Configuration").GetProperty("Hertz").GetInt32() == 1, "Report configuration wrong");
                Check(json.RootElement.GetProperty("VisibilityMarkers").GetArrayLength() == 2, "Markers missing");
                Check(json.RootElement.GetProperty("Results").GetProperty("gpu").GetProperty("Status").GetString() == "untested", "Missing metric incorrectly passed");
            }
            Find<ToggleSwitch>("VisibilityToggle").IsOn = false;
            controller.SetGap(17);
            controller.SetSimulation(true);
            await ClickAsync("StopCaseButton");
            Check(controller.Session.State == IslandDisplayState.Hidden && !controller.Component!.IsVisible, "Stop overwrote user's latest hidden preference");
            Check(controller.Preferences.RightGapDip == 17 && !controller.SimulateUnavailable, "Stop retained injection or overwrote new placement preference");
            Check(run.EndedAt is not null && !controller.Tests.IsRunning, "Case not finalized");
            log("PASS: Host controls/Popup, case exclusion, sampling/cancel, raw report readback and directory target.");
            log("Evidence: " + root);

            controller.SetVisibility(true);
            controller.Tests.Start(new(Controls: true));
            controller.Tests.StartSampling();
            var failedEvidence = controller.Tests.Run!;
            using (var locked = new FileStream(Path.Combine(failedEvidence.DirectoryPath, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                controller.Execute(() => controller.Tests.Stop("evidence-io-fixture"));
                Check(!controller.Tests.IsRunning && !controller.Tests.IsSampling && failedEvidence.EndedAt is not null,
                    "Evidence I/O failure prevented case cleanup");
            }
            Check(controller.Tests.LastError is not null, "Evidence I/O failure was not visible");
            log("PASS: evidence write failure does not prevent sample/island cleanup or case completion.");

            foreach (var size in new[] { (1000, 960), (560, 960), (1000, 500), (560, 500) })
            {
                window.AppWindow.Resize(new(size.Item1, size.Item2));
                for (var section = 0; section < 4; section++)
                {
                    nav.SelectedItem = nav.MenuItems[section];
                    await Task.Delay(80);
                    await ConsoleScreenshot.SaveAsync(window, Path.Combine(root, $"console-{size.Item1}x{size.Item2}-{section}.png"));
                }
            }

            Find<ToggleSwitch>("VisibilityToggle").IsOn = true;
            nav.SelectedItem = nav.MenuItems[1];
            await ClickAsync("StartCaseButton");
            await ClickAsync("StartSamplingButton");
            var final = controller.Tests.Run!;
            window.Close();
            await Task.Delay(150);
            Check(!controller.Tests.IsSampling && final.Outcome == "host-closed" && controller.Session.State == IslandDisplayState.Closed,
                $"Host close did not stop resources: sampling={controller.Tests.IsSampling}, outcome={final.Outcome}, state={controller.Session.State}");
            log("PASS: Host close stops sampling and island; fixture does not validate human taskbar behavior.");
            var previewDisplay = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
                new LocalComponentDisplayPreferenceStore(Path.Combine(root, "preview-display.json")));
            var previewLoaded = previewDisplay.Load();
            var previewController = new HostConsoleController(previewDisplay, previewLoaded,
                new LocalTaskbarDockPreferenceStore(Path.Combine(root, "preview-dock.json")), target.Capture, Path.Combine(root, "preview-evidence"));
            var previewOwner = new MainWindow(previewController, () => []);
            try
            {
                previewOwner.AppWindow.Move(new(-30000, -30000));
                previewController.OpenPreview(WinRT.Interop.WindowNative.GetWindowHandle(previewOwner), new(Controls: true));
                var previewHandle = previewController.Preview.Handle;
                var previewBridge = previewController.Preview.Bridge;
                previewController.Preview.SetPopup(true);
                previewOwner.Close();
                await Task.Delay(150);
                Check(!NativeWindows.IsWindow(previewHandle) && !NativeWindows.IsWindow(previewBridge) && !previewController.Preview.PopupOpen,
                    "Host close left preview/bridge/Popup alive");
                log("PASS: Host close releases independent preview, its island and Popup.");
            }
            finally { previewController.Shutdown(); previewOwner.Close(); }
        }
        finally { controller.Shutdown(); window.Close(); }
    }

    public static async Task RunLifetimeAsync(Action<string> log)
    {
        var component = HostComponentDisplayModel.From(new Component(new StableIdentity(new StableId("native-fixture")), CapabilityState.Available));
        using var target = new OwnedIslandTarget();
        foreach (var failure in new[] { "host", "source", "attach" })
        {
            var host = new ContentIslandHost((_, _) => { });
            try { host.Start(target.Parent, new(-29700, -29270, 360, 48), component, new(), failure); throw new InvalidOperationException("Injection did not fail"); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Injected")) { }
            host.Close(); host.Close();
            Check(!NativeWindows.IsWindow(host.Handle) && !NativeWindows.IsWindow(host.Bridge), "Failed initialization leaked native handles");
        }
        var island = new ContentIslandHost((_, _) => { });
        island.Start(target.Parent, new(-29700, -29270, 360, 48), component, new());
        await Task.Delay(100);
        var oldHost = island.Handle; var oldBridge = island.Bridge;
        Check(NativeWindows.GetParent(oldBridge) == oldHost && NativeWindows.GetParent(oldHost) == target.Parent, "DWXS direct parent not MTP host");
        target.Destroy();
        island.Close(); island.Close();
        target.Recreate();
        island.Start(target.Parent, new(-29700, -29270, 360, 48), component, new());
        await Task.Delay(100);
        Check(island.IsAlive, "Late destroyed-parent event invalidated new instance");
        island.Close();
        log("PASS: production island initialization/binding failure, parent destruction, idempotent cleanup and recreation.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

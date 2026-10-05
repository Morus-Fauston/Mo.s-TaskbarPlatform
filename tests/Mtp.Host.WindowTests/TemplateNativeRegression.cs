using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class TemplateNativeRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        await RegisteredImageCacheRegression.RunAsync(log);
        string root = Path.Combine(AppContext.BaseDirectory, "template-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        using var target = new OwnedIslandTarget();
        var controller = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")),
            target.Capture, Path.Combine(root, "evidence"));
        var window = new MainWindow(controller, () => [new("owned-primary", true, new(-30000, -30000, 1000, 800), new(-30000, -30000, 1000, 720), 96)]);
        var processes = new List<Process>();
        nint handle = 0, bridge = 0;
        try
        {
            window.AppWindow.Move(new(-30000, -30000));
            window.AppWindow.Show(false);
            await controller.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), templates: true).WaitAsync(TimeSpan.FromSeconds(12));
            var host = Field<HostBrokerSession>(controller, "communication");
            foreach (int pid in host.ServiceProcessIds.Prepend(host.BrokerProcessId))
            {
                var process = Process.GetProcessById(pid); _ = process.Handle; processes.Add(process);
            }
            await Until(() => controller.Component?.HasTemplate == true, "Template declaration did not reach production projection.");
            controller.Tests.Start(new(Alpha: 1));
            controller.SetVisibility(true);
            await Until(() => controller.Session.State == IslandDisplayState.Embedded, "Template island was not embedded.");
            var adapter = Field<IslandDisplayAdapter>(controller, "adapter");
            var island = Field<ContentIslandHost>(adapter, "host");
            handle = island.Handle; bridge = island.Bridge;
            await Until(() => island.ContentRoot?.IsLoaded == true && Find<TextBlock>(island, "count-text") is not null, "Template controls were not loaded.");
            var text = Find<TextBlock>(island, "count-text")!;
            var button = Find<Button>(island, "increment")!;
            var icon = Find<Button>(island, "icon-increment")!;
            var toggle = Find<ToggleSwitch>(island, "enabled-toggle")!;
            var slider = Find<Slider>(island, "level-slider")!;
            Check(button is not null && icon is not null && toggle is not null && slider is not null, "Missing first-batch native controls.");
            await Until(() => Find<FrameworkElement>(island, "status-image") is { } imageRoot &&
                Descendants(imageRoot).OfType<Image>().Any(image => image.Source is not null), "Registered PNG failed to decode in real UI.");
            foreach (var element in new FrameworkElement[] { button!, icon!, toggle!, slider! })
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)), "Interactive control lacks accessible name.");
            string first = text.Text;
            await Until(() => text.Text != first, "Confirmed state did not refresh native text.");
            Check(ReferenceEquals(text, Find<TextBlock>(island, "count-text")) && handle == island.Handle, "Value refresh rebuilt native content.");
            var oldSession = host.States.GetSnapshot("counter")!.SessionId;
            var stale = await host.SendActionAsync(new("counter", "main", ActionEntryKind.Component, "counter", "activate"), new(),
                expectedSessionId: "old-" + oldSession);
            Check(stale.Code == "StaleSession", "Delayed old-session UI intent reached current service.");
            double delta = Delta(host);
            ((IInvokeProvider)new ButtonAutomationPeer(button!).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => Delta(host) == delta + 9, "Native button did not confirm exactly one action.");
            Check(handle == island.Handle && bridge == island.Bridge, "Button action replaced the native island.");
            ((IToggleProvider)new ToggleSwitchAutomationPeer(toggle!).GetPattern(PatternInterface.Toggle)).Toggle();
            await Until(() => FieldValue(host, "enabled").Boolean == false && !toggle!.IsOn, "Toggle request did not reach shared confirmed state.");
            ((IRangeValueProvider)new SliderAutomationPeer(slider!).GetPattern(PatternInterface.RangeValue)).SetValue(35);
            await Until(() => FieldValue(host, "level").Number == 35 && slider!.Value == 35 && slider.IsEnabled, "Slider request did not confirm normalized value.");
            ((IRangeValueProvider)new SliderAutomationPeer(slider!).GetPattern(PatternInterface.RangeValue)).SetValue(95);
            await Until(() => host.Actions.GetLastErrorHint("counter")?.Result.Code == "DemoValueRejected", "Business failure did not retain an error intent.");
            await Until(() => slider!.Value == 35 && slider.IsEnabled, "Rejected slider target did not roll back to confirmation and unlock.");
            Check(FieldValue(host, "level").Number == 35, "Rejected target changed shared confirmation.");
            ((FrameworkElement)island.ContentRoot!).RequestedTheme = ElementTheme.Dark;
            await Task.Delay(100);
            Check(Descendants(island.ContentRoot!).OfType<Grid>().Any(grid => grid.Background is SolidColorBrush { Color.A: 255, Color.R: 32 }),
                "Template bypassed the opaque dark Host surface.");
            await ConsoleScreenshot.SaveAsync(island.ContentRoot!, Path.Combine(root, "template-dark.png"));
            ((FrameworkElement)island.ContentRoot!).RequestedTheme = ElementTheme.Light;
            await Task.Delay(100);
            Check(Descendants(island.ContentRoot!).OfType<Grid>().Any(grid => grid.Background is SolidColorBrush { Color.A: 255, Color.R: 243 }),
                "Template bypassed the opaque light Host surface.");
            await ConsoleScreenshot.SaveAsync(island.ContentRoot!, Path.Combine(root, "template-light.png"));
            Check(ReferenceEquals(slider, Find<Slider>(island, "level-slider")), "Theme change rebuilt control tree.");
            log($"PASS: production controlled template -> real native Text/Image/Button/IconButton/Toggle/Slider; sameHwnd={handle}; staleSessionRejected=true; confirmedLevel=35; failed95RolledBack=true; screenshots={root}");
            controller.SetVisibility(false);
            Check(!NativeWindows.IsWindow(handle) && !NativeWindows.IsWindow(bridge), "Hiding entry retained its content island.");
            var afterHide = FieldValue(host, "enabled").Boolean;
            toggle!.IsOn = !toggle.IsOn;
            slider!.Value = 65;
            await Task.Delay(250);
            Check(FieldValue(host, "enabled").Boolean == afterHide && FieldValue(host, "level").Number == 35,
                "Retained controls emitted late actions after entry was hidden.");
            log("PASS: hidden template controls cannot dispatch late input; leases and entry generation released.");
        }
        finally
        {
            Check(controller.Shutdown(), "Template Host cleanup reported failure.");
            window.Close();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var process in processes)
            {
                await process.WaitForExitAsync(cleanup.Token);
                Check(process.HasExited, "Template service did not exit.");
                log($"templateOwnedPid={process.Id}; exited=true"); process.Dispose();
            }
            Check(!NativeWindows.IsWindow(handle) && !NativeWindows.IsWindow(bridge) && NativeWindows.IsWindow(target.Parent), "Template cleanup violated native ownership.");
        }
    }
    private static double Delta(HostBrokerSession host)
    {
        var state = host.States.GetSnapshot("counter")!.State!;
        return state.Components.Single().Number!.Value - state.Revision;
    }
    private static TemplateValue FieldValue(HostBrokerSession host, string id) =>
        host.States.GetSnapshot("counter")!.State!.TemplateEntries!.Single().Fields.Single(field => field.FieldId == id).Value;
    private static T Field<T>(object value, string name) where T : class =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static T? Find<T>(ContentIslandHost island, string id) where T : FrameworkElement =>
        island.ContentRoot is null ? null : Descendants(island.ContentRoot).OfType<T>().FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == "mtp-template-" + id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        yield return value;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(value); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(value, index))) yield return child;
    }
    private static async Task Until(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException(message); await Task.Delay(25); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

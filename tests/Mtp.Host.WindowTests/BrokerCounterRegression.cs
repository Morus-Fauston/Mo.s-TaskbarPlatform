using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>Production SDK/Broker readings rendered by a real island attached only to our own window.</summary>
internal static class BrokerCounterRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        var brokerPath = Path.Combine(AppContext.BaseDirectory, "Broker", "Mtp.Broker.dll");
        var counterPath = Path.Combine(AppContext.BaseDirectory, "CounterService", "Mtp.CounterService.dll");
        Check(File.Exists(brokerPath) && File.Exists(counterPath),
            "Missing copied Broker/CounterService runtime: " + brokerPath + "; " + counterPath);

        var root = Path.Combine(AppContext.BaseDirectory, "broker-counter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var display = new HostDisplayController(
            new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        var loaded = display.Load();
        using var target = new OwnedIslandTarget();
        var controller = new HostConsoleController(display, loaded,
            new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")),
            target.Capture, Path.Combine(root, "evidence"));
        var window = new MainWindow(controller, () =>
            [new("owned-primary", true, new(-30000, -30000, 1000, 800), new(-30000, -30000, 1000, 720), 96)]);
        var processes = new List<Process>();
        try
        {
            window.AppWindow.Move(new(-30000, -30000));
            window.AppWindow.Show(false);
            await controller.StartCounterAsync(brokerPath, counterPath).WaitAsync(TimeSpan.FromSeconds(20));

            var communication = Field<HostBrokerSession>(controller, "communication");
            var pids = communication.ServiceProcessIds.Prepend(communication.BrokerProcessId).ToArray();
            Check(pids.Length == 2 && pids.Distinct().Count() == 2 && pids.All(id => id != Environment.ProcessId),
                "Counter, Broker and native Host test must be three distinct real processes.");
            foreach (var pid in pids)
            {
                var process = Process.GetProcessById(pid);
                processes.Add(process);
                _ = process.Handle; // Pin this owned process identity for exit verification, independent of PID reuse.
                Check(!process.HasExited, "Owned runtime exited before native rendering: " + pid);
            }
            log($"Broker native fixture: hostPid={Environment.ProcessId}; brokerPid={pids[0]}; counterPid={pids[1]}; preferences={root}");

            // Wait for the production controller timer, rather than injecting a snapshot or calling UpdateConfirmed.
            await WaitUntilAsync(() => TryReading(controller, out _), TimeSpan.FromSeconds(12),
                () => "Confirmed counter did not reach the display model: " + string.Join("; ", controller.Errors));
            // Visibility belongs to the accepted counter identity; the earlier local declaration has a different preference.
            controller.SetVisibility(true);
            await WaitUntilAsync(() => controller.Session.State == IslandDisplayState.Embedded, TimeSpan.FromSeconds(5),
                () => "Confirmed counter did not reach the native island: " + string.Join("; ", controller.Errors));
            var adapter = Field<IslandDisplayAdapter>(controller, "adapter");
            var island = Field<ContentIslandHost>(adapter, "host");
            var handle = island.Handle;
            var bridge = island.Bridge;
            Check(island.IsAlive && NativeWindows.GetParent(handle) == target.Parent &&
                NativeWindows.GetParent(bridge) == handle, "Counter island is not attached through the owned native parent chain.");
            await WaitUntilAsync(() => island.ContentRoot?.IsLoaded == true,
                TimeSpan.FromSeconds(5), () => "Counter island XAML content never loaded.");

            var label = Descendants(island.ContentRoot!).OfType<TextBlock>()
                .Single(text => AutomationProperties.GetName(text) == "声明组件");
            Check(TryReading(controller, out var first), "Initial counter display was not a confirmed numeric reading.");
            Check(label.Text == controller.Component!.Text + " · " + controller.Component.StatusLabel,
                "Native TextBlock differs from the confirmed display model.");
            log($"Broker native initial: reading={first}; nativeText={label.Text}; hwnd={handle}; bridge={bridge}");

            await WaitUntilAsync(() => TryReading(controller, out var current) && current > first,
                TimeSpan.FromSeconds(8), () => "Production refresh timer did not publish a later confirmed counter reading.");
            Check(TryReading(controller, out var next) && next > first, "Counter reading did not advance.");
            Check(ReferenceEquals(island, Field<ContentIslandHost>(adapter, "host")) &&
                handle == island.Handle && bridge == island.Bridge && island.IsAlive,
                "State-only update recreated or invalidated the native content island.");
            Check(label.Text == controller.Component!.Text + " · " + controller.Component.StatusLabel,
                "Later confirmed model update did not reach the existing native TextBlock.");
            var confirmed = communication.States.GetSnapshot("counter")!;
            Check(confirmed.IsInteractive && confirmed.State is not null && confirmed.State.Revision >= next,
                "Rendered reading has no corresponding accepted Broker state.");
            log($"PASS: independent Counter/SDK -> Broker -> production Host timer -> native island; reading={next}; acceptedRevision={confirmed.State!.Revision}; nativeText={label.Text}; sameHwnd={handle}");

            var actionButton = Descendants(island.ContentRoot!).OfType<Button>()
                .SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "MtpDeclaredAction");
            Check(actionButton is not null && actionButton.IsEnabled, "Declared counter action has no enabled native button.");
            var beforeAction = communication.States.GetSnapshot("counter")!.State!;
            var previousDelta = beforeAction.Components.Single().Number!.Value - beforeAction.Revision;
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(actionButton!);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(
                Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await WaitUntilAsync(() => communication.States.GetSnapshot("counter")?.State is { } state &&
                state.Components.Single().Number - state.Revision == previousDelta + 9 &&
                TryReading(controller, out var current) && current >= next + 10,
                TimeSpan.FromSeconds(5), () => "Native action did not receive a confirmed counter increment: " + string.Join("; ", controller.Errors));
            Check(island.Handle == handle && island.Bridge == bridge && island.IsAlive, "Action recreated the native island.");
            Check(label.Text == controller.Component!.Text + " · " + controller.Component.StatusLabel,
                "Action confirmation was not rendered in the native label.");
            log($"PASS: real WinUI Button Invoke -> SDK handler -> confirmed reading {controller.Component.Text}; sameHwnd={handle}.");

            processes[1].Kill(entireProcessTree: true);
            await processes[1].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => controller.Component?.Status == CapabilityStatus.Unavailable && !actionButton!.IsEnabled &&
                communication.ServiceProcessExits.Any(exit => exit.ProcessId == pids[1]), TimeSpan.FromSeconds(5),
                () => "Owned service exit did not disable the native action or record its process identity.");
            var unavailable = communication.States.GetSnapshot("counter")!;
            Check(unavailable.State is not null && controller.Component!.Text == unavailable.State.Components.Single().Text,
                "Service loss discarded the last confirmed reading.");
            Check(island.Handle == handle && island.IsAlive && NativeWindows.IsWindow(target.Parent),
                "Service loss destroyed the existing component or parent.");
            log($"PASS: owned counter exit disables real native button, preserves reading={controller.Component!.Text}, sameHwnd={handle}; reason={unavailable.LastError?.Code}.");

            window.Close();
            await WaitUntilAsync(() => controller.Session.State == IslandDisplayState.Closed &&
                !NativeWindows.IsWindow(handle) && !NativeWindows.IsWindow(bridge), TimeSpan.FromSeconds(5),
                () => "Host window close left its native island or bridge alive.");
            using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(exitDeadline.Token)));
            Check(processes.All(process => process.HasExited), "Host close left an owned Broker or Counter process running.");
            Check(NativeWindows.IsWindow(target.Parent), "Host cleanup destroyed the fixture-owned parent.");
            log("PASS: Host close released owned Broker/Counter processes and native island/bridge; parent ownership preserved. Human taskbar appearance remains untested.");
        }
        finally
        {
            try { controller.Shutdown(); window.Close(); }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        await RunStartupCloseAsync(brokerPath, counterPath, missingBroker: true, log);
        await RunStartupCloseAsync(brokerPath, counterPath, missingBroker: false, log);
    }

    private static async Task RunStartupCloseAsync(string brokerPath, string counterPath, bool missingBroker, Action<string> log)
    {
        var scenario = missingBroker ? "failed-startup" : "startup-cancel";
        var root = Path.Combine(AppContext.BaseDirectory, "broker-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var display = new HostDisplayController(
            new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        using var target = new OwnedIslandTarget();
        var controller = new HostConsoleController(display, display.Load(),
            new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")),
            target.Capture, Path.Combine(root, "evidence"));
        var window = new MainWindow(controller, () => []);
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Task? startup = null;
        try
        {
            window.AppWindow.Move(new(-30000, -30000));
            window.AppWindow.Show(false);
            startup = controller.StartCounterAsync(missingBroker ? Path.Combine(root, "missing-broker.dll") : brokerPath, counterPath);
            if (missingBroker)
            {
                var rejected = false;
                try { await startup.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (FileNotFoundException) { rejected = true; }
                Check(rejected, "Missing Broker executable did not fail startup as expected.");
            }

            // For the cancellation case, close immediately without yielding to the startup continuation.
            window.Close();
            await WaitUntilAsync(() => !NativeWindows.IsWindow(windowHandle) && controller.Session.State == IslandDisplayState.Closed,
                TimeSpan.FromSeconds(5), () => scenario + ": communication startup state prevented native window close.");
            Check(controller.Shutdown(), scenario + ": repeated shutdown reports cleanup failure after resources closed.");
            if (!missingBroker)
            {
                try { await startup.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    log("Expected startup interruption: " + error.GetType().Name);
                }
            }
            Check(NativeWindows.IsWindow(target.Parent), scenario + ": cleanup destroyed a parent it does not own.");
            log("PASS: " + scenario + " permits actual MainWindow close and idempotent cleanup; preferences=" + root);
        }
        finally
        {
            controller.Shutdown();
            window.Close();
            // Observe a fault even if a native assertion failed before the normal await above.
            if (startup is not null)
            {
                try { await startup.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
        }
    }

    private static bool TryReading(HostConsoleController controller, out long reading)
    {
        reading = 0;
        var component = controller.Component;
        return component is not null && component.Identity.Segments[0].Value == "counter" &&
            component.Status == CapabilityStatus.Available &&
            long.TryParse(component.Text, NumberStyles.None, CultureInfo.InvariantCulture, out reading);
    }

    private static T Field<T>(object owner, string name) where T : class =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) as T
        ?? throw new InvalidOperationException($"Missing active {owner.GetType().Name}.{name}.");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> failure)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed >= timeout) throw new InvalidOperationException(failure());
            await Task.Delay(50);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

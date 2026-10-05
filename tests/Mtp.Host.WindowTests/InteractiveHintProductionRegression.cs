using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class InteractiveHintProductionRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static async Task RunAsync(Action<string> log)
    {
        var watch = Stopwatch.StartNew();
        var evidence = Path.Combine(AppContext.BaseDirectory, "interactive-hint-production-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(evidence, "display.json")));
        using var target = new OwnedIslandTarget();
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(evidence, "dock.json")),
            target.Capture, evidence, displayProvider: () => [new("owned-primary", true,
                new(-30000, -30000, 1000, 800), new(-30000, -30000, 1000, 720), NativeWindows.GetDpiForWindow(target.Parent))]);
        host.FlyoutDiagnostics = (kind, data) => log(kind + ": " + JsonSerializer.Serialize(data));
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(Path.Combine(evidence, "settings.json")),
            () => host.Applications, host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        var adapter = Field<IslandDisplayAdapter>(host, "adapter")!;
        adapter.GetType().GetProperty("ReducedMotionOverride", Members)!.SetValue(adapter, true);
        var processes = new List<Process>();
        var handles = new HashSet<nint>();
        HostBrokerSession? session = null;
        int checkpoints = 0;
        try
        {
            await host.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), interactiveHints: true).WaitAsync(TimeSpan.FromSeconds(8));
            session = Field<HostBrokerSession>(host, "communication")!;
            Capture();
            Check(processes.Count == 2, "SDK scenario must own independent Broker and service");
            await Until(() => settings.GetSnapshot().Components.Any(x => x.Identity.Segments[0].Value == "counter"), "SDK entry missing");
            foreach (var row in settings.GetSnapshot().Components)
                Check(settings.SetVisibility(row.Identity, row.Identity.Segments[0].Value == "counter").IsSuccess, "visibility setup failed");
            await Until(() => host.Session.State == IslandDisplayState.Embedded && adapter.IsAlive, "owned island not embedded");
            var island = Field<ContentIslandHost>(adapter, "host")!;
            handles.Add(island.Handle); handles.Add(island.Bridge);
            await Until(() => Find<Button>(island.ContentRoot, "request")?.IsLoaded == true, "real SDK request control not loaded");
            var originalSession = State().SessionId;
            Invoke(Find<Button>(island.ContentRoot, "request")!);
            await Until(() => Coordinator()?.InteractiveHints.Inspect().Count == 1, "SDK request did not display interactive hint");
            var coordinator = Coordinator()!;
            coordinator.InteractiveHints.ReducedMotionOverride = true;
            coordinator.Hints.ReducedMotionOverride = true;
            coordinator.Manager.ReducedMotionOverride = true;
            coordinator.Refresh();
            var observation = coordinator.InteractiveHints.Inspect().Single();
            var window = coordinator.InteractiveHints.WindowForTesting(observation.Generation)!;
            Track(window);
            await Until(() => Find<Slider>(window.RootForTesting, "slider")?.IsLoaded == true, "interactive slider not loaded");
            var slider = Find<Slider>(window.RootForTesting, "slider")!;
            Check(slider.Value == 20, "initial confirmed value missing");
            Check(session.States.FlyoutRequests.GetLastResult("counter")?.Result.Code == "Displayed", "SDK queue final result missing");
            Mark("sdk-displayed", new { observation.Generation, handle = observation.Handle.ToInt64(), slider.Value });

            ((IRangeValueProvider)new SliderAutomationPeer(slider).GetPattern(PatternInterface.RangeValue)).SetValue(55);
            await Until(() => Level() == 55 && slider.Value == 55 && AutomationProperties.GetHelpText(slider) != "等待确认", "SDK slider confirmation failed");
            Check(ReferenceEquals(slider, Find<Slider>(window.RootForTesting, "slider")) && coordinator.InteractiveHints.Inspect().Single().Handle == observation.Handle,
                "confirmed value rebuilt controls or native window");
            Mark("confirmed", new { level = Level(), slider.Value, revision = State().State!.Revision });
            ((IRangeValueProvider)new SliderAutomationPeer(slider).GetPattern(PatternInterface.RangeValue)).SetValue(95);
            await Until(() => AutomationProperties.GetHelpText(slider) == "DemoLevelRejected" && coordinator.Hints.Inspect().Count > 0,
                "rejected slider did not return explanatory hint");
            Check(Level() == 55 && slider.Value == 55, "rejected adjustment replaced confirmed value");
            Mark("failed-rollback", new { confirmed = Level(), slider.Value, error = AutomationProperties.GetHelpText(slider) });

            var hintIdentity = settings.GetSnapshot().HintEntries.Single(x => x.Identity.Segments[0].Value == "counter" && x.Identity.LocalId.Value == "adjust").Identity;
            Check(settings.SetHintVisibility(hintIdentity, false).IsSuccess, "hide interactive hint failed");
            await Until(() => !coordinator.InteractiveHints.HasResources, "hide did not close paired windows");
            Check(settings.SetHintVisibility(hintIdentity, true).IsSuccess, "show preference failed");
            Invoke(Find<Button>(island.ContentRoot, "requestGroup")!);
            await Until(() => coordinator.Manager.Inspect().Any(x => x.Entry.Entry.EntryId == "details" && !x.Closing), "declared taskbar group did not open");
            var group = coordinator.Manager.Inspect().Single();
            foreach (var item in group.Windows) handles.Add(item.Handle);
            Invoke(Find<Button>(island.ContentRoot, "request")!);
            await Until(() => coordinator.InteractiveHints.Inspect().Count == 1, "associated SDK hint did not open");
            observation = coordinator.InteractiveHints.Inspect().Single();
            Check(observation.Request.Owner?.Generation == group.Generation, "SDK hint lost declared group generation");
            window = coordinator.InteractiveHints.WindowForTesting(observation.Generation)!; Track(window);
            await Until(() => Find<Button>(window.RootForTesting, "expand")?.IsLoaded == true, "expand control did not load");
            Invoke(Find<Button>(window.RootForTesting, "expand")!);
            await Until(() => coordinator.Manager.Inspect().Single().Windows.Any(x => x.TemplateId == "expanded"), "controlled hint expansion did not navigate owner group");
            Check(coordinator.Manager.Inspect().Single().Generation == group.Generation, "expansion replaced group identity");
            Mark("owned-expansion", new { group.Generation, owner = observation.Request.Owner, windows = coordinator.Manager.Inspect().Single().Windows.Select(x => x.TemplateId) });

            Invoke(Find<Button>(window.RootForTesting, "fail")!);
            await Until(() => coordinator.Hints.Inspect().Any(x => x.Request.Owner?.Generation == group.Generation && x.Text?.Contains("演示") == true), "associated action failure did not display hint");
            Check(coordinator.Manager.Inspect().Single().Generation == group.Generation, "action failure closed owner group");
            Mark("owned-failure", new { group.Generation, level = Level() });
            var oldHint = observation.Request;
            processes.Single(x => x.Id == session.BrokerProcessId).Kill();
            await Until(() => State() is { IsInteractive: true, IsConnected: true } current && current.SessionId != originalSession,
                "bounded Broker recovery did not rebind service");
            Capture();
            await Until(() => !coordinator.InteractiveHints.HasResources && coordinator.Manager.Inspect().Count == 0, "reconnect retained old UI instance");
            Check(coordinator.InteractiveHints.Show(oldHint).Code == "StaleHint", "late old-session hint survived recovery");
            var late = await session.SendActionAsync(new("counter", "main", ActionEntryKind.Hint, "adjust", "level"),
                new(ActionParameterKind.Number, Number: 10), expectedSessionId: originalSession).WaitAsync(TimeSpan.FromSeconds(2));
            Check(!late.Accepted && Level() == 55, "old-session action altered new confirmed state");
            Mark("session-replaced", new { originalSession, current = State().SessionId, level = Level(), late.Code });
        }
        finally
        {
            if (session is not null) Capture();
            Check(host.Shutdown(), "production host cleanup retained owner");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(processes.Select(x => x.WaitForExitAsync(deadline.Token)));
            foreach (var process in processes) { log($"interactiveHintOwnedPid={process.Id}; exited={process.HasExited}"); process.Dispose(); }
            Check(handles.All(x => !FlyoutNative.IsWindow(x)), "production native handle survived cleanup");
        }
        Check(checkpoints == 6 && watch.Elapsed < TimeSpan.FromSeconds(45), "missing checkpoints or exceeded scenario budget");
        log($"interactive-hint-production-pass: checkpoints={checkpoints}; elapsedMs={watch.ElapsedMilliseconds}; SDK=true; cleanup=true; evidence={evidence}");

        BrokerApplicationSnapshot State() => session!.States.GetSnapshot("counter")!;
        double? Level() => State().State!.TemplateEntries!.Single(x => x.Entry.Kind == TemplateEntryKind.Hint && x.Entry.EntryId == "adjust")
            .Fields.Single(x => x.FieldId == "level").Value.Number;
        TaskbarFlyoutCoordinator? Coordinator() => Field<TaskbarFlyoutCoordinator>(host, "flyouts");
        void Track(InteractiveHintWindow value) { handles.Add(value.Handle); handles.Add(value.BackgroundHandle); }
        void Capture()
        {
            foreach (int pid in session!.ServiceProcessIds.Prepend(session.BrokerProcessId))
            {
                if (processes.Any(x => x.Id == pid)) continue;
                var process = Process.GetProcessById(pid); _ = process.Handle; processes.Add(process);
            }
        }
        void Mark(string phase, object value) { checkpoints++; log("interactive-hint-production: " + JsonSerializer.Serialize(new { phase, elapsedMs = watch.ElapsedMilliseconds, value })); }
        async Task Until(Func<bool> condition, string error)
        {
            var limit = Stopwatch.StartNew();
            while (!condition())
            {
                if (limit.Elapsed > TimeSpan.FromSeconds(8) || watch.Elapsed > TimeSpan.FromSeconds(40))
                    throw new InvalidOperationException(error + "; " + string.Join("; ", host.Errors.TakeLast(4)));
                await Task.Delay(15);
            }
        }
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static T? Find<T>(DependencyObject? value, string id) where T : FrameworkElement
    {
        if (value is null) return null;
        if (value is T element && AutomationProperties.GetAutomationId(element) == "mtp-template-" + id) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
            if (Find<T>(VisualTreeHelper.GetChild(value, i), id) is { } found) return found;
        return null;
    }
    private static T? Field<T>(object value, string name) where T : class => (T?)value.GetType().GetField(name, Members)!.GetValue(value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

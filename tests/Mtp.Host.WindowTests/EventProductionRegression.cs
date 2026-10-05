using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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

/// <summary>Real Host/Broker/SDK event path against a native parent owned by this test.</summary>
internal static class EventProductionRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static async Task RunAsync(Action<string> log)
    {
        var watch = Stopwatch.StartNew();
        var evidence = Path.Combine(AppContext.BaseDirectory, "event-production-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(evidence, "display.json")));
        using var target = new OwnedIslandTarget();
        var workArea = new PixelRect(-30000, -30000, 1000, 720);
        uint dpi = NativeWindows.GetDpiForWindow(target.Parent);
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(evidence, "dock.json")),
            target.Capture, evidence, displayProvider: () => [new("owned-primary", true,
                new(-30000, -30000, 1000, 800), workArea, dpi)]);
        host.FlyoutDiagnostics = (kind, data) => log(kind + ": " + JsonSerializer.Serialize(data));
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(Path.Combine(evidence, "settings.json")),
            () => host.Applications, host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        var adapter = Field<IslandDisplayAdapter>(host, "adapter")!;
        adapter.GetType().GetProperty("ReducedMotionOverride", Members)!.SetValue(adapter, true);
        var processes = new List<Process>();
        var handles = new HashSet<nint>();
        HostBrokerSession? session = null;
        TaskbarFlyoutCoordinator? coordinator = null;
        ContentIslandHost? island = null;
        long lastRequestAt = -1000;
        int checkpoints = 0;
        try
        {
            await host.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), events: true).WaitAsync(TimeSpan.FromSeconds(8));
            session = Field<HostBrokerSession>(host, "communication")!;
            CaptureProcesses();
            Check(processes.Count == 2, "Event scenario must own an independent Broker and SDK service.");
            await Until(() => settings.GetSnapshot().EventEntries.Count(x => x.Identity.Segments[0].Value == "counter") == 12,
                "SDK event declarations did not reach settings.");
            // EventEntries reads the live Broker snapshot, while Components is the
            // UI-thread display projection updated by HostConsole.Refresh. Wait for
            // that projection before setting visibility on the newly declared entry.
            await Until(() => settings.GetSnapshot().Components.Any(x => x.Identity.Segments[0].Value == "counter" &&
                x.Identity.Segments[1].Value == "main" && x.Identity.LocalId.Value == "controls" && x.HasTemplate),
                "SDK component directory did not project counter/main/controls.");
            Startup("component-directory-ready");
            foreach (var row in settings.GetSnapshot().Components)
                Check(settings.SetVisibility(row.Identity, row.Identity.Segments[0].Value == "counter").IsSuccess, "Component visibility setup failed.");
            Startup("component-visibility-set");
            await Until(() => host.Session.State == IslandDisplayState.Embedded && adapter.IsAlive, "Owned component island did not embed.");
            island = Field<ContentIslandHost>(adapter, "host")!;
            handles.Add(island.Handle); handles.Add(island.Bridge);
            await Until(() => Find<Button>(island.ContentRoot, "send")?.IsLoaded == true, "Real SDK component controls did not load.");
            string originalSession = State().SessionId;
            // The production coordinator is intentionally created on the first action/request.
            await Request("send", "Displayed");
            coordinator = Field<TaskbarFlyoutCoordinator>(host, "flyouts")!;
            coordinator.Events.ReducedMotionOverride = true;
            coordinator.Hints.ReducedMotionOverride = true;
            coordinator.Manager.ReducedMotionOverride = true;
            coordinator.Refresh();
            await Until(() => Group("event0") is { } value && Window(value.Identity, "main") is { } window &&
                Find<Button>(window.Root, "confirm")?.IsLoaded == true, "SDK queue did not display event0 controls.");
            var first = Group("event0")!;
            var main = Window(first.Identity, "main")!;
            nint firstHandle = main.Handle;
            var confirm = Find<Button>(main.Root, "confirm")!;
            Check(Confirmed() == "确认次数：0", "Initial event confirmed state is missing.");
            Mark("sdk-displayed", new { first.Identity, hwnd = firstHandle.ToInt64(), receipt = Receipt() });

            Invoke(confirm);
            await Until(() => Confirmed() == "确认次数：1" && Find<TextBlock>(main.Root, "count")?.Text == "确认次数：1" &&
                AutomationProperties.GetHelpText(confirm) != "等待确认", "SDK event action did not confirm into the current control.");
            Check(Group("event0")?.Identity == first.Identity && main.Handle == firstHandle &&
                ReferenceEquals(confirm, Find<Button>(main.Root, "confirm")), "Confirmation rebuilt the event window or control.");
            Mark("confirmed-in-place", new { confirmed = Confirmed(), hwnd = main.Handle.ToInt64(), revision = State().State!.Revision });

            var positions = new[] { FlyoutPosition.TopLeft, FlyoutPosition.TopCenter, FlyoutPosition.TopRight,
                FlyoutPosition.BottomLeft, FlyoutPosition.BottomCenter, FlyoutPosition.BottomRight, FlyoutPosition.Center, FlyoutPosition.LowerCenter };
            foreach (var position in positions)
            {
                Check(settings.SetEvents(new(position, false, 5)).IsSuccess, "Event position setting failed.");
                await Until(() => AtPosition(first.Identity, position), "Host position did not affect actual event HWNDs: " + position);
                Check(Group("event0")?.Identity == first.Identity, "Position preference replaced the event identity.");
                log("event-production-position: " + JsonSerializer.Serialize(new { position = position.ToString(), bounds = Presented(first.Identity), dpi }));
            }
            Check(settings.SetEvents(new(FlyoutPosition.BottomLeft, false, 5)).IsSuccess, "Host-only position setup failed.");
            await Until(() => AtPosition(first.Identity, FlyoutPosition.BottomLeft), "SDK TopRight preference bypassed the disabled override.");
            Check(settings.SetEvents(new(FlyoutPosition.BottomLeft, true, 5)).IsSuccess, "Application override setting failed.");
            await Until(() => AtPosition(first.Identity, FlyoutPosition.TopRight), "Allowed SDK TopRight preference did not move the native event.");
            Mark("eight-positions-and-override", new { positions = positions.Length, before = "BottomLeft", after = "TopRight", bounds = Presented(first.Identity) });

            Invoke(Find<Button>(main.Root, "open")!);
            await Until(() => Window(first.Identity, "child") is { } child && Find<Button>(child.Root, "back")?.IsLoaded == true,
                "Controlled event OpenPanel did not present its child.");
            var childWindow = Window(first.Identity, "child")!;
            Check(Find<TextBlock>(childWindow.Root, "count")?.Text == "确认次数：1", "Child panel did not share confirmed event state.");
            Invoke(Find<Button>(childWindow.Root, "back")!);
            await Until(() => Window(first.Identity, "main") is { } value && Find<Button>(value.Root, "fail")?.IsLoaded == true,
                "Event Back did not return to the current main panel.");
            Check(Group("event0")?.Identity == first.Identity, "Event navigation replaced its generation.");
            Mark("controlled-navigation", new { first.Identity, panels = Group("event0")!.Windows.Select(x => x.TemplateId).ToArray() });

            main = Window(first.Identity, "main")!;
            Invoke(Find<Button>(main.Root, "fail")!);
            var owner = new HintOwner(first.Identity.ScreenId, first.Identity.Generation, first.Entry, first.Identity.SessionId);
            await Until(() => coordinator.Hints.Inspect().Any(x => x.Request.Owner == owner && x.Text?.Contains("演示事件") == true),
                "Event business failure did not bind a hint to the full event owner.");
            Check(Group("event0")?.Identity == first.Identity && Confirmed() == "确认次数：1", "Failed event action changed confirmed state or closed its owner.");
            var errorHint = coordinator.Hints.Inspect().Single(x => x.Request.Owner == owner);
            Mark("failure-owner", new { owner, hintGeneration = errorHint.Generation, hintHwnd = errorHint.Handle.ToInt64(), confirmed = Confirmed() });

            var eventIdentity = settings.GetSnapshot().EventEntries.Single(x => x.Identity.Segments[0].Value == "counter" && x.Identity.LocalId.Value == "event0").Identity;
            nint[] oldWindows = Group("event0")!.Windows.Select(x => x.Handle).Append(errorHint.Handle).ToArray();
            Check(settings.SetEventVisibility(eventIdentity, false).IsSuccess, "Event hide preference failed.");
            await Until(() => Group("event0") is null && !coordinator.Hints.Inspect().Any(x => x.Request.Owner == owner),
                "Event visibility did not immediately clean its group and associated hint.");
            Check(oldWindows.All(x => !FlyoutNative.IsWindow(x)), "Hidden event retained an owned native window.");
            await Request("send", "EntryDisabled");
            Check(Group("event0") is null, "SDK request recreated a disabled event entry.");
            Check(settings.SetEventVisibility(eventIdentity, true).IsSuccess, "Event show preference failed.");
            await Until(() => session.States.FlyoutRequests.IsEntryEnabled("counter", originalSession, "main", "event0"), "Event admission preference did not update.");
            await Request("send", "Displayed");
            await Until(() => Group("event0") is { } value && value.Identity.Generation != first.Identity.Generation, "Reenabled event did not create a fresh generation.");
            Check(Confirmed() == "确认次数：1", "Visibility preference lost confirmed business state.");
            Mark("visibility-and-admission", new { oldGeneration = first.Identity.Generation, newGeneration = Group("event0")!.Identity.Generation, denied = "EntryDisabled" });

            Check(settings.SetEvents(new(FlyoutPosition.BottomLeft, false, 3)).IsSuccess, "Event limit=3 preference failed.");
            await Request("persistent", "Displayed");
            await Request("next", "Displayed");
            await Until(() => coordinator.Events.Inspect().Count == 3 && Group("event1") is not null && Group("event2") is not null,
                "SDK persistent/next channels did not coexist at limit=3.");
            Check(settings.GetSnapshot().EventEntries.Single(x => x.Identity == EventIdentity("event1")).ClosePolicy == EventClosePolicy.Persistent,
                "Persistent SDK channel did not retain its declared policy.");
            Check(settings.SetEvents(new(FlyoutPosition.BottomLeft, false, 1)).IsSuccess, "Event limit=1 preference failed.");
            await Until(() => coordinator.Events.Inspect().Count == 1 && Group("event2") is not null, "Reduced limit did not retain only the newest unprotected group.");
            Check(settings.SetEvents(new(FlyoutPosition.BottomLeft, false, 2)).IsSuccess, "Event limit=2 preference failed.");
            await Request("next", "Displayed");
            await Until(() => coordinator.Events.Inspect().Count == 2 && Group("event3") is not null, "Increased limit did not admit another SDK channel.");
            Mark("persistent-multiple-and-limits", new { limits = new[] { 3, 1, 2 }, channels = coordinator.Events.Inspect().Select(x => x.Entry.Entry.EntryId).ToArray() });

            await Request("send", "Displayed");
            var beforeRecovery = Group("event0")!;
            var oldRequest = new EventGroupOpenRequest(beforeRecovery.Entry, originalSession, beforeRecovery.Identity.ScreenId,
                workArea, dpi, FlyoutPosition.TopRight);
            var oldQueued = new QueuedHostFlyout(new ProtocolMessage
            {
                Kind = MessageKind.FlyoutRequest, ApplicationId = "counter", SessionId = originalSession,
                Flyout = new("stale-event-production", Receipt()!.RequestSequence, "main", "event0", FlyoutKind.EventGroup, State().State!.Revision)
            }, false);
            CaptureProcesses(); Track();
            processes.Single(x => x.Id == session.BrokerProcessId).Kill();
            await Until(() => State() is { IsInteractive: true, IsConnected: true } current && current.SessionId != originalSession,
                "Bounded Broker recovery did not rebind the live SDK service.");
            CaptureProcesses();
            await Until(() => !coordinator.Events.HasResources && !coordinator.Hints.HasResources, "Reconnect retained an old event or associated hint.");
            Check(coordinator.Present(oldQueued).Code == "StaleSession" && coordinator.Events.Show(oldRequest).Code == "StaleSession",
                "Late old-session event survived the queue or manager boundary.");
            var late = await session.SendActionAsync(new("counter", "main", ActionEntryKind.EventChannel, "event0", "confirm"),
                new(ActionParameterKind.None), expectedSessionId: originalSession).WaitAsync(TimeSpan.FromSeconds(2));
            Check(!late.Accepted && Confirmed() == "确认次数：1", "Old event action changed the recovered confirmed state.");
            await Request("send", "Displayed");
            await Until(() => Group("event0") is { } current && current.Identity.SessionId != originalSession &&
                Window(current.Identity, "main") is { } value && Find<TextBlock>(value.Root, "count")?.Text == "确认次数：1",
                "New SDK event did not retain confirmed state after Broker recovery.");
            Mark("recovery-and-old-session-isolation", new { originalSession, currentSession = State().SessionId,
                late.Code, confirmed = Confirmed(), generation = Group("event0")!.Identity.Generation });
        }
        finally
        {
            if (session is not null) CaptureProcesses();
            Track();
            bool released = host.Shutdown();
            bool forcedCleanup = false;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                try { await Task.WhenAll(processes.Select(x => x.WaitForExitAsync(deadline.Token))); }
                catch (OperationCanceledException)
                {
                    forcedCleanup = true;
                    foreach (var process in processes.Where(x => !x.HasExited)) process.Kill();
                    await Task.WhenAll(processes.Select(x => x.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            finally
            {
                foreach (var process in processes) { log($"eventOwnedPid={process.Id}; exited={process.HasExited}"); process.Dispose(); }
            }
            Check(released, "Production event Host cleanup retained an owner.");
            Check(!forcedCleanup, "Production event Host required forced test-owned process cleanup.");
            Check(handles.All(x => !FlyoutNative.IsWindow(x)), "Production event native handle survived cleanup.");
        }
        Check(checkpoints == 8 && watch.Elapsed < TimeSpan.FromSeconds(45), "Missing event checkpoints or exceeded scenario budget.");
        log($"event-production-pass: checkpoints={checkpoints}; elapsedMs={watch.ElapsedMilliseconds}; SDK=true; cleanup=true; evidence={evidence}");

        BrokerApplicationSnapshot State() => session!.States.GetSnapshot("counter")!;
        FlyoutRequestReceipt? Receipt() => session!.States.FlyoutRequests.GetLastResult("counter");
        string? Confirmed() => State().State!.TemplateEntries!.Single(x => x.Entry.Kind == TemplateEntryKind.EventChannel && x.Entry.EntryId == "event0")
            .Fields.Single(x => x.FieldId == "count").Value.Text;
        EventGroupHostObservation? Group(string id) => coordinator!.Events.Inspect().SingleOrDefault(x => x.Entry.Entry.EntryId == id && !x.Closing);
        StableIdentity EventIdentity(string id) => settings.GetSnapshot().EventEntries.Single(x => x.Identity.Segments[0].Value == "counter" && x.Identity.LocalId.Value == id).Identity;
        TaskbarFlyoutWindow? Window(EventGroupIdentity id, string template)
        {
            foreach (string key in new[] { "main" }.Concat(Enumerable.Range(0, TemplateLimits.TemplatesPerEntry).Select(index => "panel:" + index)))
                if (coordinator!.Events.WindowForTesting(id, key) is { } window && window.TemplateId == template &&
                    FlyoutNative.IsWindow(window.Handle) && IsWindowVisible(window.Handle) && FlyoutNative.ReadOpacity(window.Handle) > 0) return window;
            return null;
        }
        PixelRect? Presented(EventGroupIdentity id)
        {
            var rectangles = coordinator!.Events.Inspect().SingleOrDefault(x => x.Identity == id)?.Windows
                .Where(x => !x.Closing && FlyoutNative.IsWindow(x.Handle) && IsWindowVisible(x.Handle) && FlyoutNative.ReadOpacity(x.Handle) > 0)
                .Select(x => FlyoutNative.Bounds(x.Handle)).ToArray();
            if (rectangles is null || rectangles.Length == 0) return null;
            int left = rectangles.Min(x => x.X), top = rectangles.Min(x => x.Y);
            return new(left, top, (int)rectangles.Max(x => x.Right) - left, (int)rectangles.Max(x => x.Bottom) - top);
        }
        bool AtPosition(EventGroupIdentity id, FlyoutPosition position)
        {
            if (Presented(id) is not { } actual) return false;
            double margin = 16 * dpi / 96d;
            double x = position switch { FlyoutPosition.TopLeft or FlyoutPosition.BottomLeft => workArea.X + margin,
                FlyoutPosition.TopRight or FlyoutPosition.BottomRight => workArea.Right - margin - actual.Width,
                _ => workArea.X + (workArea.Width - actual.Width) / 2d };
            double y = position switch { FlyoutPosition.TopLeft or FlyoutPosition.TopCenter or FlyoutPosition.TopRight => workArea.Y + margin,
                FlyoutPosition.Center => workArea.Y + (workArea.Height - actual.Height) / 2d,
                FlyoutPosition.LowerCenter => workArea.Y + workArea.Height * 0.8 - actual.Height / 2d,
                _ => workArea.Bottom - margin - actual.Height };
            y = Math.Clamp(y, workArea.Y + margin, workArea.Bottom - margin - actual.Height);
            return Math.Abs(actual.X - x) <= 2 && Math.Abs(actual.Y - y) <= 2;
        }
        async Task Request(string control, string expected)
        {
            // Stay below the documented ten requests/second admission rate, without retrying a rejected action.
            long remaining = 150 - (watch.ElapsedMilliseconds - lastRequestAt);
            if (remaining > 0) await Task.Delay((int)remaining);
            await Until(() => Field<ContentIslandHost>(adapter, "host") is { } current &&
                Find<Button>(current.ContentRoot, control)?.IsLoaded == true, "Current SDK component request control did not load.");
            island = Field<ContentIslandHost>(adapter, "host")!;
            handles.Add(island.Handle); handles.Add(island.Bridge);
            var button = Find<Button>(island.ContentRoot, control)!;
            await Until(() => button.IsLoaded && button.IsEnabled && AutomationProperties.GetHelpText(button) != "等待确认", "SDK request control is not available.");
            long before = Receipt()?.RequestSequence ?? 0;
            lastRequestAt = watch.ElapsedMilliseconds;
            Invoke(button);
            await Until(() => Receipt() is { } receipt && receipt.RequestSequence > before && receipt.Result.Code is not ("Received" or "Queued"),
                "SDK event request did not get its final Host receipt.");
            Check(Receipt()!.Result.Code == expected, "SDK event request returned " + Receipt()!.Result.Code + "; expected " + expected);
            await Until(() => AutomationProperties.GetHelpText(button) != "等待确认", "SDK component action confirmation remained pending.");
        }
        void Track()
        {
            if (coordinator is null) return;
            foreach (var group in coordinator.Events.Inspect()) foreach (var window in group.Windows) if (window.Handle != 0) handles.Add(window.Handle);
            foreach (var hint in coordinator.Hints.Inspect()) if (hint.Handle != 0) handles.Add(hint.Handle);
        }
        void CaptureProcesses()
        {
            foreach (int pid in session!.ServiceProcessIds.Prepend(session.BrokerProcessId))
            {
                if (processes.Any(x => x.Id == pid)) continue;
                var process = Process.GetProcessById(pid); _ = process.Handle; processes.Add(process);
            }
        }
        void Mark(string phase, object value) { Track(); checkpoints++; log("event-production: " + JsonSerializer.Serialize(new { phase, elapsedMs = watch.ElapsedMilliseconds, value })); }
        void Startup(string phase) => log("event-production-startup: " + JsonSerializer.Serialize(new
        {
            phase, elapsedMs = watch.ElapsedMilliseconds, islandState = host.Session.State.ToString(), host.Session.Error,
            adapterAlive = adapter.IsAlive, target = host.TargetSummary,
            components = settings.GetSnapshot().Components.Select(x => new
                { identity = x.Identity.Segments.Select(y => y.Value).ToArray(), x.IsVisible, x.HasTemplate }).ToArray(),
            applications = host.Applications.Select(x => new { x.ApplicationId, x.SessionId, x.IsConnected, x.IsInteractive }).ToArray()
        }));
        async Task Until(Func<bool> condition, string error)
        {
            var limit = Stopwatch.StartNew();
            while (true)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(40))
                { Startup("work-timeout"); throw new TimeoutException("Event production work exceeded forty seconds."); }
                Track();
                if (condition()) return;
                if (limit.Elapsed > TimeSpan.FromSeconds(8) || watch.Elapsed > TimeSpan.FromSeconds(40))
                {
                    Startup("wait-timeout: " + error);
                    throw new InvalidOperationException(error + "; " + string.Join("; ", host.Errors.TakeLast(4)));
                }
                await Task.Delay(15);
            }
        }
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static T? Find<T>(DependencyObject? root, string id) where T : FrameworkElement
    {
        if (root is null) return null;
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        for (int count = 0; pending.Count > 0 && count < 4096; count++)
        {
            var current = pending.Pop();
            if (current is T element && AutomationProperties.GetAutomationId(element) == "mtp-template-" + id) return element;
            for (int i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0 && pending.Count < 4096; i--) pending.Push(VisualTreeHelper.GetChild(current, i));
        }
        return null;
    }
    private static T? Field<T>(object value, string name) where T : class => (T?)value.GetType().GetField(name, Members)!.GetValue(value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}

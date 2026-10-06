using System.Diagnostics;
using System.Globalization;
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
using Mtp.Host;
using Mtp.Host.Flyouts;
using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class CombinationStabilityNativeRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int RoundCount = 50;
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    public static async Task RunAsync(Action<string> log)
    {
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(Budget - TimeSpan.FromSeconds(20));
        string evidence = Path.Combine(AppContext.BaseDirectory, "combination-stability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        using var rounds = new StreamWriter(Path.Combine(evidence, "rounds.jsonl"));
        using var resources = new StreamWriter(Path.Combine(evidence, "resources.jsonl"));
        using var lifecycle = new StreamWriter(Path.Combine(evidence, "lifecycle.jsonl"));
        await File.WriteAllTextAsync(Path.Combine(evidence, "policy.json"), JsonSerializer.Serialize(new
        {
            requestedRounds = RoundCount, hardBudgetSeconds = 600, warmupSamplesPerPid = 10,
            fixtureBoundary = "MTP-owned offscreen Win32 parent; Explorer and manual acceptance remain out of scope",
            warmupRationale = "runtime lazy-init plateaus by ~round 10; measured in evidence/12/ab-peer-20261006-175501/probe-project/results/empty.jsonl",
            resourceTrend = new { handleDelta = 256, privateBytesDelta = 128 * 1024 * 1024, tailHandleSpan = 32, tailPrivateBytesSpan = 16 * 1024 * 1024 }
        }, new JsonSerializerOptions { WriteIndented = true }));

        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(evidence, "display.json")));
        using var target = new OwnedIslandTarget();
        PlaceOwned();
        IReadOnlyList<TaskbarDockDisplay> Displays() => [new("owned-primary", true, new(-30000, -30000, 8000, 800),
            new(-30000, -30000, 8000, 720), NativeWindows.GetDpiForWindow(target.Parent))];
        CoreResult<IslandTarget> Capture(TaskbarDockPreferences preferences) =>
            target.Parent == 0 ? CoreResult<IslandTarget>.Failure(new("fixture_parent_missing", "自有父窗口已销毁。")) :
            CoreResult<IslandTarget>.Success(new IslandTarget(target.Parent, "owned-wide",
                new(preferences.TargetDisplayId ?? "owned-primary", new(-30000, -30000, 8000, 800),
                new(-30000, -29280, 8000, 80), new(-22200, -29280, 200, 80), NativeWindows.GetDpiForWindow(target.Parent)), true));
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(evidence, "dock.json")),
            Capture, evidence, displayProvider: Displays);
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(Path.Combine(evidence, "settings.json")),
            () => host.Applications, host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        Check(settings.SetAppearance(new(HostTheme.Light, MaterialKind.None, 1)).IsSuccess, "fixture appearance failed");
        var adapter = Field<IslandDisplayAdapter>(host, "adapter");
        adapter.ReducedMotionOverride = false;

        string[] apps = ["organization", "presets", "timers", "dynamic", "flyouts", "hints", "events"];
        var args = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["organization"] = ["--organization"], ["presets"] = ["--presets"], ["timers"] = ["--timers"],
            ["dynamic"] = ["--dynamic"], ["flyouts"] = ["--flyouts"], ["hints"] = ["--interactive-hints"], ["events"] = ["--events"]
        };
        var initialPids = new Dictionary<string, int>(StringComparer.Ordinal);
        var allPids = new HashSet<int>();
        var roundHandles = new HashSet<nint>();
        var observedGroupTimers = new HashSet<DispatcherTimer>();
        var samples = new List<ResourceSample>();
        var lifecycleProbes = new List<LifecycleProbe>();
        HostBrokerSession? session = null;
        TaskbarFlyoutCoordinator? coordinator = null;
        ProtocolResult? lastPresentationResult = null;
        ProtocolMessage? lastFlyoutMessage = null;
        ProtocolResult? lastHintPresentation = null;
        Exception? failure = null;
        int completed = 0, queuePeak = 0;
        bool cleanupVerified = false;
        string lastAction = "setup";
        try
        {
            session = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), apps, deadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
            SetField(host, "communication", session);
            SetField(host, "dynamicDemo", true);
            // Capture production's own presentation verdict through the supported diagnostics seam.
            // Without it a rejected hint only surfaces 12 seconds later as a bare timeout, and the
            // rejection code that explains the failure is lost.
            host.FlyoutDiagnostics = (kind, value) =>
            {
                if (kind != "flyout-presentation-result" || value is null) return;
                try
                {
                    using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
                    if (!document.RootElement.TryGetProperty("Code", out var code)) return;
                    string? text = code.GetString();
                    bool accepted = document.RootElement.TryGetProperty("Accepted", out var flag) && flag.GetBoolean();
                    if (!accepted) lastHintPresentation = ProtocolResult.Reject(text ?? "Unknown", "production rejected the hint request");
                }
                catch (JsonException) { }
            };
            session.QueueFlyoutPresentation = message =>
            {
                lastFlyoutMessage = message;
                var queue = Field<HostFlyoutRequestQueue>(host, "flyoutRequests");
                var trigger = FlyoutNative.GetCursorPos(out var point) ? new HostFlyoutTrigger(point.X, point.Y) : null;
                var result = queue.Enqueue(message, trigger);
                lastPresentationResult = result;
                queuePeak = Math.Max(queuePeak, queue.Count);
                if (result.Accepted) host.RequestRefresh();
                return result;
            };
            foreach (string app in apps)
            {
                int pid = await session.StartServiceAsync(app, Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"),
                    deadline.Token, args[app]).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
                initialPids[app] = pid; allPids.Add(pid);
            }
            allPids.Add(session.BrokerProcessId);
            await Until(() => { host.Refresh(); return apps.All(app => session.States.GetSnapshot(app)?.Declaration is not null); }, "SDK declarations missing", deadline.Token);
            SelectVisible(true);
            host.Refresh();
            await Until(() => host.Session.State == IslandDisplayState.Embedded && adapter.GetGroupFrame() is { IsComplete: true }, "group did not settle", deadline.Token);
            coordinator = EnsureCoordinator(host);
            SetReducedMotion(coordinator, false);
            await Until(() => adapter.GroupSnapshot is { ItemsByKey.Count: > 0 }, "no composed items", deadline.Token);

            for (int round = 1; round <= RoundCount; round++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                long started = watch.ElapsedMilliseconds;
                string fault = "none";
                try
                {
                    await ExerciseRound(round, deadline.Token);
                    if (round == 10) { fault = "broker-reconnect"; await RecoverBroker(deadline.Token); }
                    if (round == 20)
                    {
                        fault = "parent-recreate";
                        nint oldHandle = adapter.Handle; nint oldParent = target.Parent;
                        target.Destroy(); host.Refresh();
                        await Until(() => !NativeWindows.IsWindow(oldParent) && !NativeWindows.IsWindow(oldHandle) && !adapter.IsAlive,
                            "parent loss was not observed", deadline.Token);
                        target.Recreate(); PlaceOwned(); host.Refresh();
                        await Until(() => adapter.IsAlive && adapter.Handle != oldHandle && NativeWindows.GetParent(adapter.Handle) == target.Parent &&
                            adapter.GetGroupFrame() is { IsComplete: true }, "parent recreation failed", deadline.Token);
                        log("combination-parent-replacement: " + JsonSerializer.Serialize(new { oldParent = oldParent.ToInt64(), oldHandle = oldHandle.ToInt64(),
                            newParent = target.Parent.ToInt64(), newHandle = adapter.Handle.ToInt64() }));
                    }
                    if (round == 30) { fault = "service-recovery"; await RecoverService("dynamic", deadline.Token); }
                    if (round == 40) { fault = "reduced-motion-switch"; adapter.ReducedMotionOverride = true; SetReducedMotion(coordinator, true); host.Refresh(); await Until(() => adapter.GetGroupFrame() is { IsComplete: true }, "reduced-motion settle failed", deadline.Token); }
                    lifecycleProbes.Add(CaptureLifecycleProbe(round));
                    await CloseRound(deadline.Token);
                    completed++;
                    await rounds.WriteLineAsync(JsonSerializer.Serialize(new { round, startedMs = started, endedMs = watch.ElapsedMilliseconds, result = "pass", fault, lastAction }));
                    log($"combination-round: round={round}; elapsedMs={watch.ElapsedMilliseconds}; fault={fault}; lastAction={lastAction}");
                }
                catch (Exception error)
                {
                    failure = error;
                    await rounds.WriteLineAsync(JsonSerializer.Serialize(new { round, startedMs = started, endedMs = watch.ElapsedMilliseconds, result = "fail", fault, lastAction, error = error.ToString() }));
                    break;
                }
                await SampleResources(round);
                bool collectLifecycle = round is 5 or 10;
                if (collectLifecycle) CollectForLifecycleProbe();
                await lifecycle.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    round,
                    fullGc = collectLifecycle,
                    native = CaptureNativeWindowSample(),
                    probes = collectLifecycle
                        ? lifecycleProbes.Select(probe => probe.Describe()).ToArray()
                        : Array.Empty<object>()
                }));
                await rounds.FlushAsync();
                await resources.FlushAsync();
                await lifecycle.FlushAsync();
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            bool hostReleased = false;
            try { SelectVisible(false); host.Refresh(); hostReleased = host.Shutdown(); } catch (Exception error) { failure ??= error; }
            HostLifecycleSnapshot? before = null, after = null;
            try { before = session?.GetLifecycleSnapshot(); if (session is not null) await session.DisposeAsync(); after = session?.GetLifecycleSnapshot(); } catch (Exception error) { failure ??= error; }
            try
            {
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(600 - watch.Elapsed.TotalSeconds, 0.01, 15)));
                foreach (int pid in allPids.Where(pid => !ProcessExited(pid)))
                { using var process = Process.GetProcessById(pid); await process.WaitForExitAsync(cleanupDeadline.Token); }
            }
            catch (Exception error) { failure ??= error; }
            bool pidsExited = allPids.All(ProcessExited);
            bool nativeReleased = !adapter.IsAlive && adapter.Handle == 0;
            bool managersReleased = coordinator is null || (!coordinator.Manager.HasResources && !coordinator.Hints.HasResources &&
                !coordinator.InteractiveHints.HasResources && !coordinator.Events.HasResources);
            bool timersStopped = !adapter.TimerDriverRunning && observedGroupTimers.All(timer => !timer.IsEnabled) && !TimerEnabled(coordinator?.Hints) &&
                !TimerEnabled(coordinator?.InteractiveHints) && !TimerEnabled(coordinator?.Events) && !Field<DispatcherTimer>(host, "timer").IsEnabled;
            // Post-teardown control point: every round, window, session and subscription is now gone.
            // Comparing this against the last in-round sample separates "bounded framework cache that is
            // reclaimed once the workload stops" from "a genuine retained leak". Per-round sampling alone
            // cannot tell those apart, which is why this point exists.
            CollectForLifecycleProbe();
            await Task.Delay(250);
            using (var self = Process.GetCurrentProcess())
            {
                self.Refresh();
                (int gdi, int user) = GuiResourceCounts(self);
                int threads = -1;
                try { threads = self.Threads.Count; } catch { }
                var teardown = new ResourceSample(completed + 1, "host-after-teardown", self.Id, self.HandleCount,
                    self.PrivateMemorySize64, self.WorkingSet64, self.TotalProcessorTime.TotalMilliseconds, gdi, user, threads);
                samples.Add(teardown);
                // Persist the control point: it is excluded from the trend windows (its group has a
                // single sample) but must still be readable to compare against the last in-round value.
                await resources.WriteLineAsync(JsonSerializer.Serialize(new { round = completed + 1, afterTeardown = true, samples = new[] { teardown } }));
                await resources.FlushAsync();
            }
            var trend = ResourceTrend(samples);
            int lastRoundHandles = samples.Where(x => x.Owner == "host").Select(x => x.HandleCount).DefaultIfEmpty(0).Last();
            int teardownHandles = samples.Where(x => x.Owner == "host-after-teardown").Select(x => x.HandleCount).DefaultIfEmpty(-1).Last();
            long lastRoundPrivate = samples.Where(x => x.Owner == "host").Select(x => x.PrivateBytes).DefaultIfEmpty(0).Last();
            long teardownPrivate = samples.Where(x => x.Owner == "host-after-teardown").Select(x => x.PrivateBytes).DefaultIfEmpty(-1).Last();
            cleanupVerified = failure is null && completed == RoundCount && hostReleased && pidsExited && nativeReleased &&
                managersReleased && timersStopped && after is { OwnedServices: 0, OwnedBrokers: 0, ActiveClockTasks: 0, ActiveRecoveryTasks: 0,
                    ActiveOutputDrains: 0, OutstandingActions: 0, BusyActions: 0, CurrentBroker: null,
                    LastRetiredBroker: { ReceiverCompleted: true, ActionDispatcherCompleted: true, PermissionPublisherCompleted: true,
                        ActionQueueCount: 0, PermissionSnapshotCount: 0, PendingRegistrationCount: 0 } } && trend.Pass && watch.Elapsed <= Budget;
            await File.WriteAllTextAsync(Path.Combine(evidence, "cleanup.json"), JsonSerializer.Serialize(new
            {
                hostReleased, roundsCompleted = completed, requestedRounds = RoundCount, elapsedMs = watch.ElapsedMilliseconds,
                lifecycleBefore = before, lifecycleAfter = after, queuePeak, pidsExited, nativeReleased, managersReleased, timersStopped,
                ownedPids = allPids.ToArray(), trend, withinBudget = watch.Elapsed <= Budget, failure = failure?.ToString(),
                afterTeardown = new
                {
                    lastRoundHandles, teardownHandles, reclaimedHandles = lastRoundHandles - teardownHandles,
                    lastRoundPrivate, teardownPrivate, reclaimedPrivate = lastRoundPrivate - teardownPrivate,
                    interpretation = teardownHandles < 0 ? "unavailable" :
                        lastRoundHandles - teardownHandles > 0 ? "workload resources largely reclaimed once all rounds stopped" :
                        "resources retained after full teardown"
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
            var result = new { roundsCompleted = completed, requestedRounds = RoundCount, elapsedMs = watch.ElapsedMilliseconds, cleanupVerified, queuePeak, trend, failure = failure?.ToString() };
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            log($"combination-stability-result: roundsCompleted={completed}; elapsedMs={watch.ElapsedMilliseconds}; cleanupVerified={cleanupVerified}; evidence={evidence}; queuePeak={queuePeak}");
        }
        if (failure is not null) throw failure;
        Check(cleanupVerified, "Combination cleanup, bounded resource trend or hard budget failed; inspect " + evidence);

        async Task ExerciseRound(int round, CancellationToken token)
        {
            SelectVisible(true);
            await Until(() => adapter.IsAlive && adapter.GetGroupFrame() is { IsComplete: true } &&
                Field<ContentIslandHost>(adapter, "host").ContentRoot?.IsLoaded == true, "round content island did not restore", token);
            HostTheme theme = round % 2 == 0 ? HostTheme.Dark : HostTheme.Light;
            CheckSetting(settings.SetAppearance(new(theme, MaterialKind.None, 1)), "theme switch failed");
            host.Refresh();
            await Until(() => Field<ContentIslandHost>(adapter, "host").ContentRoot?.RequestedTheme ==
                (theme == HostTheme.Dark ? ElementTheme.Dark : ElementTheme.Light), "theme did not apply", token);
            await Until(() => adapter.GroupSnapshot!.ItemsByKey.Keys.Count(x => x.Component.ApplicationId == "dynamic") == 2 &&
                adapter.GroupSnapshot!.ItemsByKey.Keys.Count(x => x.Component.ApplicationId == "timers") >= 2 &&
                adapter.GroupSnapshot!.ItemsByKey.Keys.Count(x => x.Component.ApplicationId == "presets") == 6 &&
                adapter.GroupSnapshot!.ItemsByKey.Keys.Count(x => x.Component.ApplicationId == "organization") >= 6 &&
                adapter.GetGroupFrame() is { IsComplete: true }, "required composed business items missing", token);

            await Until(() => Find<Button>(Find<FrameworkElement>(Field<ContentIslandHost>(adapter, "host").ContentRoot,
                "mtp-component/" + string.Join("/", Escape("dynamic"), Escape("main"), Escape("left"))),
                ActionId("dynamic", "main", "left")) is { IsLoaded: true }, "dynamic controls not restored", token);
            await ClickAction("dynamic", "main", "left", token);
            await ClickAction("dynamic", "main", "right", token);
            await ClickTemplate("timers", "main", "controls", "reset", token);
            await ClickTemplate("timers", "main", "controls", "add", token);
            await ClickTemplate("presets", "main", "controls", "forward", token);
            await ClickTemplate("presets", "main", "controls", "backward", token);
            await ClickTemplate("presets", "main", "controls", "mode", token);
            await ClickTemplate("presets", "main", "controls", "semantics", token);
            await ClickTemplate("presets", "main", "controls", "status", token);
            await ClickTemplate("organization", "main", "controls", "reset", token);
            await ClickTemplate("organization", "main", "controls", "add", token);
            await ClickTemplate("organization", "main", "controls", "update", token);
            await ClickTemplate("organization", "main", "controls", "shared", token);
            await ClickTemplate("organization", "main", "controls", "end", token);
            await ClickTemplate("flyouts", "main", "controls", "request", token);
            await Until(() => coordinator!.Manager.Inspect().Count > 0, "taskbar flyout not observed", token);
            RememberHandles();
            var taskbarGroup = coordinator!.Manager.Inspect().Single();
            var taskbarWindow = coordinator.Manager.WindowForTesting(taskbarGroup.ScreenId)!;
            await Until(() => FindInteractiveButton(taskbarWindow.Root, "mtp-template-child") is not null, "taskbar panel missing", token);
            await InvokeUntil(() => FindInteractiveButton(taskbarWindow.Root, "mtp-template-child"),
                "taskbar child navigation invoke failed", token,
                () => DescribeButtonMatches(taskbarWindow.Root, "mtp-template-child"));
            await Until(() => coordinator.Manager.Inspect().Single().Windows.Any(x => x.TemplateId == "child"), "taskbar child navigation missing", token);
            RememberHandles();
            var child = coordinator.Manager.WindowForTesting(taskbarGroup.ScreenId)!;
            // Navigation keeps the disabled outgoing renderer until the animation ends.
            // Both templates declare "back", so locate it inside the incoming child.
            await Until(() => FindInteractiveButton(child.Root, "mtp-template-back") is not null, "taskbar child back missing", token);
            await InvokeUntil(() => FindInteractiveButton(child.Root, "mtp-template-back"),
                "taskbar child back invoke failed", token,
                () => DescribeButtonMatches(child.Root, "mtp-template-back"));
            await Until(() => coordinator.Manager.Inspect().Single().Windows.Any(x => x.TemplateId == "panel"), "taskbar return missing", token);
            await ClickTemplate("flyouts", "main", "controls", "hint", token);
            await Until(() => coordinator!.Hints.Inspect().Count > 0, "short hint not observed", token);
            RememberHandles();
            await ClickTemplate("hints", "main", "controls", "requestGroup", token);
            await Until(() => coordinator!.Manager.Inspect().Any(x => x.Entry.ApplicationId == "hints" && !x.Closing), "interactive owner group not observed", token);
            // The interactive hint is anchored to the "hints" taskbar group. Production rejects the
            // request with StaleHint until that owner group has a settled layout, so wait for the
            // group's presented bounds rather than merely for the group entry to appear.
            await Until(() => coordinator!.Manager.Inspect().Any(x => x.Entry.ApplicationId == "hints" && !x.Closing &&
                x.Windows.Any(window => window.TemplateId == "details" && !window.Closing)) &&
                coordinator.Manager.BoundsForHint(coordinator.Manager.Inspect().Single(x => x.Entry.ApplicationId == "hints").ScreenId,
                    coordinator.Manager.Inspect().Single(x => x.Entry.ApplicationId == "hints").Generation) is not null,
                "interactive owner group did not settle", token);
            await ClickTemplate("hints", "main", "controls", "request", token);
            try
            {
            await Until(() => coordinator!.InteractiveHints.Inspect().Count == 1,
                "interactive hint not observed; " + DescribeInteractiveHintState(), token);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException("interactive hint not observed; " + DescribeInteractiveHintRequest(), error);
            }
            var hint = coordinator!.InteractiveHints.Inspect().Single();
            var hintWindow = coordinator.InteractiveHints.WindowForTesting(hint.Generation)!;
            await Until(() => FindInteractiveSlider(hintWindow.RootForTesting) is { IsLoaded: true },
                "interactive slider missing; candidates=" + DescribeSliders(hintWindow.RootForTesting), token);
            var slider = FindInteractiveSlider(hintWindow.RootForTesting)!;
            double level = round % 2 == 0 ? 30 : 35;
            ((IRangeValueProvider)new SliderAutomationPeer(slider).GetPattern(PatternInterface.RangeValue)).SetValue(level);
            await Until(() => State("hints").TemplateEntries!.Single(x => x.Entry.Kind == TemplateEntryKind.Hint).Fields.Single(x => x.FieldId == "level").Value.Number == level && slider.Value == level, "interactive adjustment not confirmed", token);
            Check(coordinator.InteractiveHints.Inspect().Single().Handle == hint.Handle, "interactive state refresh replaced native owner");
            var expand = Find<Button>(hintWindow.RootForTesting, "mtp-template-expand");
            Check(expand is { IsEnabled: true }, "interactive expansion missing"); Invoke(expand!);
            await Until(() => coordinator.Manager.Inspect().Any(x => x.Entry.ApplicationId == "hints" && x.Windows.Any(w => w.TemplateId == "expanded")), "interactive expansion not applied", token);
            await ClickTemplate("events", "main", "controls", round % 2 == 0 ? "persistent" : "send", token);
            await Until(() => coordinator!.Events.Inspect().Count > 0, "event window not observed", token);
            var eventGroup = coordinator.Events.Inspect().Single();
            var eventWindow = coordinator.Events.WindowForTesting(eventGroup.Identity)!;
            await Until(() => Find<Button>(eventWindow.Root, "mtp-template-confirm") is { IsLoaded: true }, "event confirm missing", token);
            long eventRevision = State("events").Revision;
            Invoke(Find<Button>(eventWindow.Root, "mtp-template-confirm")!);
            await Until(() => State("events").Revision == eventRevision + 1, "event business confirmation missing", token);
            await ExpandReverse(token);
            await ExerciseTimer(token);
            var grouping = settings.GetSnapshot().Groupings.Single(x => x.Identity.Segments[0].Value == "organization" && x.Identity.LocalId.Value == "islands").Identity;
            CheckSetting(settings.SetGrouping(grouping, DynamicGrouping.Together), "organization merge setting failed"); host.Refresh();
            await Until(() => adapter.GroupSnapshot!.Instances.Count(x => x.Entry.ApplicationId == "organization") == 1 && adapter.GetGroupFrame() is { IsComplete: true }, "organization merge not projected", token);
            CheckSetting(settings.SetGrouping(grouping, DynamicGrouping.Separate), "organization split setting failed"); host.Refresh();
            await Until(() => adapter.GroupSnapshot!.Instances.Count(x => x.Entry.ApplicationId == "organization") == 3 && adapter.GetGroupFrame() is { IsComplete: true }, "organization split not projected", token);
            await ClickTemplate("timers", "main", "controls", "remove", token);
            RememberHandles();
            lastAction = $"round-{round}-complete";
        }

        async Task ClickAction(string app, string feature, string component, CancellationToken token)
        {
            lastAction = $"action:{app}/{feature}/{component}";
            Button? button = Find<Button>(ComponentRoot(app, feature, component), ActionId(app, feature, component));
            Check(button is not null && button.IsEnabled, "missing native required action " + lastAction);
            int beforeItems = State(app).DynamicEntries?.SingleOrDefault(x => x.ComponentId == "items")?.Content.Items.Count ?? 0;
            Invoke(button!);
            await Until(() => button!.IsEnabled && session!.Actions.OutstandingCount == 0 &&
                (component == "left" ? State(app).DynamicEntries!.Single(x => x.ComponentId == "items").Content.Items.Count == beforeItems + 1 :
                 component == "right" ? State(app).DynamicEntries!.Single(x => x.ComponentId == "items").Content.Items.Count == beforeItems - 1 : true), "native action did not confirm " + lastAction, token);
            host.Refresh();
        }
        async Task ClickTemplate(string app, string feature, string component, string node, CancellationToken token)
        {
            lastAction = $"template:{app}/{feature}/{component}/{node}";
            string automationId = "mtp-template-" + node;
            FrameworkElement TemplateRoot() => ComponentRoot(app, feature, component);
            Button? CurrentButton() => FindInteractiveButton(TemplateRoot(), automationId);
            Button? button = CurrentButton();
            Check(button is not null && button.IsEnabled, "missing native required template " + lastAction);
            long revision = State(app).Revision;
            Invoke(button!);
            try
            {
                await Until(() => !button!.IsEnabled || session!.Actions.OutstandingCount > 0 || State(app).Revision != revision,
                    "native template did not dispatch " + lastAction, token);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException("native template did not dispatch " + lastAction + "; candidates=" + DescribeButtonMatches(TemplateRoot(), automationId), error);
            }
            try
            {
                await Until(() => CurrentButton() is { IsLoaded: true, IsEnabled: true } && session!.Actions.OutstandingCount == 0 &&
                    (app is "timers" or "organization" ? State(app).Revision == revision + 1 :
                     app == "presets" ? State(app).Revision >= revision : true), "native template did not confirm " + lastAction, token);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException("native template did not confirm " + lastAction + "; candidates=" + DescribeButtonMatches(TemplateRoot(), automationId), error);
            }
            host.Refresh();
        }
        string DescribeInteractiveHintState()
        {
            if (coordinator is null) return "coordinator=<null>";
            var hints = coordinator.InteractiveHints.Inspect();
            var groups = coordinator.Manager.Inspect();
            var state = session?.States.GetSnapshot("hints");
            return $"interactiveCount={hints.Count};interactive={string.Join("|", hints.Select(x => $"generation={x.Generation}:closing={x.Closing}:handle={x.Handle}"))};" +
                $"groups={string.Join("|", groups.Where(x => x.Entry.ApplicationId == "hints").Select(x => $"generation={x.Generation}:closing={x.Closing}:windows={x.Windows.Count}"))};" +
                $"session={state?.SessionId ?? "<null>"}:connected={state?.IsConnected}:revision={state?.State?.Revision}:templateEntries={state?.State?.TemplateEntries?.Count ?? 0}";
        }
        string DescribeInteractiveHintRequest()
        {
            var snapshot = session?.States.GetSnapshot("hints");
            var state = snapshot?.State;
            var entries = state?.TemplateEntries?.Select(entry => new
            {
                feature = entry.Entry.FeatureGroupId,
                id = entry.Entry.EntryId,
                kind = entry.Entry.Kind.ToString(),
                fields = entry.Fields.Select(field => field.FieldId).ToArray()
            }).ToArray() ?? Array.Empty<object>();
            var flyout = lastFlyoutMessage?.Flyout;
            var request = flyout is null ? null : new
            {
                requestId = flyout.RequestId,
                sequence = flyout.RequestSequence,
                feature = flyout.FeatureGroupId,
                entry = flyout.EntryId,
                kind = flyout.Kind.ToString(),
                revision = flyout.StateRevision,
                screen = flyout.Screen.ToString(),
                position = flyout.Position.ToString()
            };
            return JsonSerializer.Serialize(new
            {
                interactiveHints = coordinator?.InteractiveHints.Inspect(),
                taskbarGroups = coordinator?.Manager.Inspect(),
                ordinaryHints = coordinator?.Hints.Inspect(),
                events = coordinator?.Events.Inspect(),
                hintsState = snapshot is null ? null : new
                {
                    snapshot.SessionId,
                    snapshot.IsConnected,
                    snapshot.IsInteractive,
                    revision = state?.Revision,
                    templateEntries = entries,
                    lastError = snapshot.LastError
                },
                lastFlyoutRequest = request,
                lastHintPresentation = lastHintPresentation is null ? null : new { lastHintPresentation.Code, lastHintPresentation.Accepted },
                lastPresentationResult,
                hostQueueCount = Field<HostFlyoutRequestQueue>(host, "flyoutRequests").Count,
                buttonCandidates = DescribeButtonMatches(ComponentRoot("hints", "main", "controls"), "mtp-template-request")
            });
        }
        async Task InvokeUntil(Func<Button?> locate, string failureMessage, CancellationToken token, Func<string>? describe = null)
        {
            Exception? last = null;
            string lastCandidates = "<not sampled>";
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(2))
            {
                token.ThrowIfCancellationRequested();
                var button = locate();
                if (describe is not null) lastCandidates = describe();
                if (button is { IsLoaded: true, IsEnabled: true })
                {
                    try
                    {
                        Invoke(button);
                        return;
                    }
                    catch (COMException error) when (error.HResult == unchecked((int)0x80040200))
                    {
                        last = error;
                    }
                }
                await Task.Delay(20, token);
            }
            throw new InvalidOperationException(failureMessage + "; candidates=" + lastCandidates, last);
        }
        async Task ExerciseTimer(CancellationToken token)
        {
            foreach (string id in new[] { "up", "down" })
            {
                var key = Key("timers", id);
                if (!adapter.GroupSnapshot!.ItemsByKey[key].Expanded) continue;
                var current = Find<Button>(Field<ContentIslandHost>(adapter, "host").ContentRoot, ItemId(key));
                Check(current is { IsEnabled: true }, "timer normalization missing"); Invoke(current!);
                await Until(() => !adapter.GroupSnapshot!.ItemsByKey[key].Expanded, "timer normalization did not apply", token);
            }
            TaskbarItemKey up = Key("timers", "up");
            Button? blank = Find<Button>(Field<ContentIslandHost>(adapter, "host").ContentRoot, ItemId(up));
            Check(blank is { IsEnabled: true }, "timer expand missing"); Invoke(blank!);
            await Until(() => adapter.GroupSnapshot!.ItemsByKey[up].Expanded && adapter.GetGroupFrame() is { IsComplete: true }, "timer controls did not expand", token);
            foreach (bool paused in new[] { true, false })
            {
                await Until(() => Find<Button>(Field<ContentIslandHost>(adapter, "host").ContentRoot, ItemId(up) + "/PrimaryButton") is { IsLoaded: true, IsEnabled: true },
                    "timer pause/resume control missing", token);
                var primary = Find<Button>(Field<ContentIslandHost>(adapter, "host").ContentRoot, ItemId(up) + "/PrimaryButton")!;
                Invoke(primary);
                await Until(() => State("timers").DynamicEntries!.Single().Content.Items.Single(x => x.ItemId == "up").Fields.Timer!.IsPaused == paused, "timer pause/resume not confirmed", token);
            }
            await Until(() => Find<Button>(Field<ContentIslandHost>(adapter, "host").ContentRoot, ItemId(up) + "/SecondaryButton") is { IsLoaded: true, IsEnabled: true },
                "timer batch expansion missing", token);
            var secondary = Find<Button>(Field<ContentIslandHost>(adapter, "host").ContentRoot, ItemId(up) + "/SecondaryButton")!;
            Invoke(secondary);
            await Until(() => adapter.GroupSnapshot!.ItemsByKey[Key("timers", "down")].Expanded && adapter.GetGroupFrame() is { IsComplete: true }, "timer declared batch expansion missing", token);
        }
        async Task ExpandReverse(CancellationToken token)
        {
            await Until(() => adapter.GetGroupFrame() is { IsComplete: true }, "shared width did not settle before reversal", token);
            TaskbarItemKey key = Key("dynamic", "a");
            var initial = adapter.GetGroupFrame() ?? throw new InvalidOperationException("missing expansion frame");
            double baselineWidth = initial.WidthDip;
            Button? button = Find<Button>(Field<ContentIslandHost>(adapter, "host").ContentRoot, ItemId(key));
            Check(button is not null && button.IsEnabled, "expand button missing");
            Invoke(button!);
            bool intermediate = false;
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromMilliseconds(700))
            {
                intermediate |= adapter.GetGroupFrame() is { IsComplete: false };
                if (adapter.GroupSnapshot?.ItemsByKey.GetValueOrDefault(key)?.Expanded == true) break;
                await Task.Delay(10, token);
            }
            Check(adapter.GroupSnapshot?.ItemsByKey.GetValueOrDefault(key)?.Expanded == true, "required item did not expand");
            Invoke(button!);
            await Until(() => adapter.GetGroupFrame() is { IsComplete: true } && adapter.GroupSnapshot?.ItemsByKey.GetValueOrDefault(key)?.Expanded == false, "reverse expansion did not settle", token);
            Check(intermediate || adapter.ReducedMotionOverride == true, "no expansion observation");
            Check(Math.Abs(adapter.GetGroupFrame()!.WidthDip - baselineWidth) < 1, "reverse expansion did not restore shared width");
        }
        async Task CloseRound(CancellationToken token)
        {
            foreach (var group in coordinator!.Manager.Inspect().ToArray())
                Check(coordinator.Manager.CloseScreen(group.ScreenId, group.Generation).IsSuccess, "taskbar close failed");
            foreach (var group in coordinator.Events.Inspect().ToArray())
                Check(coordinator.Events.Close(group.Identity).IsSuccess, "event close failed");
            foreach (var hint in settings.GetSnapshot().HintEntries) CheckSetting(settings.SetHintVisibility(hint.Identity, false), "hint close preference failed");
            foreach (var entry in settings.GetSnapshot().EventEntries) CheckSetting(settings.SetEventVisibility(entry.Identity, false), "event close preference failed");
            host.Refresh();
            await Until(() => coordinator.Manager.Inspect().Count == 0 && coordinator.Hints.Inspect().Count == 0 &&
                coordinator.InteractiveHints.Inspect().Count == 0 && coordinator.Events.Inspect().Count == 0 &&
                !coordinator.Manager.HasResources && !coordinator.Hints.HasResources &&
                !coordinator.InteractiveHints.HasResources && !coordinator.Events.HasResources, "flyout managers not closed", token);
            SelectVisible(false); host.Refresh();
            await Until(() => !adapter.IsAlive && adapter.GetGroupFrame() is null && adapter.GetTimerReadings().Count == 0 &&
                adapter.GetPresetReadings().Count == 0, "hidden group retained visual or animation readings", token);
            Check(session!.Actions.OutstandingCount == 0 && session.Actions.BusyCount == 0 &&
                Field<HostFlyoutRequestQueue>(host, "flyoutRequests").Count == 0, "pending action survived round");
            Check(!adapter.TimerDriverRunning && observedGroupTimers.All(timer => !timer.IsEnabled) && !TimerEnabled(coordinator.Hints) &&
                !TimerEnabled(coordinator.InteractiveHints) && !TimerEnabled(coordinator.Events), "round timer survived cleanup");
            Check(roundHandles.All(handle => !FlyoutNative.IsWindow(handle)), "round native owner survived cleanup");
            // The timer Tick delegate captures its Group. Retain timers only until this
            // round's cleanup assertion, then release the fixture's strong references so
            // closed visual trees and native owners can be collected before the next round.
            observedGroupTimers.Clear();
            roundHandles.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        async Task RecoverBroker(CancellationToken token)
        {
            int old = session!.BrokerProcessId;
            var oldSessions = apps.ToDictionary(app => app, app => session.States.GetSnapshot(app)!.SessionId, StringComparer.Ordinal);
            int[] services = session.ServiceProcessIds.ToArray();
            using var process = Process.GetProcessById(old);
            process.Kill();
            await Until(() => session.BrokerProcessId != old && apps.All(app => session.States.GetSnapshot(app) is { IsInteractive: true } state && state.SessionId != oldSessions[app]), "broker recovery failed", token);
            allPids.Add(session.BrokerProcessId);
            Check(session.ServiceProcessIds.Order().SequenceEqual(services.Order()), "Broker recovery restarted healthy SDK services");
            var late = await session.SendActionAsync(new("dynamic", "main", ActionEntryKind.Component, "left", "add"), new(), token, expectedSessionId: oldSessions["dynamic"]);
            Check(!late.Accepted && late.Code == "StaleSession", "old Broker generation accepted action");
            log("combination-broker-replacement: " + JsonSerializer.Serialize(new { oldPid = old, newPid = session.BrokerProcessId, oldSessions, newSessions = apps.ToDictionary(app => app, app => session.States.GetSnapshot(app)!.SessionId) }));
        }
        async Task RecoverService(string app, CancellationToken token)
        {
            int old = initialPids[app];
            string oldSession = session!.States.GetSnapshot(app)!.SessionId;
            using var process = Process.GetProcessById(old);
            process.Kill();
            await Until(() => session!.States.GetSnapshot(app)?.IsInteractive == false && ProcessExited(old), "service did not become disconnected", token);
            // The session watcher may already have scheduled its bounded automatic recovery.
            // Wait for that operation to settle before issuing the explicit retry fallback;
            // otherwise a healthy in-flight recovery is incorrectly reported as a failure.
            var recoveryWait = Stopwatch.StartNew();
            while (session!.States.GetSnapshot(app)?.IsInteractive != true &&
                session.GetRecovery(app) is { State: RecoveryState.Recovering })
            {
                token.ThrowIfCancellationRequested();
                if (recoveryWait.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("service recovery did not settle");
                await Task.Delay(50, token);
            }
            if (session.States.GetSnapshot(app)?.IsInteractive != true)
                Check((await session.RetryApplicationAsync(app, token)).Accepted, "service retry failed");
            await Until(() => session.States.GetSnapshot(app) is { IsInteractive: true } state && state.SessionId != oldSession, "service recovery failed", token);
            int replacement = session.ServiceProcessIds.Except(allPids).FirstOrDefault();
            Check(replacement > 0 && replacement != old, "service replacement PID missing");
            allPids.Add(replacement);
            log("combination-sdk-replacement: " + JsonSerializer.Serialize(new { app, oldPid = old, newPid = replacement, oldSession, newSession = session.States.GetSnapshot(app)!.SessionId }));
        }
        async Task SampleResources(int round)
        {
            foreach (int pid in session!.ServiceProcessIds.Prepend(session.BrokerProcessId)) allPids.Add(pid);
            using (var process = Process.GetCurrentProcess()) AddSample(process, round, "host");
            foreach (int pid in session!.ServiceProcessIds) TrySample(pid, round, "sdk");
            TrySample(session.BrokerProcessId, round, "broker");
            await resources.WriteLineAsync(JsonSerializer.Serialize(new { round, samples = samples.Where(x => x.Round == round), queuePeak, brokerPeak = session.PeakPendingRequests }));
        }
        LifecycleProbe CaptureLifecycleProbe(int round)
        {
            var island = Field<ContentIslandHost>(adapter, "host");
            object? content = PrivateField(island, "content");
            object? group = content is null ? null : PrivateField(content, "group");
            var templates = new List<WeakReference<object>>();
            AddTemplate(content, templates);
            if (group is not null)
            {
                foreach (string fieldName in new[] { "components", "items" })
                {
                    if (PrivateField(group, fieldName) is not System.Collections.IDictionary visuals) continue;
                    foreach (object? visual in visuals.Values) AddTemplate(visual, templates);
                }
            }
            return new(round, Weak(island), Weak(content), group is null ? null : Weak(group), templates.ToArray(), island.Handle, island.Bridge);
        }
        void AddTemplate(object? owner, List<WeakReference<object>> templates)
        {
            if (owner is null) return;
            object? value = PrivateField(owner, "template");
            if (value?.GetType().FullName == "Mtp.Host.Templates.TemplateRenderer") templates.Add(Weak(value));
        }
        static WeakReference<object> Weak(object? value) => new(value ?? throw new InvalidOperationException("Lifecycle probe target missing."));
        static void CollectForLifecycleProbe()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        }
        static NativeWindowSample CaptureNativeWindowSample()
        {
            int Count(string name)
            {
                object? value = typeof(NativeWindows).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
                return value is System.Collections.ICollection collection ? collection.Count : -1;
            }
            int threadWindows;
            try { threadWindows = NativeWindows.OwnThreadTopLevelWindows().Length; }
            catch { threadWindows = -1; }
            return new(Count("Callbacks"), Count("Owned"), Count("Previews"), threadWindows);
        }
        void AddSample(Process process, int round, string owner)
        {
            process.Refresh();
            // Process.HandleCount covers every kernel handle. GDI/USER object counts are the only way
            // to tell whether growth sits in kernel objects (events/mutexes/threads) or in USER/GDI
            // objects (windows/hooks/menus/DC/bitmaps). Without this split the growth cannot be
            // attributed, and WinUI windows created by the framework are invisible to MTP's own
            // NativeWindows counters. Thread count separates thread handles from other kernel objects.
            int threads = -1;
            if (owner == "host") { try { threads = process.Threads.Count; } catch { threads = -1; } }
            (int gdi, int user) = owner == "host" ? GuiResourceCounts(process) : (-1, -1);
            samples.Add(new(round, owner, process.Id, process.HandleCount, process.PrivateMemorySize64,
                process.WorkingSet64, process.TotalProcessorTime.TotalMilliseconds, gdi, user, threads));
        }
        static (int Gdi, int User) GuiResourceCounts(Process process)
        {
            try
            {
                nint handle = process.Handle;
                return handle == 0 ? (-1, -1) : (GetGuiResources(handle, 0), GetGuiResources(handle, 1));
            }
            catch { return (-1, -1); }
        }
        void TrySample(int pid, int round, string owner) { try { using var process = Process.GetProcessById(pid); AddSample(process, round, owner); } catch { samples.Add(new(round, owner, pid, -1, -1, -1, -1, -1, -1, -1)); } }
        void PlaceOwned() { if (target.Parent != 0) NativeWindows.Place(target.Parent, -30000, -29280, 8000, 80); }
        void RememberHandles()
        {
            var groups = Field<System.Collections.IDictionary>(coordinator!.Manager, "groups");
            foreach (object group in groups.Values) observedGroupTimers.Add(Field<DispatcherTimer>(group, "Timer"));
            var island = Field<ContentIslandHost>(adapter, "host"); roundHandles.Add(island.Handle); roundHandles.Add(island.Bridge);
            foreach (var window in coordinator!.Manager.Inspect().SelectMany(x => x.Windows).Concat(coordinator.Events.Inspect().SelectMany(x => x.Windows))) roundHandles.Add(window.Handle);
            foreach (var hint in coordinator.Hints.Inspect()) roundHandles.Add(hint.Handle);
            foreach (var hint in coordinator.InteractiveHints.Inspect())
            { roundHandles.Add(hint.Handle); if (coordinator.InteractiveHints.WindowForTesting(hint.Generation) is { } window) roundHandles.Add(window.BackgroundHandle); }
        }
        void SelectVisible(bool visible)
        {
            foreach (var row in settings.GetSnapshot().Components)
            {
                var result = settings.SetVisibility(row.Identity, visible && apps.Contains(row.Identity.Segments[0].Value, StringComparer.Ordinal) && row.Identity.LocalId.Value is not ("together" or "separate"));
                Check(result.IsSuccess, $"visibility update failed: {row.Identity}; code={result.Error?.Code}; message={result.Error?.Message}");
            }
            foreach (var hint in settings.GetSnapshot().HintEntries)
            {
                var result = settings.SetHintVisibility(hint.Identity, visible);
                Check(result.IsSuccess, $"hint visibility update failed: {hint.Identity}; code={result.Error?.Code}; message={result.Error?.Message}");
            }
            foreach (var entry in settings.GetSnapshot().EventEntries)
            {
                var result = settings.SetEventVisibility(entry.Identity, visible);
                Check(result.IsSuccess, $"event visibility update failed: {entry.Identity}; code={result.Error?.Code}; message={result.Error?.Message}");
            }
        }
        FrameworkElement ComponentRoot(string app, string feature, string component) =>
            Find<FrameworkElement>(Field<ContentIslandHost>(adapter, "host").ContentRoot,
                "mtp-component/" + string.Join("/", Escape(app), Escape(feature), Escape(component))) ??
            throw new InvalidOperationException($"missing component root {app}/{feature}/{component}");
        TaskbarItemKey Key(string app, string item) => adapter.GroupSnapshot!.ItemsByKey.Keys.Single(key => key.Component.ApplicationId == app && key.ItemId == item);
        ApplicationState State(string app) => session!.States.GetSnapshot(app)?.State ?? throw new InvalidOperationException("confirmed state missing: " + app);
        static string Escape(string value) => Uri.EscapeDataString(value);
        static string ActionId(string app, string feature, string component) => "mtp-action/" + string.Join("/", Escape(app), Escape(feature), Escape(component));
    }

    private sealed record ResourceSample(int Round, string Owner, int Pid, int HandleCount, long PrivateBytes, long WorkingSetBytes, double TotalProcessorTimeMs, int GdiObjects, int UserObjects, int ThreadCount);
    [DllImport("user32.dll")]
    private static extern int GetGuiResources(nint process, uint flags);
    private sealed record LifecycleProbe(int Round, WeakReference<object> Host, WeakReference<object> Content,
        WeakReference<object>? Group, WeakReference<object>[] Templates, nint HostHandle, nint BridgeHandle)
    {
        public object Describe() => new
        {
            round = Round,
            hostAlive = Host.TryGetTarget(out _),
            contentAlive = Content.TryGetTarget(out _),
            groupAlive = Group?.TryGetTarget(out _) == true,
            templatesAlive = Templates.Count(reference => reference.TryGetTarget(out _)),
            templateCount = Templates.Length,
            hostWindowAlive = NativeWindows.IsWindow(HostHandle),
            bridgeWindowAlive = NativeWindows.IsWindow(BridgeHandle)
        };
    }
    private sealed record NativeWindowSample(int Callbacks, int Owned, int Previews, int ThreadTopLevelWindows);
    private sealed record TrendResult(bool Pass, int WarmupRounds, int MaxHandleDelta, long MaxPrivateDelta, int TailHandleSpan, long TailPrivateSpan, string Verdict,
        int GdiDelta, int UserDelta, int KernelDelta, int ThreadDelta);
    private static TrendResult ResourceTrend(IReadOnlyList<ResourceSample> values)
    {
        bool allObserved = values.Count > 0 && values.All(x => x.HandleCount >= 0 && x.PrivateBytes >= 0);
        // Each replacement process gets its own warmup; never compare different PID generations.
        // The warmup window is 10 rounds, not 2: a control probe that performs no work at all still
        // climbs about +21 handles/round for its first ~10 rounds before plateauing, because the
        // .NET/WinUI runtime populates lazy per-process state. A two-round warmup therefore reports
        // that one-off runtime cost as a per-round leak. Measured in
        // evidence/12/ab-peer-20261006-175501/probe-project/results/{empty,baseline}.jsonl.
        const int WarmupRounds = 10;
        var valid = values.Where(x => x.HandleCount >= 0 && x.PrivateBytes >= 0).GroupBy(x => x.Pid)
            .Select(g => g.OrderBy(x => x.Round).Skip(WarmupRounds).ToArray()).Where(x => x.Length >= 5).ToArray();
        if (valid.Length == 0) return new(false, WarmupRounds, 0, 0, 0, 0, "no-samples", 0, 0, 0, 0);
        int maxHandles = valid.Max(x => x[^1].HandleCount - x[0].HandleCount);
        long maxPrivate = valid.Max(x => x[^1].PrivateBytes - x[0].PrivateBytes);
        int tailHandles = valid.Max(x => x.TakeLast(5).Max(s => s.HandleCount) - x.TakeLast(5).Min(s => s.HandleCount));
        long tailPrivate = valid.Max(x => x.TakeLast(5).Max(s => s.PrivateBytes) - x.TakeLast(5).Min(s => s.PrivateBytes));
        // Attribute the growth by handle class. Everything here must use the same first-to-last delta
        // as the handle total: mixing a first/last delta with a max/min range has no physical meaning
        // and lets a one-round transient pulse masquerade as a trend.
        var gui = valid.Where(x => x.All(s => s.GdiObjects >= 0 && s.UserObjects >= 0)).ToArray();
        int gdiDelta = gui.Length == 0 ? -1 : gui.Max(x => x[^1].GdiObjects - x[0].GdiObjects);
        int userDelta = gui.Length == 0 ? -1 : gui.Max(x => x[^1].UserObjects - x[0].UserObjects);
        int threadDelta = gui.Length == 0 ? -1 : gui.Max(x => x[^1].ThreadCount - x[0].ThreadCount);
        int kernelDelta = gdiDelta < 0 || userDelta < 0 ? -1 : maxHandles - gdiDelta - userDelta;
        bool pass = allObserved && maxHandles <= 256 && maxPrivate <= 128 * 1024 * 1024 && tailHandles <= 32 && tailPrivate <= 16 * 1024 * 1024;
        return new(pass, WarmupRounds, maxHandles, maxPrivate, tailHandles, tailPrivate, pass ? "within-bounded-cache-threshold" : "resource-trend-exceeded",
            gdiDelta, userDelta, kernelDelta, threadDelta);
    }

    private static bool TimerEnabled(object? manager) => manager?.GetType().GetField("timer", Members)?.GetValue(manager) is DispatcherTimer timer && timer.IsEnabled;
    private static object? PrivateField(object owner, string name) => owner.GetType().GetField(name, Members)?.GetValue(owner);
    private static void SetReducedMotion(TaskbarFlyoutCoordinator coordinator, bool value)
    {
        coordinator.Manager.ReducedMotionOverride = value; coordinator.Hints.ReducedMotionOverride = value;
        coordinator.InteractiveHints.ReducedMotionOverride = value; coordinator.Events.ReducedMotionOverride = value;
    }
    private static TaskbarFlyoutCoordinator EnsureCoordinator(HostConsoleController host)
    {
        host.GetType().GetMethod("EnsureFlyouts", Members)?.Invoke(host, null);
        return Field<TaskbarFlyoutCoordinator>(host, "flyouts");
    }
    private static bool ProcessExited(int pid) { try { using var p = Process.GetProcessById(pid); return p.HasExited; } catch { return true; } }
    private static async Task Until(Func<bool> condition, string error, CancellationToken token)
    {
        var wait = Stopwatch.StartNew();
        while (!condition()) { token.ThrowIfCancellationRequested(); if (wait.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(error); await Task.Delay(25, token); }
    }
    private static T? Find<T>(DependencyObject? root, string id) where T : FrameworkElement
    {
        if (root is null) return null;
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject current = stack.Pop();
            if (current is T element && AutomationProperties.GetAutomationId(element) == id) return element;
            for (int i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(current, i));
        }
        return null;
    }
    private static Button? FindInteractiveButton(DependencyObject? root, string id)
    {
        var matches = FindButtons(root, id);
        return matches.FirstOrDefault(button => button.IsLoaded && button.IsEnabled &&
            button.Visibility == Visibility.Visible && button.Opacity > 0 && button.IsHitTestVisible)
            ?? matches.FirstOrDefault(button => button.IsLoaded && button.IsEnabled);
    }
    private static IReadOnlyList<Button> FindButtons(DependencyObject? root, string id)
    {
        if (root is null) return [];
        var matches = new List<Button>();
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject current = stack.Pop();
            if (current is Button button && AutomationProperties.GetAutomationId(button) == id) matches.Add(button);
            for (int i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(current, i));
        }
        return matches;
    }
    private static string DescribeButtonMatches(DependencyObject? root, string id)
    {
        var matches = FindButtons(root, id);
        return matches.Count == 0 ? "<none>" : string.Join(",", matches.Select((button, index) =>
            $"{index}:loaded={button.IsLoaded}:enabled={button.IsEnabled}:visibility={button.Visibility}:opacity={button.Opacity:0.###}:hit={button.IsHitTestVisible}"));
    }
    private static Slider? FindInteractiveSlider(DependencyObject root) =>
        Find<Slider>(root, "mtp-template-slider") ?? Find<Slider>(root, "mtp-template-value");
    private static string DescribeSliders(DependencyObject? root)
    {
        if (root is null) return "<null>";
        var values = new List<string>(); var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is Slider slider)
                values.Add($"{AutomationProperties.GetAutomationId(slider)}:loaded={slider.IsLoaded}:visible={slider.Visibility}:enabled={slider.IsEnabled}:value={slider.Value}");
            for (int i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(current, i));
        }
        return values.Count == 0 ? "<none>" : string.Join(",", values);
    }
    private static string ItemId(TaskbarItemKey key) => "mtp-item/" + string.Join("/", Uri.EscapeDataString(key.Component.ApplicationId),
        Uri.EscapeDataString(key.Component.FeatureGroupId), Uri.EscapeDataString(key.Component.ComponentId),
        Uri.EscapeDataString(key.ItemId), key.PresenceGeneration.ToString(CultureInfo.InvariantCulture));
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static T Field<T>(object owner, string name) where T : class => (T)owner.GetType().GetField(name, Members)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, Members)!.SetValue(owner, value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void CheckSetting<T>(CoreResult<T> result, string message) =>
        Check(result.IsSuccess, $"{message}; code={result.Error?.Code}; message={result.Error?.Message}");
}


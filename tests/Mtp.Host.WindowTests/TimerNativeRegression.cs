using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
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

/// <summary>Real Host/SDK processes and applied WinUI timer frames on an MTP-owned parent; no manual acceptance.</summary>
internal static class TimerNativeRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static async Task RunAsync(Action<string> log)
    {
        var scenario = Stopwatch.StartNew();
        string root = Path.Combine(AppContext.BaseDirectory, "timer-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        using var target = new OwnedIslandTarget();
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")), target.Capture, root);
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(Path.Combine(root, "settings.json")),
            () => host.Applications, host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        Check(settings.SetAppearance(new(HostTheme.Light, MaterialKind.None, 1)).IsSuccess, "Could not select readable timer fixture appearance.");
        var adapter = Field<IslandDisplayAdapter>(host, "adapter");
        SetReducedMotion(adapter, false);
        var processes = new List<Process>();
        nint handle = 0, bridge = 0;
        int frames = 0, localSamples = 0;
        Exception? failure = null;
        try
        {
            await host.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), timers: true).WaitAsync(TimeSpan.FromSeconds(8));
            var communication = Field<HostBrokerSession>(host, "communication");
            foreach (int pid in communication.ServiceProcessIds.Prepend(communication.BrokerProcessId))
            {
                var process = Process.GetProcessById(pid);
                _ = process.Handle;
                processes.Add(process);
            }
            Check(processes.Count == 2 && processes.All(process => process.Id != Environment.ProcessId), "Timer fixture must own separate Broker and SDK service processes.");
            await Until(() => settings.GetSnapshot().Components.Count(row => IsCounter(row.Identity)) == 2, "Timer declaration missing.");
            foreach (var row in settings.GetSnapshot().Components)
                Check(settings.SetVisibility(row.Identity, IsCounter(row.Identity)).IsSuccess, "Could not select timer fixture visibility.");
            await Until(() => host.Session.State == IslandDisplayState.Embedded && TryFrame(adapter) is { IsComplete: true, Items.Count: 2 } && Readings(adapter).Count == 2,
                "Initial timer items were not rendered.");
            var island = Field<ContentIslandHost>(adapter, "host");
            await Until(() => island.ContentRoot?.IsLoaded == true, "Timer content island did not load.");
            handle = adapter.Handle; bridge = island.Bridge;
            Check(handle != 0 && NativeWindows.GetParent(handle) == target.Parent && NativeWindows.GetParent(bridge) == handle,
                "Timer rendering escaped its owned parent chain.");
            var up = Snapshot(adapter).ItemsByKey.Keys.Single(key => key.ItemId == "up");
            var down = Snapshot(adapter).ItemsByKey.Keys.Single(key => key.ItemId == "down");
            var upBlank = Button(up);
            int fixedRight = NativeWindows.WindowBounds(handle).Right;
            var baseline = Frame("initial");
            Check(Math.Abs(upBlank.ActualWidth - baseline.Items.Single(value => value.Key == up).Bounds.Width) < 1,
                "Blank activation does not cover the full normal item width.");
            long stateMessages = communication.ReceivedStateMessages;
            long heartbeats = communication.ReceivedHeartbeatMessages;
            var business = State();
            var expiries = Expiries(business);
            var first = Reading(up, "local-start");
            long firstObserved = scenario.ElapsedMilliseconds;
            await Until(() => scenario.ElapsedMilliseconds - firstObserved >= 1100 &&
                Readings(adapter).Single(value => value.Key == up).ValueMilliseconds >= first.ValueMilliseconds + 1000,
                "Applied native timer did not advance locally for at least one second.");
            var advanced = Reading(up, "local-advanced");
            Check(advanced.Text != first.Text && AutomationProperties.GetName(upBlank) == advanced.Text,
                "Local timer text did not reach its native accessible control.");
            log($"timer-transport: stateMessagesBefore={stateMessages}; stateMessagesAfter={communication.ReceivedStateMessages}; heartbeatBefore={heartbeats}; heartbeatAfter={communication.ReceivedHeartbeatMessages}");
            Check(communication.ReceivedStateMessages == stateMessages, "Host received business State messages during local-only sampling.");
            Check(State().Revision == business.Revision && Expiries(State()).SequenceEqual(expiries),
                "Local sampling published a business revision or renewed activity expiry.");
            Check(Frame("fixed-width-local").Items.Select(value => value.Bounds).SequenceEqual(baseline.Items.Select(value => value.Bounds)) &&
                ReferenceEquals(upBlank, Button(up)), "Timer digits changed geometry or rebuilt native controls.");
            await ConsoleScreenshot.SaveAsync(island.ContentRoot!, Path.Combine(root, "timer-initial.png")).WaitAsync(TimeSpan.FromSeconds(2));

            Invoke(upBlank);
            await Until(() =>
            {
                var frame = Frame("expand-forward");
                return Snapshot(adapter).ItemsByKey[up].Expanded && !frame.IsComplete && frame.WidthDip > baseline.WidthDip;
            }, "No real intermediate expansion frame was applied.");
            long forwardRevision = TimerNativeRegression.Frame(adapter).Revision;
            Invoke(upBlank);
            bool reversed = false;
            await Until(() =>
            {
                var frame = Frame("expand-reversed");
                reversed |= !frame.IsComplete && frame.Revision > forwardRevision;
                return !Snapshot(adapter).ItemsByKey[up].Expanded && frame.IsComplete;
            }, "Rapid reverse expansion did not settle.");
            Check(reversed && Math.Abs(TimerNativeRegression.Frame(adapter).WidthDip - baseline.WidthDip) < 0.001 && State().Revision == business.Revision,
                "Reversal skipped shared intermediate geometry or wrote business state.");
            Invoke(upBlank);
            await Until(() => Snapshot(adapter).ItemsByKey[up].Expanded && TimerNativeRegression.Frame(adapter).IsComplete, "Could not expand timer controls.");
            var primary = Button(up, "/PrimaryButton");
            var secondary = Button(up, "/SecondaryButton");
            Check(ReferenceEquals(VisualTreeHelper.GetParent(upBlank), VisualTreeHelper.GetParent(primary)) &&
                ReferenceEquals(VisualTreeHelper.GetParent(primary), VisualTreeHelper.GetParent(secondary)),
                "Timer input controls are not sibling regions.");
            long beforePause = State().Revision;
            Invoke(primary);
            await Until(() => State().Revision == beforePause + 1 && Readings(adapter).Single(value => value.Key == up).IsPaused,
                "Native pause button did not produce one confirmed provider update.");
            var paused = Reading(up, "paused");
            await Task.Delay(250);
            Check(State().Revision == beforePause + 1 && Snapshot(adapter).ItemsByKey[up].Expanded &&
                Readings(adapter).Single(value => value.Key == up).ValueMilliseconds == paused.ValueMilliseconds,
                "Pause duplicated dispatch, advanced the paused value or bubbled into collapse.");
            Invoke(primary);
            await Until(() => State().Revision == beforePause + 2 && !Readings(adapter).Single(value => value.Key == up).IsPaused,
                "Native resume button did not confirm exactly once.");
            Check(Snapshot(adapter).ItemsByKey[up].Expanded && Expiries(State()).SequenceEqual(expiries),
                "Resume changed expansion or renewed activity lifetime.");
            long batchBusinessRevision = State().Revision;
            Invoke(secondary);
            bool batchIntermediate = false;
            await Until(() =>
            {
                var frame = Frame("batch-expand");
                batchIntermediate |= !frame.IsComplete && Snapshot(adapter).ItemsByKey[down].Expanded;
                return Snapshot(adapter).ItemsByKey[up].Expanded && Snapshot(adapter).ItemsByKey[down].Expanded && frame.IsComplete;
            }, "Declared batch targets did not expand together.");
            Check(batchIntermediate && State().Revision == batchBusinessRevision, "Batch expansion lacked animation or wrote provider state.");

            await TemplateAction("add");
            await Until(() => Snapshot(adapter).Items.Count == 3 && TimerNativeRegression.Frame(adapter).IsComplete, "Repeated timer was not added.");
            var repeated = Snapshot(adapter).ItemsByKey.Keys.Single(key => key != up && key != down);
            Check(!Snapshot(adapter).ItemsByKey[repeated].Expanded && Snapshot(adapter).ItemsByKey[up].Expanded && Snapshot(adapter).ItemsByKey[down].Expanded,
                "New item inherited an old expansion instead of its declared initial state.");
            var repeatedBlank = Button(repeated);
            var repeatedHandle = Snapshot(adapter).ItemsByKey[repeated].Handle;
            var repeatBusiness = State();
            var repeatExpiries = Expiries(repeatBusiness);
            Reading(repeated, "repeat-start");
            await Until(() => Readings(adapter).Single(value => value.Key == repeated).IsOvertime,
                "Native repeated countdown did not expose overtime within its bounded wait.", TimeSpan.FromSeconds(10));
            var overtime = Reading(repeated, "repeat-overtime");
            var stopped = Reading(down, "countdown-zero");
            Check(overtime.Text.StartsWith("超时 ", StringComparison.Ordinal) && AutomationProperties.GetName(repeatedBlank) == overtime.Text,
                "Applied overtime marker did not reach the native timer control.");
            Check(stopped.ValueMilliseconds == 0 && !stopped.IsAdvancing && stopped.Text == "00:00",
                "Native countdown without overtime did not stop at zero.");
            Check(State().Revision == repeatBusiness.Revision && Expiries(State()).SequenceEqual(repeatExpiries),
                "Native zero/overtime sampling published or renewed provider state.");
            Check(TimerNativeRegression.Frame(adapter).IsComplete, "Stable timer screenshot still had a running geometry transition.");
            await ConsoleScreenshot.SaveAsync(island.ContentRoot!, Path.Combine(root, "timer-overtime-expanded.png")).WaitAsync(TimeSpan.FromSeconds(2));

            await TemplateAction("remove");
            await Until(() => !Snapshot(adapter).ItemsByKey.ContainsKey(repeated) && TimerNativeRegression.Frame(adapter).IsComplete, "Repeated item was not removed.");
            Check(display.ItemPresentations!.Resolve(repeatedHandle) is null && !repeatedBlank.IsEnabled && repeatedBlank.Content is null,
                "Removed timer retained its old occurrence handle or native handlers.");
            long removedRevision = State().Revision;
            try { Invoke(repeatedBlank); }
            catch (Exception error) { log("timer-retired-invoke: " + error.GetType().Name); }
            await Task.Delay(100);
            Check(State().Revision == removedRevision, "Old native appearance dispatched after removal.");
            SetReducedMotion(adapter, true);
            host.RequestRefresh();
            Invoke(secondary);
            await Until(() =>
            {
                var frame = Frame("reduced-batch-collapse");
                if (Snapshot(adapter).ItemsByKey[up].Expanded || Snapshot(adapter).ItemsByKey[down].Expanded) return false;
                Check(frame.IsComplete && frame.Items.All(value => !value.IsExiting && value.Opacity == 1), "Reduced motion exposed partial timer geometry.");
                return true;
            }, "Reduced-motion batch collapse did not settle.");
            await TemplateAction("add");
            await Until(() =>
            {
                var frame = Frame("reduced-new-item");
                if (Snapshot(adapter).Items.Count != 3) return false;
                Check(frame.IsComplete, "Reduced-motion add exposed an incomplete frame.");
                return true;
            }, "Reduced-motion repeated add failed.");
            var replacement = Snapshot(adapter).ItemsByKey.Keys.Single(key => key != up && key != down);
            Check(replacement != repeated && !Snapshot(adapter).ItemsByKey[replacement].Expanded && display.ItemPresentations.Resolve(repeatedHandle) is null,
                "Replacement reused an old item appearance or expansion state.");
            await TemplateAction("reset");
            await Until(() => Readings(adapter).Single(value => value.Key == down).ValueMilliseconds > 3000,
                "Explicit provider adjustment did not reanchor the native countdown.");
            Reading(down, "provider-reset");

            foreach (var row in settings.GetSnapshot().Components.Where(row => IsCounter(row.Identity)))
                Check(settings.SetVisibility(row.Identity, false).IsSuccess, "Could not hide timer entries.");
            await Until(() => host.Session.State == IslandDisplayState.Hidden && adapter.Handle == 0 && !NativeWindows.IsWindow(handle),
                "Hiding timers did not release native ownership.");
            Check(Readings(adapter).Count == 0 && !DriverRunning(adapter) && TryFrame(adapter) is null,
                "Hidden timer retained applied readings, animation or refresh driver.");
            Check(!upBlank.IsEnabled && upBlank.Content is null, "Hidden native timer retained input handlers.");
            await Task.Delay(150);
            Check(Readings(adapter).Count == 0 && !DriverRunning(adapter), "Late timer callback revived hidden display state.");
            log("timer-hidden-pass: appliedSamples=0; driverRunning=false; nativeOwnerReleased=true; oldControlsDisabled=true");

            ApplicationState State() => communication.States.GetSnapshot("counter")?.State ?? throw new InvalidOperationException("No confirmed timer provider state.");
            Button Button(TaskbarItemKey key, string suffix = "") => Find<Button>(island.ContentRoot!, ItemId(key) + suffix);
            TimerDisplayReading Reading(TaskbarItemKey key, string phase)
            {
                var reading = Readings(adapter).Single(value => value.Key == key);
                localSamples++;
                log("timer-reading: " + JsonSerializer.Serialize(new { phase, elapsedMs = scenario.ElapsedMilliseconds,
                    State().Revision, expiries = Expiries(State()), reading, nativeText = AutomationProperties.GetName(Button(key)), driverRunning = DriverRunning(adapter) }));
                return reading;
            }
            TaskbarGroupAnimationFrame Frame(string phase)
            {
                Check(adapter.Handle == handle, "Timer value/expansion refresh recreated its native HWND.");
                var frame = TimerNativeRegression.Frame(adapter);
                var rect = NativeWindows.WindowBounds(handle);
                double scale = NativeWindows.GetDpiForWindow(handle) / 96d;
                Check(Math.Abs(rect.Right - rect.Left - Math.Max(1, Math.Round(frame.WidthDip * scale))) <= 1,
                    "Applied timer frame width differs from native owner width.");
                if (phase != "initial") Check(rect.Right == fixedRight, "Timer shared group right edge drifted.");
                if (frames++ < 160) log("timer-frame: " + JsonSerializer.Serialize(new { phase, elapsedMs = scenario.ElapsedMilliseconds,
                    frame.Generation, frame.Revision, frame.WidthDip, frame.IsComplete, businessRevision = State().Revision,
                    native = new { rect.Left, rect.Top, rect.Right, rect.Bottom },
                    items = frame.Items.Select(value => new { value.Key.ItemId, value.Key.PresenceGeneration, value.Bounds, value.Opacity, value.IsExiting }) }));
                return frame;
            }
            async Task TemplateAction(string id)
            {
                var button = Find<Button>(island.ContentRoot!, "mtp-template-" + id);
                await Until(() => button.IsEnabled, "Timer template action is disabled: " + id);
                long revision = State().Revision;
                Invoke(button);
                await Until(() => State().Revision == revision + 1 && button.IsEnabled, "Timer template action did not confirm once: " + id);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            bool shutdown = host.Shutdown();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(35 - scenario.Elapsed.TotalSeconds, 0.01, 5)));
            try
            {
                await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(deadline.Token)));
                foreach (var process in processes) log($"timerOwnedPid={process.Id}; exited={process.HasExited}");
                Check(shutdown && (handle == 0 || !NativeWindows.IsWindow(handle)) && (bridge == 0 || !NativeWindows.IsWindow(bridge)),
                    "Timer Host failed native/process cleanup.");
            }
            catch (Exception cleanup) when (failure is not null) { log("timer-cleanup-after-failure: " + cleanup); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        Check(frames > 0 && localSamples >= 6 && scenario.Elapsed < TimeSpan.FromSeconds(35), "Timer scenario lacked evidence or exceeded its total 35-second budget.");
        log($"timer-pass: localSamples={localSamples}; frames={frames}; elapsedMs={scenario.Elapsed.TotalMilliseconds:F0}; localOnlySampling=true; fixedWidth=true; reverseAndBatchAnimation=true; internalActionNoBubble=true; zeroAndOvertime=true; reducedMotion=true; nativeAndProcessCleanup=true; evidence={root}");

        async Task Until(Func<bool> predicate, string message, TimeSpan? timeout = null)
        {
            var wait = Stopwatch.StartNew();
            while (!predicate())
            {
                if (wait.Elapsed >= (timeout ?? TimeSpan.FromSeconds(8)) || scenario.Elapsed >= TimeSpan.FromSeconds(35))
                    throw new InvalidOperationException(message + " Host errors: " + string.Join("; ", host.Errors.TakeLast(5)));
                await Task.Delay(10);
            }
            Check(scenario.Elapsed < TimeSpan.FromSeconds(35), "Timer native scenario exceeded 35 seconds.");
        }
    }

    private static bool IsCounter(StableIdentity identity) => identity.Segments.Count == 3 && identity.Segments[0].Value == "counter";
    private static DateTimeOffset[] Expiries(ApplicationState state) => state.DynamicEntries!.SelectMany(entry => entry.Content.Activities)
        .OrderBy(activity => activity.ActivityId, StringComparer.Ordinal).Select(activity => activity.ExpiresAt).ToArray();
    private static TaskbarGroupAnimationFrame? TryFrame(IslandDisplayAdapter adapter) =>
        (TaskbarGroupAnimationFrame?)adapter.GetType().GetMethod("GetGroupFrame", Members)!.Invoke(adapter, null);
    private static TaskbarGroupAnimationFrame Frame(IslandDisplayAdapter adapter) => TryFrame(adapter) ?? throw new InvalidOperationException("No applied timer geometry frame.");
    private static HostGroupPresentationSnapshot Snapshot(IslandDisplayAdapter adapter) =>
        (HostGroupPresentationSnapshot)(adapter.GetType().GetProperty("GroupSnapshot", Members)!.GetValue(adapter) ?? throw new InvalidOperationException("No timer group snapshot."));
    private static IReadOnlyList<TimerDisplayReading> Readings(IslandDisplayAdapter adapter) =>
        (IReadOnlyList<TimerDisplayReading>)adapter.GetType().GetMethod("GetTimerReadings", Members)!.Invoke(adapter, null)!;
    private static bool DriverRunning(IslandDisplayAdapter adapter) => (bool)adapter.GetType().GetProperty("TimerDriverRunning", Members)!.GetValue(adapter)!;
    private static void SetReducedMotion(IslandDisplayAdapter adapter, bool value) => adapter.GetType().GetProperty("ReducedMotionOverride", Members)!.SetValue(adapter, value);
    private static string ItemId(TaskbarItemKey key) => "mtp-item/" + string.Join("/", Uri.EscapeDataString(key.Component.ApplicationId),
        Uri.EscapeDataString(key.Component.FeatureGroupId), Uri.EscapeDataString(key.Component.ComponentId), Uri.EscapeDataString(key.ItemId), key.PresenceGeneration.ToString(CultureInfo.InvariantCulture));
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(value => AutomationProperties.GetAutomationId(value) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static T Field<T>(object value, string name) where T : class => (T)value.GetType().GetField(name, Members)!.GetValue(value)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

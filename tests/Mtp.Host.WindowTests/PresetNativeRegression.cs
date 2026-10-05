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

internal static class PresetNativeRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static async Task RunAsync(Action<string> log)
    {
        var clock = Stopwatch.StartNew();
        string root = Path.Combine(AppContext.BaseDirectory, "preset-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        using var target = new OwnedIslandTarget();
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")), target.Capture, root);
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(Path.Combine(root, "settings.json")),
            () => host.Applications, host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        Check(settings.SetAppearance(new(HostTheme.Light, MaterialKind.None, 1)).IsSuccess, "Could not prepare preset appearance.");
        var adapter = Field<IslandDisplayAdapter>(host, "adapter");
        Reduced(adapter, false);
        var processes = new List<Process>();
        nint handle = 0;
        int samples = 0;
        Exception? failure = null;
        try
        {
            await host.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), presets: true).WaitAsync(TimeSpan.FromSeconds(8));
            var communication = Field<HostBrokerSession>(host, "communication");
            foreach (int pid in communication.ServiceProcessIds.Prepend(communication.BrokerProcessId))
            {
                var process = Process.GetProcessById(pid); _ = process.Handle; processes.Add(process);
            }
            Check(processes.Count == 2, "Preset fixture did not own separate Broker and service processes.");
            await Until(() => settings.GetSnapshot().Components.Count(value => Counter(value.Identity)) == 2, "Preset declaration missing.");
            foreach (var row in settings.GetSnapshot().Components)
                Check(settings.SetVisibility(row.Identity, Counter(row.Identity)).IsSuccess, "Could not select preset entries.");
            await Until(() => host.Session.State == IslandDisplayState.Embedded && Frame(adapter) is { IsComplete: true, Items.Count: 6 } && Readings(adapter).Count == 6,
                "Six accepted preset items did not render.");
            var island = Field<ContentIslandHost>(adapter, "host");
            await Until(() => island.ContentRoot?.IsLoaded == true, "Preset island did not load.");
            handle = adapter.Handle;
            Check(NativeWindows.GetParent(handle) == target.Parent && NativeWindows.GetParent(island.Bridge) == handle, "Preset island escaped its owned parent.");
            var firstFrame = Frame(adapter)!;
            int right = NativeWindows.WindowBounds(handle).Right;
            var state = State();
            var expiry = Expiry();
            var compositeKey = Key("composite");
            var oldHandle = Snapshot(adapter).ItemsByKey[compositeKey].Handle;
            var compositeButton = Native<Button>("composite");
            Check(AutomationProperties.GetName(Native<Button>("counter-ring")).Contains("第4项", StringComparison.Ordinal), "Current index lacks its distinct semantic description.");
            Check(AutomationProperties.GetName(Native<Button>("counter-text")).Contains("已完成4个", StringComparison.Ordinal), "Completed count lacks its semantic description.");
            Check(Readings(adapter).Single(value => value.Key == Key("counter-ring")).ProgressFraction == 0.25,
                "Counter graphic inferred 4/10 instead of independent progress 25/100.");
            Check(Readings(adapter).Single(value => value.Key == Key("counter-text")).ProgressFraction is null, "Text-only counter acquired graphical progress.");
            Check(Native<TextBlock>("composite", "/Counter").Text == "4" && !string.IsNullOrWhiteSpace(Native<TextBlock>("composite", "/Timer").Text) &&
                Native<TextBlock>("composite", "/Text").Text == "就绪" && Native<TextBlock>("composite", "/ProgressText").Text.Contains("25", StringComparison.Ordinal),
                "Composite failed to preserve independent count, timer, status and progress fields.");
            Check(Native<Microsoft.UI.Xaml.Shapes.Path>("counter-ring", "/Ring").Visibility == Visibility.Visible &&
                Native<Microsoft.UI.Xaml.Shapes.Rectangle>("counter-bar", "/Bar").Visibility == Visibility.Visible,
                "Native ring and bar variants were not applied.");
            Sample("initial");
            await ConsoleScreenshot.SaveAsync(island.ContentRoot!, Path.Combine(root, "presets-initial.png")).WaitAsync(TimeSpan.FromSeconds(2));

            long revision = State().Revision;
            Invoke(ActionButton("forward"));
            await Until(() => State().Revision == revision + 1 && Current("counter-ring").ProgressFraction is > 0.25 and < 0.75,
                "Determinate progress never exposed an intermediate native fraction.");
            double interrupted = Current("counter-ring").ProgressFraction!.Value;
            Sample("forward-intermediate");
            await Until(() => ActionButton("backward").IsEnabled, "Reverse action remained busy.");
            Invoke(ActionButton("backward"));
            await Until(() => State().Revision == revision + 2 && Current("counter-ring").ProgressFraction is > 0.25 and < 0.75,
                "Rapid reverse did not retain an in-flight displayed fraction.");
            Sample("reverse-intermediate");
            await Until(() => Current("counter-ring").ProgressFraction == 0.25 && !Current("counter-ring").IsAnimating,
                "Reverse progress failed to settle at the latest target.");
            Check(interrupted > 0.25 && Expiry() == expiry, "Progress transition changed activity lifetime.");

            await Action("mode");
            await Until(() => Current("counter-ring").IsIndeterminate, "Indeterminate mode was not applied.");
            var busy = Current("counter-ring");
            long busyRevision = State().Revision;
            await Until(() => Current("counter-ring").BusyPhase != busy.BusyPhase, "Shared busy phase did not advance.");
            Check(Current("counter-ring").ProgressFraction is null && State().Revision == busyRevision && Expiry() == expiry,
                "Busy animation fabricated a ratio or generated business state.");
            Check(Native<Microsoft.UI.Xaml.Shapes.Ellipse>("counter-ring", "/Busy").Visibility == Visibility.Visible &&
                AutomationProperties.GetName(Native<Button>("counter-ring")).Contains("处理中", StringComparison.Ordinal),
                "Native indeterminate presentation omitted its explicit busy meaning.");
            Sample("indeterminate");
            await Action("semantics");
            await Until(() => AutomationProperties.GetName(Native<Button>("counter-ring")).Contains("已完成4个", StringComparison.Ordinal), "Counter semantic change did not reach accessibility text.");
            await Action("status");
            await Until(() => Current("status").TextOffsetDip < -1, "Measured long status did not scroll after its initial pause.");
            var statusText = Native<TextBlock>("status", "/Text");
            var statusTransform = (TranslateTransform)statusText.RenderTransform;
            Check(statusTransform.X < 0 && statusText.TextTrimming == TextTrimming.None &&
                AutomationProperties.GetName(Native<Button>("status")).Contains(statusText.Text, StringComparison.Ordinal),
                "Fixed-slot scrolling lost its full native tooltip/accessibility description.");
            Check(AutomationProperties.GetName(Native<FontIcon>("status", "/StatusMarker")) == "注意", "Attention marker did not reach native semantic icon.");
            Sample("scrolling-attention");
            long scrollRevision = State().Revision;
            await Task.Delay(200);
            Check(State().Revision == scrollRevision && Expiry() == expiry && Frame(adapter)!.WidthDip == firstFrame.WidthDip,
                "Text animation changed geometry, revision or expiry.");
            await Action("status");
            await Until(() => AutomationProperties.GetName(Native<FontIcon>("status", "/StatusMarker")) == "错误", "Error marker did not reach native semantic icon.");
            Reduced(adapter, true); host.RequestRefresh();
            await Until(() => Readings(adapter).All(value => !value.IsAnimating && value.TextOffsetDip == 0), "Reduced motion did not stop content movement.");
            Check(Current("counter-ring").IsIndeterminate && Current("counter-ring").ProgressFraction is null && statusTransform.X == 0 &&
                statusText.TextTrimming == TextTrimming.CharacterEllipsis, "Reduced motion removed busy meaning or failed to restore ellipsis.");
            Sample("reduced-motion");
            await ConsoleScreenshot.SaveAsync(island.ContentRoot!, Path.Combine(root, "presets-error-reduced.png")).WaitAsync(TimeSpan.FromSeconds(2));
            await Action("forward");
            await Until(() => Current("counter-ring").ProgressFraction == 0.75, "Reduced-motion progress did not jump to correct final fraction.");
            Check(!Current("counter-ring").IsAnimating, "Reduced-motion determinate progress retained a transition.");

            await Action("end");
            await Until(() => Snapshot(adapter).Items.Count == 0 && Readings(adapter).Count == 0 && TimerReadings(adapter).Count == 0 && Frame(adapter) is { IsComplete: true },
                "Ending activity did not remove all content tracks and items.");
            Check(!Driver(adapter) && !compositeButton.IsEnabled && compositeButton.Content is null && display.ItemPresentations!.Resolve(oldHandle) is null,
                "Ended presets retained animation driver, old visual or input handle.");
            foreach (var row in settings.GetSnapshot().Components.Where(value => Counter(value.Identity)))
                Check(settings.SetVisibility(row.Identity, false).IsSuccess, "Could not hide fixture entries.");
            await Until(() => adapter.Handle == 0 && !NativeWindows.IsWindow(handle), "Preset hide did not release native owner.");
            Check(Readings(adapter).Count == 0 && !Driver(adapter), "Hidden preset retained samples or driver.");
            log("preset-cleanup-pass: activityEndRemovedAllTracks=true; driverStopped=true; staleHandleRejected=true; hiddenOwnerReleased=true");

            ApplicationState State() => communication.States.GetSnapshot("counter")?.State ?? throw new InvalidOperationException("No confirmed preset state.");
            DateTimeOffset Expiry() => State().DynamicEntries!.Single(value => value.ComponentId == "presets").Content.Activities.Single().ExpiresAt;
            TaskbarItemKey Key(string id) => Snapshot(adapter).ItemsByKey.Keys.Single(value => value.ItemId == id);
            PresetMotionReading Current(string id) => Readings(adapter).Single(value => value.Key.ItemId == id);
            T Native<T>(string id, string suffix = "") where T : FrameworkElement => Find<T>(island.ContentRoot!, ItemId(Key(id)) + suffix);
            Button ActionButton(string id) => Find<Button>(island.ContentRoot!, "mtp-template-" + id);
            async Task Action(string id)
            {
                var button = ActionButton(id); await Until(() => button.IsEnabled, "Preset action disabled: " + id);
                long before = State().Revision; Invoke(button);
                await Until(() => State().Revision == before + 1 && button.IsEnabled, "Preset action did not confirm once: " + id);
            }
            void Sample(string phase)
            {
                samples++;
                var frame = Frame(adapter)!; var rect = NativeWindows.WindowBounds(handle);
                Check(adapter.Handle == handle && rect.Right == right && frame.WidthDip == firstFrame.WidthDip,
                    "Preset field refresh changed native owner, fixed right edge or declared width.");
                log("preset-frame: " + JsonSerializer.Serialize(new { phase, elapsedMs = clock.ElapsedMilliseconds, businessRevision = State().Revision,
                    expiry = Expiry(), frame.WidthDip, frame.IsComplete, native = new { rect.Left, rect.Top, rect.Right, rect.Bottom },
                    content = Readings(adapter), timer = TimerReadings(adapter), driverRunning = Driver(adapter) }));
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            bool shutdown = host.Shutdown();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(30 - clock.Elapsed.TotalSeconds, 0.01, 5)));
            try
            {
                await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(deadline.Token)));
                foreach (var process in processes) log($"presetOwnedPid={process.Id}; exited={process.HasExited}");
                Check(shutdown && (handle == 0 || !NativeWindows.IsWindow(handle)), "Preset native/process cleanup failed.");
            }
            catch (Exception cleanup) when (failure is not null) { log("preset-cleanup-after-failure: " + cleanup); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        Check(clock.Elapsed < TimeSpan.FromSeconds(30) && samples >= 6, "Preset scenario lacked evidence or exceeded 30 seconds.");
        log($"preset-pass: samples={samples}; elapsedMs={clock.Elapsed.TotalMilliseconds:F0}; trueSdk=true; variantsAndSemantics=true; interruption=true; busyAndMeasuredScroll=true; reducedMotion=true; nativeAndProcessCleanup=true; evidence={root}");

        async Task Until(Func<bool> condition, string error)
        {
            var wait = Stopwatch.StartNew();
            while (!condition())
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(6) || clock.Elapsed > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException(error + " Host errors: " + string.Join("; ", host.Errors.TakeLast(5)));
                await Task.Delay(10);
            }
        }
    }

    private static bool Counter(StableIdentity identity) => identity.Segments.Count == 3 && identity.Segments[0].Value == "counter";
    private static TaskbarGroupAnimationFrame? Frame(IslandDisplayAdapter adapter) => (TaskbarGroupAnimationFrame?)adapter.GetType().GetMethod("GetGroupFrame", Members)!.Invoke(adapter, null);
    private static HostGroupPresentationSnapshot Snapshot(IslandDisplayAdapter adapter) => (HostGroupPresentationSnapshot)adapter.GetType().GetProperty("GroupSnapshot", Members)!.GetValue(adapter)!;
    private static IReadOnlyList<PresetMotionReading> Readings(IslandDisplayAdapter adapter) => (IReadOnlyList<PresetMotionReading>)adapter.GetType().GetMethod("GetPresetReadings", Members)!.Invoke(adapter, null)!;
    private static IReadOnlyList<TimerDisplayReading> TimerReadings(IslandDisplayAdapter adapter) => (IReadOnlyList<TimerDisplayReading>)adapter.GetType().GetMethod("GetTimerReadings", Members)!.Invoke(adapter, null)!;
    private static bool Driver(IslandDisplayAdapter adapter) => (bool)adapter.GetType().GetProperty("TimerDriverRunning", Members)!.GetValue(adapter)!;
    private static void Reduced(IslandDisplayAdapter adapter, bool value) => adapter.GetType().GetProperty("ReducedMotionOverride", Members)!.SetValue(adapter, value);
    private static string ItemId(TaskbarItemKey key) => "mtp-item/" + string.Join("/", Uri.EscapeDataString(key.Component.ApplicationId), Uri.EscapeDataString(key.Component.FeatureGroupId),
        Uri.EscapeDataString(key.Component.ComponentId), Uri.EscapeDataString(key.ItemId), key.PresenceGeneration.ToString(CultureInfo.InvariantCulture));
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement => Descendants(root).OfType<T>().Single(value => AutomationProperties.GetAutomationId(value) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Field<T>(object value, string name) where T : class => (T)value.GetType().GetField(name, Members)!.GetValue(value)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

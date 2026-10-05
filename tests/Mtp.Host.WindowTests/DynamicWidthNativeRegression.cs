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
using Mtp.Host.Islands;
using Mtp.Platform.Core;
using Windows.Foundation;

namespace Mtp.Host.WindowTests;

/// <summary>Production processes, native action controls and actual child HWND geometry on an MTP-owned parent.</summary>
internal static class DynamicWidthNativeRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TaskbarComponentKey Left = new("counter", "main", "left");
    private static readonly TaskbarComponentKey Right = new("counter", "main", "right");

    public static async Task RunAsync(Action<string> log)
    {
        var scenario = Stopwatch.StartNew();
        string root = Path.Combine(AppContext.BaseDirectory, "dynamic-width-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        using var target = new OwnedIslandTarget();
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")), target.Capture, root);
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(Path.Combine(root, "settings.json")),
            () => host.Applications, host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        Check(settings.SetAppearance(new(HostTheme.Light, MaterialKind.None, 1)).IsSuccess,
            "Could not select opaque fixture appearance for readable XAML screenshots.");
        var adapter = Field<IslandDisplayAdapter>(host, "adapter");
        SetReducedMotion(adapter, false);
        var processes = new List<Process>();
        nint handle = 0;
        int loggedFrames = 0;
        try
        {
            await host.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), dynamic: true).WaitAsync(TimeSpan.FromSeconds(8));
            var communication = Field<HostBrokerSession>(host, "communication");
            foreach (int pid in communication.ServiceProcessIds.Prepend(communication.BrokerProcessId))
            {
                var process = Process.GetProcessById(pid);
                _ = process.Handle;
                processes.Add(process);
            }
            Check(processes.Count == 2 && processes.All(process => process.Id != Environment.ProcessId), "Fixture must own separate Broker and Counter processes.");
            await Until(() => settings.GetSnapshot().Components.Count(row => IsCounter(row.Identity)) == 3, "Dynamic declaration missing.");
            foreach (var row in settings.GetSnapshot().Components)
                Check(settings.SetVisibility(row.Identity, IsCounter(row.Identity)).IsSuccess, "Could not set fixture visibility.");
            // Move the three declared rows to the front in the exact stable order, independent of existing local entries.
            foreach (string name in new[] { "right", "items", "left" })
            {
                var identity = settings.GetSnapshot().Components.Single(row => IsCounter(row.Identity) && row.Identity.Segments[2].Value == name).Identity;
                for (int guard = 0; settings.GetSnapshot().Components[0].Identity != identity && guard < 16; guard++)
                    Check(settings.MoveComponent(identity, -1).IsSuccess, "Could not order fixture components.");
                Check(settings.GetSnapshot().Components[0].Identity == identity, "Fixture ordering exceeded bounded setup.");
            }
            await Until(() => host.Session.State == IslandDisplayState.Embedded && TryFrame(adapter) is { IsComplete: true, Items.Count: 2 }, "Initial two-item animation did not finish.");
            var island = Field<ContentIslandHost>(adapter, "host");
            await Until(() => island.ContentRoot?.IsLoaded == true, "Dynamic island did not load.");
            handle = adapter.Handle;
            Check(handle != 0 && NativeWindows.GetParent(handle) == target.Parent && NativeWindows.GetParent(island.Bridge) == handle,
                "Dynamic rendering escaped the owned parent chain.");
            var baseline = Sample("initial");
            var initial = Snapshot(adapter);
            Check(initial.Layout.Components.Select(component => component.Key.ComponentId).SequenceEqual(new[] { "left", "items", "right" }), "Visible group order is wrong.");
            Check(baseline.Items.Select(item => item.Bounds.Width).Distinct().Count() == 2, "Initial repeated items must have different widths.");
            var fixedRect = NativeWindows.WindowBounds(handle);
            double fixedRightX = baseline.Components.Single(component => component.Key == Right).Bounds.X;
            double originalLeftX = baseline.Components.Single(component => component.Key == Left).Bounds.X;
            var rightControl = Find<Grid>(island.ContentRoot!, "mtp-component/counter/main/right");
            double fixedNativeRightX = NativeX(rightControl, island, handle);
            var firstKey = initial.ItemsByKey.Keys.First();
            var firstButton = Find<Button>(island.ContentRoot!, ItemId(firstKey));
            long firstReading = initial.ItemsByKey[firstKey].Item.Fields.Counter!.Value;

            await Until(() => Snapshot(adapter).ItemsByKey[firstKey].Item.Fields.Counter!.Value != firstReading, "Confirmed readings did not advance.");
            var readingFrame = Sample("reading-only");
            Check(Math.Abs(readingFrame.WidthDip - baseline.WidthDip) < 0.001 && readingFrame.Items.Select(item => item.Bounds).SequenceEqual(baseline.Items.Select(item => item.Bounds)),
                "Reading-only update changed geometry.");
            Check(ReferenceEquals(firstButton, Find<Button>(island.ContentRoot!, ItemId(firstKey))), "Reading-only update replaced its native control.");
            Check(AutomationProperties.GetName(firstButton) == $"已完成{Snapshot(adapter).ItemsByKey[firstKey].Item.Fields.Counter!.Value}个",
                "Native item text did not match its confirmed reading.");
            await ConsoleScreenshot.SaveAsync(island.ContentRoot!, Path.Combine(root, "dynamic-initial.png")).WaitAsync(TimeSpan.FromSeconds(2));

            await Ready("mtp-action/counter/main/left");
            Invoke(Find<Button>(island.ContentRoot!, "mtp-action/counter/main/left"));
            bool addedIntermediate = false;
            await Until(() =>
            {
                var frame = Sample("add");
                AssertAnchors(frame);
                addedIntermediate |= !frame.IsComplete && frame.WidthDip > baseline.WidthDip && Snapshot(adapter).Items.Count == 3;
                return Snapshot(adapter).Items.Count == 3 && frame.IsComplete;
            }, "Native add action did not settle three items.");
            var added = Sample("added");
            Check(addedIntermediate, "No intermediate native add frame was observed.");
            Check(added.WidthDip > baseline.WidthDip && added.Components.Single(component => component.Key == Left).Bounds.X < originalLeftX,
                "Added item did not increase native group width and move the left neighbor.");
            await ConsoleScreenshot.SaveAsync(island.ContentRoot!, Path.Combine(root, "dynamic-added.png")).WaitAsync(TimeSpan.FromSeconds(2));
            var outgoingKey = Snapshot(adapter).ItemsByKey.Keys.Single(key => !initial.ItemsByKey.ContainsKey(key));
            var outgoing = Find<Button>(island.ContentRoot!, ItemId(outgoingKey));
            var outgoingHandle = Snapshot(adapter).ItemsByKey[outgoingKey].Handle;

            await Ready("mtp-action/counter/main/right");
            Invoke(Find<Button>(island.ContentRoot!, "mtp-action/counter/main/right"));
            await Until(() =>
            {
                var frame = Sample("remove");
                AssertAnchors(frame);
                return frame.Items.Any(item => item.Key == outgoingKey && item.IsExiting && item.Opacity > 0);
            }, "No visible retiring native item was observed.");
            Check(!outgoing.IsEnabled && !outgoing.IsHitTestVisible && !outgoing.IsTabStop, "Retiring native item still accepts input or focus.");
            Check(display.ItemPresentations!.Resolve(outgoingHandle) is null, "Retired item handle still resolves in production controller.");
            TryInvokeDisabled(outgoing, log);
            Check(!Snapshot(adapter).ItemsByKey.ContainsKey(outgoingKey), "Invoking the retiring button restored its old item.");
            var interrupted = Frame(adapter);
            await Ready("mtp-action/counter/main/left");
            Invoke(Find<Button>(island.ContentRoot!, "mtp-action/counter/main/left"));
            bool interruptionObserved = false;
            await Until(() =>
            {
                var frame = Sample("interrupted-add");
                AssertAnchors(frame);
                interruptionObserved |= frame.Revision > interrupted.Revision && !frame.IsComplete && Snapshot(adapter).Items.Count == 3;
                return Snapshot(adapter).Items.Count == 3 && frame.IsComplete;
            }, "Resize interruption did not settle.");
            Check(interruptionObserved, "No interrupted native animation was observed.");
            Check(outgoing.Content is null && !Descendants(island.ContentRoot!).Contains(outgoing), "Retired native visual or handlers were not released.");
            Check(!Frame(adapter).Items.Any(item => item.IsExiting), "Settled frame retained retired tracks.");

            SetReducedMotion(adapter, true);
            host.RequestRefresh();
            for (int expected = 2; expected >= 0; expected--)
            {
                await Ready("mtp-action/counter/main/right");
                Invoke(Find<Button>(island.ContentRoot!, "mtp-action/counter/main/right"));
                await Until(() =>
                {
                    var frame = Sample("reduced-remove-" + expected);
                    AssertAnchors(frame);
                    if (Snapshot(adapter).Items.Count != expected) return false;
                    Check(frame.IsComplete && frame.Items.Count == expected && frame.Items.All(item => !item.IsExiting && item.Opacity == 1),
                        "Reduced motion exposed a partial or retiring frame after confirmed membership changed.");
                    return true;
                }, "Reduced-motion native removal did not reach its final frame.");
            }
            Check(Snapshot(adapter).Layout.Components.Select(component => component.Key.ComponentId).SequenceEqual(new[] { "left", "right" }),
                "Empty dynamic component retained width or a layout entry.");
            Check(!Descendants(island.ContentRoot!).OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button).StartsWith("mtp-item/", StringComparison.Ordinal)),
                "Empty collection retained native item buttons.");
            var emptyDynamic = Sample("empty-dynamic");
            Check(emptyDynamic.WidthDip < baseline.WidthDip, "Last item removal did not reclaim dynamic width.");
            foreach (var row in settings.GetSnapshot().Components.Where(row => IsCounter(row.Identity) && row.Identity.Segments[2].Value != "items"))
                Check(settings.SetVisibility(row.Identity, false).IsSuccess, "Could not hide static neighbors.");
            await Until(() => TryFrame(adapter) is { IsComplete: true, WidthDip: 0 } && !NativeWindows.IsWindowVisible(handle),
                "Empty visible dynamic declaration did not retain a hidden zero-width group.");
            Check(adapter.Handle == handle && NativeWindows.IsWindow(handle), "Empty dynamic declaration unnecessarily replaced its reusable owner.");
            Check(!Descendants(island.ContentRoot!).OfType<Button>().Any(button => button.IsEnabled && button.IsHitTestVisible),
                "Empty hidden group retained an interactive control.");
            Sample("empty-hidden-owner");
            var itemIdentity = settings.GetSnapshot().Components.Single(row => IsCounter(row.Identity) && row.Identity.Segments[2].Value == "items").Identity;
            Check(settings.SetVisibility(itemIdentity, false).IsSuccess, "Could not hide the dynamic declaration.");
            await Until(() => host.Session.State == IslandDisplayState.Hidden && !NativeWindows.IsWindow(handle), "Hidden declarations did not release their native HWND.");
            Check(TryFrame(adapter) is null && adapter.Handle == 0, "Closed group retained a frame or window ownership.");
            Check(!firstButton.IsEnabled && firstButton.Content is null, "Closed group retained its original interactive visual.");

            // Match the controller's unchanged default diagnostic configuration so only mode identity can cause replacement.
            Check(settings.SetAppearance(new(HostTheme.System, MaterialKind.Solid, 0)).IsSuccess, "Could not prepare matching mode-switch appearance.");
            var leftIdentity = settings.GetSnapshot().Components.Single(row => IsCounter(row.Identity) && row.Identity.Segments[2].Value == "left").Identity;
            Check(settings.SetVisibility(leftIdentity, true).IsSuccess, "Could not restore a static group for mode-switch regression.");
            await Until(() => host.Session.State == IslandDisplayState.Embedded && TryFrame(adapter) is { IsComplete: true }, "Static group did not reopen.");
            var groupIsland = Field<ContentIslandHost>(adapter, "host");
            await Until(() => groupIsland.ContentRoot?.IsLoaded == true, "Restored group did not load.");
            var groupAction = Find<Button>(groupIsland.ContentRoot!, "mtp-action/counter/main/left");
            var matchingConfiguration = (HostTestConfiguration)host.GetType().GetProperty("DisplayConfiguration", Members)!.GetValue(host)!;
            Check(matchingConfiguration == Field<HostTestConfiguration>(host, "applied") && matchingConfiguration.IsValid,
                "Mode-switch fixture changed configuration; it would not isolate the mode environment key.");
            var groupHandle = adapter.Handle;
            long groupOwnership = NativeWindows.Ownership(groupHandle);
            host.Tests.Start(matchingConfiguration);
            await Until(() => host.Tests.IsRunning && host.Session.State == IslandDisplayState.Embedded && TryFrame(adapter) is null &&
                !ReferenceEquals(groupIsland, Field<ContentIslandHost>(adapter, "host")), "Same-configuration diagnostic mode reused the previous group surface.");
            var diagnosticIsland = Field<ContentIslandHost>(adapter, "host");
            await Until(() => diagnosticIsland.ContentRoot?.IsLoaded == true, "Diagnostic surface did not load.");
            Check(!NativeWindows.HasOwnership(groupHandle, groupOwnership) && !groupAction.IsEnabled,
                "Diagnostic mode retained old group ownership or enabled controls.");
            Check(!Descendants(diagnosticIsland.ContentRoot!).OfType<FrameworkElement>().Any(element => AutomationProperties.GetAutomationId(element) == "MtpTaskbarGroupSurface"),
                "Diagnostic mode still contains a group surface.");
            host.Tests.Stop("dynamic-mode-roundtrip");
            await Until(() => !host.Tests.IsRunning && host.Session.State == IslandDisplayState.Embedded && TryFrame(adapter) is { IsComplete: true },
                "Stopping diagnostics did not restore the latest group intent.");
            var restoredHandle = adapter.Handle;
            var hideRestored = settings.SetVisibility(leftIdentity, false);
            Check(hideRestored.IsSuccess, "Could not hide restored static group: " + JsonSerializer.Serialize(hideRestored.Error));
            await Until(() => host.Session.State == IslandDisplayState.Hidden && adapter.Handle == 0 && !NativeWindows.IsWindow(restoredHandle),
                "Mode roundtrip leaked its restored native owner.");
            log("dynamic-mode-pass: unchanged configuration replaced group with diagnostic surface; Stop restored group; final visibility cleared owner.");
            log($"dynamic-scenario-pass: native widths/readings, fixed right anchor/neighbors, add/remove/interruption, retired input rejection, reduced motion and empty visual cleanup; frames={loggedFrames}; evidence={root}");

            TaskbarGroupAnimationFrame Sample(string phase)
            {
                Check(adapter.Handle == handle, "An ordinary membership/reading update recreated the native HWND.");
                var frame = Frame(adapter);
                var rect = NativeWindows.WindowBounds(handle);
                double scale = NativeWindows.GetDpiForWindow(handle) / 96d;
                Check(Math.Abs((rect.Right - rect.Left) - Math.Max(1, Math.Round(frame.WidthDip * scale))) <= 1,
                    "Native HWND width differs from the applied frame.");
                Check(Math.Abs((rect.Bottom - rect.Top) - Math.Round(frame.HeightDip * scale)) <= 1, "Native HWND height differs from the applied frame.");
                if (loggedFrames++ < 96)
                    log("dynamic-frame: " + JsonSerializer.Serialize(new
                    {
                        phase, elapsedMs = scenario.ElapsedMilliseconds, frame.Generation, frame.Revision, frame.WidthDip, frame.IsComplete,
                        native = new { rect.Left, rect.Top, rect.Right, rect.Bottom },
                        items = frame.Items.Select(item => new { item.Key.ItemId, item.Key.PresenceGeneration, item.Bounds, item.Opacity, item.IsInteractive, item.IsExiting })
                    }));
                return frame;
            }

            void AssertAnchors(TaskbarGroupAnimationFrame frame)
            {
                var rect = NativeWindows.WindowBounds(handle);
                Check(rect.Right == fixedRect.Right, "Native group right edge drifted during animation.");
                Check(Math.Abs(frame.Components.Single(component => component.Key == Right).Bounds.X - fixedRightX) < 0.001,
                    "Right-neighbor frame position drifted.");
                double actualRightX = NativeX(rightControl, island, handle);
                if (Math.Abs(actualRightX - fixedNativeRightX) > 2)
                {
                    var transform = rightControl.TransformToVisual(island.ContentRoot!).TransformPoint(new Point(0, 0));
                    var canvas = VisualTreeHelper.GetParent(rightControl) as Canvas;
                    var surface = Descendants(island.ContentRoot!).OfType<FrameworkElement>()
                        .Single(value => AutomationProperties.GetAutomationId(value) == "MtpTaskbarGroupSurface");
                    double scale = NativeWindows.GetDpiForWindow(handle) / 96d;
                    log("dynamic-anchor-layout-failure: " + JsonSerializer.Serialize(new
                    {
                        elapsedMs = scenario.ElapsedMilliseconds, frame.Generation, frame.Revision, frame.WidthDip,
                        expectedNativeRightX = fixedNativeRightX, actualNativeRightX = actualRightX,
                        deltaPixels = actualRightX - fixedNativeRightX,
                        frameRightX = frame.Components.Single(component => component.Key == Right).Bounds.X,
                        expectedCanvasLeft = frame.Components.Single(component => component.Key == Right).Bounds.X + frame.WidthDip,
                        canvasLeft = Canvas.GetLeft(rightControl), transformX = transform.X, transformY = transform.Y,
                        rightWidth = rightControl.Width, rightActualWidth = rightControl.ActualWidth,
                        canvasWidth = canvas?.Width, canvasActualWidth = canvas?.ActualWidth,
                        surfaceWidth = surface.Width, surfaceActualWidth = surface.ActualWidth,
                        surfaceTransformX = surface.TransformToVisual(island.ContentRoot!).TransformPoint(new Point(0, 0)).X,
                        rootWidth = island.ContentRoot!.Width, rootActualWidth = island.ContentRoot.ActualWidth,
                        xamlRootWidth = island.ContentRoot.XamlRoot?.Size.Width,
                        rasterizationScale = island.ContentRoot.XamlRoot?.RasterizationScale, dpiScale = scale,
                        native = new { rect.Left, rect.Top, rect.Right, rect.Bottom },
                        predictedNativeFromCanvas = rect.Left + Canvas.GetLeft(rightControl) * scale
                    }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                }
                Check(Math.Abs(actualRightX - fixedNativeRightX) <= 2,
                    "Actual right-neighbor XAML position drifted on the native surface.");
            }

            Task Ready(string id) => Until(() => Find<Button>(island.ContentRoot!, id).IsEnabled, "Native action remained disabled: " + id);
        }
        finally
        {
            bool shutdown = host.Shutdown();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(30 - scenario.Elapsed.TotalSeconds, 0.01, 8)));
            try
            {
                await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(deadline.Token)));
                foreach (var process in processes) log($"dynamicOwnedPid={process.Id}; exited={process.HasExited}");
                Check(shutdown && (handle == 0 || !NativeWindows.IsWindow(handle)), "Dynamic Host native cleanup failed.");
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        Check(loggedFrames > 0 && scenario.Elapsed < TimeSpan.FromSeconds(30), "Dynamic native regression lacked frames or exceeded its total time budget.");
        log($"dynamic-pass: frames={loggedFrames}; recordedFrames={Math.Min(loggedFrames, 96)}; elapsedMs={scenario.Elapsed.TotalMilliseconds:F0}; nativeAndProcessCleanup=true; evidence={root}");

        async Task Until(Func<bool> predicate, string failure)
        {
            var wait = Stopwatch.StartNew();
            while (!predicate())
            {
                if (wait.Elapsed >= TimeSpan.FromSeconds(8) || scenario.Elapsed >= TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException(failure + " Host errors: " + string.Join("; ", host.Errors.TakeLast(5)));
                await Task.Delay(10);
            }
            Check(scenario.Elapsed < TimeSpan.FromSeconds(30), "Dynamic native scenario exceeded 30 seconds.");
        }
    }

    private static bool IsCounter(StableIdentity identity) => identity.Segments.Count == 3 && identity.Segments[0].Value == "counter";
    private static TaskbarGroupAnimationFrame? TryFrame(IslandDisplayAdapter adapter) =>
        (TaskbarGroupAnimationFrame?)adapter.GetType().GetMethod("GetGroupFrame", Members)!.Invoke(adapter, null);
    private static TaskbarGroupAnimationFrame Frame(IslandDisplayAdapter adapter) => TryFrame(adapter) ?? throw new InvalidOperationException("No applied group frame.");
    private static HostGroupPresentationSnapshot Snapshot(IslandDisplayAdapter adapter) =>
        (HostGroupPresentationSnapshot)(adapter.GetType().GetProperty("GroupSnapshot", Members)!.GetValue(adapter) ?? throw new InvalidOperationException("No current group snapshot."));
    private static void SetReducedMotion(IslandDisplayAdapter adapter, bool value) => adapter.GetType().GetProperty("ReducedMotionOverride", Members)!.SetValue(adapter, value);
    private static string ItemId(TaskbarItemKey key) => "mtp-item/" + string.Join("/", Uri.EscapeDataString(key.Component.ApplicationId),
        Uri.EscapeDataString(key.Component.FeatureGroupId), Uri.EscapeDataString(key.Component.ComponentId), Uri.EscapeDataString(key.ItemId),
        key.PresenceGeneration.ToString(CultureInfo.InvariantCulture));
    private static double NativeX(FrameworkElement element, ContentIslandHost island, nint handle) => NativeWindows.WindowBounds(handle).Left +
        element.TransformToVisual(island.ContentRoot!).TransformPoint(new Point(0, 0)).X * NativeWindows.GetDpiForWindow(handle) / 96d;
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void TryInvokeDisabled(Button button, Action<string> log)
    {
        Check(!button.IsEnabled, "Retired button was enabled before native invoke.");
        try { Invoke(button); log("dynamic-retired-invoke: disabled peer returned without dispatch"); }
        catch (Exception error) { log("dynamic-retired-invoke: " + error.GetType().Name); }
    }
    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(value => AutomationProperties.GetAutomationId(value) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, index))) yield return item;
    }
    private static T Field<T>(object value, string name) where T : class => (T)value.GetType().GetField(name, Members)!.GetValue(value)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

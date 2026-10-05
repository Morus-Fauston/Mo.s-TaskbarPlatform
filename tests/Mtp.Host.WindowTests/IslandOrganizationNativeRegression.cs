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

/// <summary>Exercises real SDK state, production settings controls and the native group surface.</summary>
internal static class IslandOrganizationNativeRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static async Task RunAsync(Action<string> log)
    {
        var clock = Stopwatch.StartNew();
        string root = Path.Combine(AppContext.BaseDirectory, "organization-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "settings.json");
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json")));
        using var target = new OwnedIslandTarget();
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")), target.Capture, root);
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(settingsPath), () => host.Applications,
            host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        Check(settings.SetAppearance(new(HostTheme.Light, MaterialKind.None, 1)).IsSuccess, "Could not select organization fixture appearance.");
        var adapter = Field<IslandDisplayAdapter>(host, "adapter");
        adapter.ReducedMotionOverride = false;
        var window = new SettingsWindow(settings, host, () => [new("owned-primary", true, new(-30000, -30000, 1600, 900), new(-30000, -30000, 1600, 860), 96)]);
        var processes = new List<Process>();
        nint nativeHandle = 0;
        int samples = 0;
        Exception? failure = null;
        try
        {
            window.AppWindow.Move(new(-30000, -30000));
            window.AppWindow.Show(false);
            await Until(() => ((FrameworkElement)window.Content).IsLoaded, "Organization settings failed to load.");
            await host.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), organization: true).WaitAsync(TimeSpan.FromSeconds(8));
            var communication = Field<HostBrokerSession>(host, "communication");
            foreach (int pid in communication.ServiceProcessIds.Prepend(communication.BrokerProcessId))
            { var process = Process.GetProcessById(pid); _ = process.Handle; processes.Add(process); }
            Check(processes.Count == 2, "Organization fixture needs separate Broker and SDK processes.");
            await Until(() => settings.GetSnapshot() is var snapshot &&
                snapshot.Components.Count(value => value.Identity.Segments[0].Value == "counter") == 6 &&
                snapshot.Groupings.Count(value => value.Identity.Segments[0].Value == "counter") == 3,
                "Organization declaration was not accepted.");
            Find<NavigationView>(window.Content, "mtp-settings-navigation").SelectedItem = Find<NavigationViewItem>(window.Content, "mtp-settings-nav-layout");
            foreach (var component in settings.GetSnapshot().Components)
                Check(settings.SetVisibility(component.Identity, component.Identity.Segments[0].Value == "counter" &&
                    component.Identity.LocalId.Value is not ("together" or "separate")).IsSuccess, "Could not select fixture entries.");
            await Until(() => host.Session.State == IslandDisplayState.Embedded && Frame(adapter) is { IsComplete: true, Items.Count: 7 }, "Initial organization items did not render.");
            var island = Field<ContentIslandHost>(adapter, "host");
            await Until(() => island.ContentRoot?.IsLoaded == true, "Organization island not loaded.");
            nativeHandle = adapter.Handle;
            int nativeRight = NativeWindows.WindowBounds(nativeHandle).Right;
            var identity = settings.GetSnapshot().Groupings.Single(value => value.Identity.LocalId.Value == "islands").Identity;
            var combo = Find<ComboBox>(window.Content, "mtp-settings-grouping-counter/main/islands");
            Check(combo.IsEnabled && combo.Items.Count == 2, "UserChoice did not expose both organization choices.");
            foreach (string fixedId in new[] { "together", "separate" })
            {
                var fixedCombo = Find<ComboBox>(window.Content, "mtp-settings-grouping-counter/main/" + fixedId);
                Check(!fixedCombo.IsEnabled && fixedCombo.Items.Count == 1, "Fixed grouping exposed unsupported choices.");
            }
            var originalKey = Key("a1-status");
            var originalItem = Snapshot(adapter).ItemsByKey[originalKey];
            var originalButton = ItemButton(originalKey);
            Invoke(originalButton);
            await Until(() => Snapshot(adapter).ItemsByKey[originalKey].Expanded && Frame(adapter)!.IsComplete, "Native item did not expand.");
            long revision = State().Revision;
            var activities = Content().Activities.ToArray();
            Sample("together-expanded");
            Select(DynamicGrouping.Separate);
            await Until(() => Snapshot(adapter).Instances.Count == 2 && Frame(adapter) is { IsComplete: false }, "Split never exposed intermediate geometry.");
            Sample("split-intermediate");
            Select(DynamicGrouping.Together);
            await Until(() => Snapshot(adapter).Instances.Count == 1, "Interrupted merge was not projected.");
            Select(DynamicGrouping.Separate);
            await Until(() => Snapshot(adapter).Instances.Count == 2 && Frame(adapter)!.IsComplete, "Latest split target did not settle.");
            Check(Snapshot(adapter).Instances.Select(value => value.InstanceId).SequenceEqual(new[] { "a3", "a1" }), "Connected activity order is incorrect.");
            Check(Snapshot(adapter).ItemsByKey.Count == 7 && Snapshot(adapter).ItemsByKey[originalKey].Expanded &&
                Snapshot(adapter).ItemsByKey[originalKey].Handle == originalItem.Handle && ReferenceEquals(originalButton, ItemButton(originalKey)),
                "Regrouping duplicated items or replaced native identity/expansion.");
            Check(State().Revision == revision && activities.SequenceEqual(Content().Activities), "Organization mutated business activity state.");
            Sample("split-settled");
            await ConsoleScreenshot.SaveAsync(island.ContentRoot!, Path.Combine(root, "organization-split.png")).WaitAsync(TimeSpan.FromSeconds(2));
            await Action("shared");
            await Until(() => Snapshot(adapter).Instances.Count == 3 && Frame(adapter)!.IsComplete, "Removing shared reference did not separate three activities.");
            Check(Snapshot(adapter).Instances.Select(value => value.InstanceId).SequenceEqual(new[] { "a3", "a2", "a1" }), "Provider activity order was lost.");
            Check(ReferenceEquals(originalButton, ItemButton(originalKey)), "Shared item removal recreated an unrelated native item.");
            Sample("three-instances");
            // Hidden local declarations also have stable settings positions; move through the actual list.
            for (int i = 0; i < 16; i++)
            {
                var rows = settings.GetSnapshot().Components.ToList();
                int from = rows.FindIndex(value => value.Identity == identity);
                int left = rows.FindIndex(value => value.Identity.Segments[0].Value == "counter" && value.Identity.LocalId.Value == "left");
                if (from > left) break;
                Invoke(Find<Button>(window.Content, "mtp-settings-down-counter/main/islands"));
            }
            await Until(() => Snapshot(adapter).Components[0].Identity.LocalId.Value == "left" && Frame(adapter)!.IsComplete,
                "Stable entry ordering did not move its instances together.");
            Sample("mixed-order");
            var visible = Find<ToggleSwitch>(window.Content, "mtp-settings-visible-counter/main/islands");
            visible.IsOn = false;
            await Until(() => Snapshot(adapter).Instances.Count == 0, "Hidden entry retained island width.");
            var heldIds = Content().Activities.Select(value => value.ActivityId).ToArray();
            await Action("update");
            var add = Find<Button>(island.ContentRoot!, "mtp-template-add");
            long hiddenRevision = State().Revision;
            bool observedBusy = false;
            PendingActionSnapshot? pendingAdd = null;
            long enabledSubscription = add.RegisterPropertyChangedCallback(Control.IsEnabledProperty,
                (sender, _) => { if (sender is Control { IsEnabled: false }) observedBusy = true; });
            try
            {
                Invoke(add);
                pendingAdd = communication.Actions.GetPending("counter").SingleOrDefault(value => value.Slot.EntryId == "controls" && value.Slot.ActionSlotId == "add");
                Check(observedBusy || pendingAdd is not null, "Native add click did not expose a real in-flight request.");
                await Until(() => add.IsEnabled && (pendingAdd is null || !communication.Actions.GetPending("counter").Any(value => value.RequestId == pendingAdd.RequestId)),
                    "Native add request did not finish.");
            }
            finally { add.UnregisterPropertyChangedCallback(Control.IsEnabledProperty, enabledSubscription); }
            Check(Content().Activities.Select(value => value.ActivityId).SequenceEqual(heldIds), "Hidden entry admitted a new activity.");
            log("organization-hidden-native-click: " + JsonSerializer.Serialize(new
            {
                observedBusy, requestId = pendingAdd?.RequestId, completed = true,
                beforeRevision = hiddenRevision, afterRevision = State().Revision,
                help = AutomationProperties.GetHelpText(add), heldIds,
                evidenceScope = "Native click completed; confirmed activities did not increase. This is not a captured click receipt."
            }));
            // Capture a separate real, same-session receipt. Permission notification is asynchronous:
            // the provider may reject, or Host may admit the frame while filtering new activities.
            var currentApplication = communication.States.GetSnapshot("counter")!;
            var addSlot = currentApplication.Declaration!.ActionSlots.Single(value => value.Reference.EntryId == "controls" && value.Reference.ActionSlotId == "add").Reference;
            long receiptBeforeRevision = State().Revision;
            using (var receiptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var receipt = await communication.SendActionAsync(addSlot, new(), receiptDeadline.Token, currentApplication.SessionId);
                Check(communication.States.GetSnapshot("counter")!.SessionId == currentApplication.SessionId,
                    "Creation receipt crossed a service session.");
                bool providerDenied = !receipt.Accepted && receipt.Code == "DisplayNotAllowed" && receipt.ActivityRejections is null;
                bool hostFiltered = receipt.Accepted && receipt.Code == "AcceptedWithActivityRejections" && receipt.ActivityRejections is { Count: > 0 } rejected &&
                    rejected.All(value => value.ApplicationId == "counter" && value.FeatureGroupId == "main" && value.ComponentId == "islands" &&
                        !string.IsNullOrWhiteSpace(value.ActivityId) && !heldIds.Contains(value.ActivityId, StringComparer.Ordinal) && value.Code == "DisplayNotAllowed") &&
                    rejected.Select(value => value.ActivityId).Distinct(StringComparer.Ordinal).Count() == rejected.Count;
                log("organization-hidden-direct-receipt: " + JsonSerializer.Serialize(new
                {
                    session = currentApplication.SessionId, separateFromNativeClick = true, receipt,
                    classification = providerDenied ? "provider-knew-permission" : hostFiltered ? "host-authoritative-activity-filter" : "unexpected",
                    beforeRevision = receiptBeforeRevision, afterRevision = State().Revision,
                    heldIds, actualIds = Content().Activities.Select(value => value.ActivityId).ToArray()
                }));
                Check(providerDenied || hostFiltered, "Hidden creation lacked an explicit provider rejection or Host activity rejection receipt.");
                Check(providerDenied ? State().Revision == receiptBeforeRevision : State().Revision == receiptBeforeRevision + 1,
                    "Hidden creation receipt disagrees with its state admission semantics.");
                Check(Content().Activities.Select(value => value.ActivityId).SequenceEqual(heldIds), "Explicit hidden creation receipt allowed a new activity.");
            }
            visible.IsOn = true;
            await Until(() => Snapshot(adapter).ItemsByKey.ContainsKey(originalKey) && Frame(adapter)!.IsComplete, "Restoring permission failed to restore valid held activity.");
            Check(Snapshot(adapter).ItemsByKey[originalKey].Expanded && Snapshot(adapter).ItemsByKey[originalKey].Handle == originalItem.Handle,
                "Permission change reset the held item's temporary state.");
            var restoredPreferences = new LocalHostSettingsPreferenceStore(settingsPath).Load();
            Check(restoredPreferences.IsSuccess && restoredPreferences.Value!.IslandGrouping![HostSettingsController.IdentityKey(identity)] == DynamicGrouping.Separate &&
                restoredPreferences.Value.ComponentOrder.Count > 0, "Stable grouping/order did not persist.");
            var freshPresentations = new ItemPresentationController(communication.States);
            freshPresentations.UpdateScreens([originalItem.Handle.ScreenId]);
            Check(!freshPresentations.GetPresentation(originalItem.Handle.ScreenId).Single(value => value.Item.ItemId == "a1-status").Expanded,
                "A new Host presentation owner restored temporary expansion.");
            freshPresentations.Close();
            Sample("permission-restored");
            await Action("reset");
            await Until(() => !Snapshot(adapter).ItemsByKey.ContainsKey(originalKey) && Frame(adapter)!.IsComplete, "Business replacement retained removed activity items.");
            Check(!originalButton.IsEnabled && display.ItemPresentations!.Resolve(originalItem.Handle) is null, "Old item input survived replacement.");
            for (int i = 0; i < 3; i++) await Action("end");
            await Until(() => Snapshot(adapter).Instances.Count == 0 && Frame(adapter)!.IsComplete, "All ended activities retained island instances.");
            foreach (var row in settings.GetSnapshot().Components)
                Check(settings.SetVisibility(row.Identity, false).IsSuccess, "Could not hide final fixture component.");
            await Until(() => adapter.Handle == 0 && !NativeWindows.IsWindow(nativeHandle), "Organization native owner leaked on hide.");
            log("organization-cleanup-pass: removedHandleRejected=true; hiddenOwnerReleased=true; allActivityInstancesRemoved=true");

            ApplicationState State() => communication.States.GetSnapshot("counter")!.State!;
            DynamicContentState Content() => State().DynamicEntries!.Single(value => value.ComponentId == "islands").Content;
            TaskbarItemKey Key(string id) => Snapshot(adapter).ItemsByKey.Keys.Single(value => value.ItemId == id);
            Button ItemButton(TaskbarItemKey key) => Find<Button>(island.ContentRoot!, "mtp-item/" + string.Join("/", key.Component.ApplicationId,
                key.Component.FeatureGroupId, key.Component.ComponentId, key.ItemId, key.PresenceGeneration.ToString(CultureInfo.InvariantCulture)));
            void Select(DynamicGrouping value) => combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, value));
            async Task Action(string id)
            {
                var button = Find<Button>(island.ContentRoot!, "mtp-template-" + id);
                await Until(() => button.IsEnabled, "Organization action remains disabled: " + id);
                long before = State().Revision; Invoke(button);
                await Until(() => State().Revision > before && button.IsEnabled, "Organization action did not confirm: " + id);
            }
            void Sample(string phase)
            {
                samples++;
                var snapshot = Snapshot(adapter); var frame = Frame(adapter)!; var bounds = NativeWindows.WindowBounds(nativeHandle);
                Check(adapter.Handle == nativeHandle && bounds.Right == nativeRight, "Organization changed the native owner or fixed right edge.");
                Check(snapshot.Instances.SelectMany(value => value.Items).Select(value => value.Handle).Distinct().Count() == snapshot.Items.Count,
                    "Organization duplicated a stable item.");
                log("organization-frame: " + JsonSerializer.Serialize(new { phase, elapsedMs = clock.ElapsedMilliseconds,
                    State().Revision, frame.IsComplete, frame.WidthDip, instances = snapshot.Instances.Select(value => new
                    { value.InstanceId, items = value.Items.Select(item => item.Item.ItemId).ToArray() }).ToArray(),
                    native = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom } }));
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            window.Close();
            bool shutdown = host.Shutdown();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(deadline.Token)));
                foreach (var process in processes) log($"organizationOwnedPid={process.Id}; exited={process.HasExited}");
                Check(shutdown && (nativeHandle == 0 || !NativeWindows.IsWindow(nativeHandle)), "Organization cleanup failed.");
            }
            catch (Exception cleanup) when (failure is not null) { log("organization-cleanup-after-failure: " + cleanup); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        Check(samples >= 6 && clock.Elapsed < TimeSpan.FromSeconds(45), "Organization evidence incomplete or scenario exceeded 45 seconds.");
        log($"organization-pass: samples={samples}; elapsedMs={clock.ElapsedMilliseconds}; trueSdk=true; productionSettings=true; groupingAndPermission=true; stableIdentity=true; nativeAndProcessCleanup=true; evidence={root}");

        async Task Until(Func<bool> condition, string error)
        {
            var wait = Stopwatch.StartNew();
            while (!condition())
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(6) || clock.Elapsed > TimeSpan.FromSeconds(40))
                {
                    // One bounded failure snapshot; do not accumulate per-poll state.
                    var communication = Field<HostBrokerSession>(host, "communication");
                    var snapshot = adapter.GroupSnapshot;
                    var frame = Frame(adapter);
                    var currentIsland = adapter.GetType().GetField("host", Members)?.GetValue(adapter) as ContentIslandHost;
                    var actionButtons = currentIsland?.ContentRoot is { } rootElement
                        ? Descendants(rootElement).OfType<Button>().Where(button => new[] { "mtp-template-add", "mtp-template-update" }
                            .Contains(AutomationProperties.GetAutomationId(button), StringComparer.Ordinal))
                            .Select(button => new { id = AutomationProperties.GetAutomationId(button), button.IsEnabled,
                                help = AutomationProperties.GetHelpText(button) }).ToArray() : null;
                    log("organization-failure-state: " + JsonSerializer.Serialize(new
                    {
                        error, hostState = host.Session.State.ToString(), nativeHandle = adapter.Handle.ToInt64(),
                        actionButtons, lastActionError = communication.Actions.GetLastErrorHint("counter"),
                        pendingActions = communication.Actions.GetPending("counter"),
                        entries = settings.GetSnapshot().Components.Select(value => new { identity = HostSettingsController.IdentityKey(value.Identity), value.IsVisible }).ToArray(),
                        applications = communication.States.Snapshots.Select(value => new
                        {
                            value.ApplicationId, value.SessionId, value.IsConnected, value.IsInteractive, value.LastError,
                            value.State?.Revision, permissions = communication.States.GetDisplayPermissions(value.ApplicationId),
                            entries = value.State?.DynamicEntries?.Select(entry => new { entry.ComponentId, activities = entry.Content.Activities.Count, items = entry.Content.Items.Count }).ToArray()
                        }).ToArray(),
                        projection = snapshot is null ? null : new { components = snapshot.Components.Count, items = snapshot.Items.Count, instances = snapshot.Instances.Count, snapshot.Layout.WidthDip },
                        frame = frame is null ? null : new { frame.IsComplete, frame.WidthDip, items = frame.Items.Count }
                    }));
                    throw new InvalidOperationException(error + " Host errors: " + string.Join("; ", host.Errors.TakeLast(5)));
                }
                await Task.Delay(10);
            }
        }
    }

    private static HostGroupPresentationSnapshot Snapshot(IslandDisplayAdapter adapter) =>
        adapter.GroupSnapshot ?? throw new InvalidOperationException("No current organization projection.");
    private static TaskbarGroupAnimationFrame? Frame(IslandDisplayAdapter adapter) =>
        adapter.GetGroupFrame();
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement => Descendants(root).OfType<T>().Single(value => AutomationProperties.GetAutomationId(value) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Field<T>(object value, string name) where T : class =>
        value.GetType().GetField(name, Members)?.GetValue(value) as T ??
        throw new InvalidOperationException($"Expected initialized field {value.GetType().Name}.{name} of type {typeof(T).Name}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

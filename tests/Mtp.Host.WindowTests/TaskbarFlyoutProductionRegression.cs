using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Islands;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>Production Host composition and real SDK processes. The owned offscreen island does not claim Explorer acceptance.</summary>
internal static class TaskbarFlyoutProductionRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task RunAsync(Action<string> log)
    {
        var scenario = Stopwatch.StartNew();
        string evidence = Path.Combine(AppContext.BaseDirectory, "flyout-production-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var savedForeground = FlyoutNative.GetForegroundWindow();
        FlyoutNative.GetCursorPos(out var savedCursor);
        using var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")),
            new LocalComponentDisplayPreferenceStore(Path.Combine(evidence, "display.json")));
        using var target = new OwnedIslandTarget();
        var host = new HostConsoleController(display, display.Load(), new LocalTaskbarDockPreferenceStore(Path.Combine(evidence, "dock.json")),
            target.Capture, evidence, displayProvider: () => [new("owned-primary", true,
                new(-30000, -30000, 1000, 800), new(-30000, -30000, 1000, 720), NativeWindows.GetDpiForWindow(target.Parent))]);
        host.FlyoutDiagnostics = (kind, value) => log(kind + ": " + JsonSerializer.Serialize(value));
        using var settings = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(Path.Combine(evidence, "settings.json")),
            () => host.Applications, host.RetryAsync, () => host.MaterialStatus, host.ApplyAppearance, host.RequestRefresh);
        host.AttachSettings(settings);
        Check(settings.SetAppearance(new(HostTheme.Light, MaterialKind.None, 1)).IsSuccess, "Fixture appearance failed.");
        var adapter = Field<IslandDisplayAdapter>(host, "adapter");
        adapter.GetType().GetProperty("ReducedMotionOverride", Members)!.SetValue(adapter, true);
        var processes = new List<Process>();
        var windows = new HashSet<nint>();
        var receipts = new ConcurrentQueue<QueueObservation>();
        var baselineButton = new Button { Content = "MTP owned SDK focus baseline", HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch };
        var baseline = new Window { Title = "MTP SDK flyout test", Content = baselineButton };
        nint baselineHandle = WinRT.Interop.WindowNative.GetWindowHandle(baseline);
        var work = DisplayArea.GetFromWindowId(baseline.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        baseline.AppWindow.MoveAndResize(new(work.X + 40, work.Y + 40, 400, 220));
        baseline.AppWindow.Show(false);
        Exception? failure = null;
        nint islandHandle = 0, bridge = 0;
        Func<ProtocolMessage, ProtocolResult>? originalQueue = null;
        HostBrokerSession? communication = null;
        Button? opener = null;
        int checkpoints = 0;
        try
        {
            await host.StartCounterAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"),
                Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), flyouts: true).WaitAsync(TimeSpan.FromSeconds(8));
            communication = Field<HostBrokerSession>(host, "communication");
            CaptureProcesses();
            Check(processes.Count == 2, "Fixture must own a separate Broker and SDK service.");
            originalQueue = communication.QueueFlyoutPresentation ?? throw new InvalidOperationException("Production queue was not attached.");
            communication.QueueFlyoutPresentation = message =>
            {
                var result = originalQueue(message);
                if (receipts.Count < 16) receipts.Enqueue(new(message.ApplicationId, message.SessionId, message.Flyout!.RequestSequence, result.Code));
                return result;
            };
            await Until(() => settings.GetSnapshot().Components.Count(x => Counter(x.Identity)) == 2, "SDK declaration did not reach settings.");
            foreach (var row in settings.GetSnapshot().Components)
                Check(settings.SetVisibility(row.Identity, Counter(row.Identity)).IsSuccess, "Fixture visibility selection failed.");
            await Until(() => host.Session.State == IslandDisplayState.Embedded && adapter.GetGroupFrame() is { IsComplete: true, Items.Count: 1 },
                "Production template and ordinary item did not render.");
            var island = Field<ContentIslandHost>(adapter, "host");
            await Until(() => island.ContentRoot?.IsLoaded == true, "Production content island did not load.");
            await Until(() => ButtonReady(island.ContentRoot, "mtp-template-open") && ButtonReady(island.ContentRoot, "mtp-template-request"),
                "Production template buttons did not load into the native visual tree.");
            islandHandle = island.Handle; bridge = island.Bridge;
            Check(NativeWindows.GetParent(islandHandle) == target.Parent && NativeWindows.GetParent(bridge) == islandHandle,
                "Production fixture escaped its owned parent chain.");
            var controlsIdentity = settings.GetSnapshot().Components.Single(x => Counter(x.Identity) && x.Identity.LocalId.Value == "controls").Identity;
            var itemIdentity = settings.GetSnapshot().Components.Single(x => Counter(x.Identity) && x.Identity.LocalId.Value == "item").Identity;
            await ActivateBaseline();
            var originalSession = State().SessionId;
            opener = ControlButton("open");
            Invoke(opener);
            await Until(() => Coordinator()?.Manager.Inspect() is { Count: 1 }, "Native component navigation did not open production group.");
            var coordinator = Coordinator()!;
            var manager = coordinator.Manager;
            manager.ReducedMotionOverride = true;
            host.RequestRefresh();
            await Until(() => PanelReady("panel", "child"), "Component panel controls did not load.");
            var first = Group();
            var main = Main()!;
            nint componentPanel = main.Handle; windows.Add(componentPanel);
            Check(first.Entry.Entry.Kind == TemplateEntryKind.Component && first.Entry.Entry.EntryId == "controls",
                "Component panel lost its declared origin.");
            Check(FlyoutNative.GetForegroundWindow() == baselineHandle, "Pointer component navigation stole foreground.");
            Check(main.Root.RequestedTheme == ElementTheme.Light, "New flyout did not receive global Light appearance.");
            Check(settings.SetAppearance(new(HostTheme.Dark, MaterialKind.Solid, 0.25)).IsSuccess,
                "Could not change global appearance while a flyout was open.");
            await Until(() => main.Root.RequestedTheme == ElementTheme.Dark && main.Root.ActualTheme == ElementTheme.Dark,
                "Open flyout did not follow a global Dark appearance change.");
            var darkBackground = ((Grid)main.Root).Background as SolidColorBrush;
            bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            Check(darkBackground is not null && (highContrast || darkBackground.Color.A == 63),
                "Open flyout did not apply Solid opacity or high-contrast override.");
            Check(settings.SetAppearance(new(HostTheme.Light, MaterialKind.None, 1)).IsSuccess,
                "Could not restore global fixture appearance.");
            await Until(() => main.Root.ActualTheme == ElementTheme.Light && ((Grid)main.Root).Background is SolidColorBrush { Color.A: 255 },
                "Open flyout did not restore opaque Light appearance.");
            Check(Main()!.Handle == componentPanel && FlyoutNative.GetForegroundWindow() == baselineHandle,
                "Appearance change replaced the open panel or stole foreground.");
            Mark("live-global-appearance", new { sameHwnd = componentPanel.ToInt64(), highContrast,
                theme = "Light -> Dark -> Light", material = "None -> Solid(0.25) -> None" });
            Invoke(PanelButton("child"));
            await Until(() => PanelReady("child", "confirm"), "Production child navigation controls did not load.");
            Check(Main()!.Handle == componentPanel, "Layered navigation replaced primary panel HWND.");
            var childRenderer = PanelRenderer();
            var confirmed = await Field<TemplateInteractionController>(childRenderer, "controller").ActivateAsync("confirm").WaitAsync(TimeSpan.FromSeconds(8));
            Check(confirmed.Accepted && confirmed.Code == "ActionSucceeded" && State().State!.Revision == 0,
                "Production panel controller did not receive the real SDK business confirmation.");
            Invoke(PanelButton("back"));
            await Until(() => PanelReady("panel", "child"), "Production Back did not load the main panel controls.");
            Check(Main()!.Handle == componentPanel, "Back did not preserve the native owner.");
            Invoke(ControlButton("open"));
            await Until(() => manager.Inspect().Count == 0, "Repeated component entrance did not close group.");
            Check(!FlyoutNative.IsWindow(componentPanel), "Closed component panel HWND survived.");
            Mark("component-native-navigation", new { sameHwnd = componentPanel.ToInt64(), confirmed.Code, session = originalSession });

            var itemKey = adapter.GroupSnapshot!.ItemsByKey.Keys.Single();
            var itemHandle = adapter.GroupSnapshot.ItemsByKey[itemKey].Handle;
            await Until(() => ButtonReady(island.ContentRoot, ItemId(itemKey)), "Production item Button did not load.");
            var itemButton = ItemButton(itemKey);
            Check(itemButton.Focus(FocusState.Programmatic), "Could not select non-keyboard item focus state.");
            await ActivateBaseline();
            log("flyout-production-pointer-before: " + JsonSerializer.Serialize(new { itemButton.FocusState,
                baseline = baselineHandle.ToInt64(), foreground = FlyoutNative.GetForegroundWindow().ToInt64(),
                island = islandHandle.ToInt64(), bridge = bridge.ToInt64() }));
            Invoke(itemButton);
            await Until(() => manager.Inspect().Count == 1, "Production item pointer entrance failed after busy capture.");
            log("flyout-production-pointer-after: " + JsonSerializer.Serialize(new { itemButton.FocusState,
                baseline = baselineHandle.ToInt64(), foreground = FlyoutNative.GetForegroundWindow().ToInt64(),
                windows = Group().Windows.Select(x => x.Handle.ToInt64()).ToArray() }));
            Check(Group().Entry.Entry.Kind == TemplateEntryKind.TaskbarFlyout && Group().Entry.Entry.EntryId == "details" &&
                FlyoutNative.GetForegroundWindow() == baselineHandle, "Item pointer origin or no-activation contract failed.");
            CaptureWindows();
            Check(manager.CloseScreen(Group().ScreenId, Group().Generation).IsSuccess, "Pointer group cleanup failed.");
            Check(itemButton.Focus(FocusState.Keyboard) && itemButton.FocusState == FocusState.Keyboard,
                "Could not enter real item keyboard focus state.");
            Invoke(itemButton);
            await Until(() => manager.Inspect().Count == 1 && FlyoutNative.BelongsTo(FlyoutNative.GetForegroundWindow(), Group().Windows.Select(x => x.Handle)),
                "Item keyboard trigger lost its focus origin while becoming busy.");
            CaptureWindows();
            Check(manager.CloseScreen(Group().ScreenId, Group().Generation).IsSuccess, "Keyboard group cleanup failed.");
            Mark("item-pointer-keyboard", new { itemHandle, busyTriggerCaptured = true, policy = "TaskbarFlyout" });

            // Provider's request action awaits RequestFlyoutAsync and returns the SDK's queue receipt to Host.
            await ActivateBaseline();
            Invoke(ControlButton("request"));
            await Until(() => receipts.Any(x => x.Code == "Queued") && manager.Inspect().Count == 1 &&
                communication.States.FlyoutRequests.GetLastResult("counter")?.Result.Code == "Displayed" && ControlButton("request").IsEnabled,
                "Native request control did not reach SDK queue, actual presentation and final receipt.");
            Check(communication.Actions.GetLastErrorHint("counter") is null, "Native SDK request action returned a business error.");
            CaptureWindows();
            Check(manager.CloseScreen(Group().ScreenId, Group().Generation).IsSuccess, "SDK group cleanup failed.");
            var sdkReceipt = await communication.SendActionAsync(new("counter", "main", ActionEntryKind.Component, "controls", "request"),
                new(), expectedSessionId: originalSession).WaitAsync(TimeSpan.FromSeconds(8));
            Check(sdkReceipt.Accepted && sdkReceipt.Code == "Queued", "Actual SDK acknowledgement was not Queued.");
            await Until(() => manager.Inspect().Count == 1 && communication.States.FlyoutRequests.GetLastResult("counter")?.Result.Code == "Displayed",
                "SDK acknowledged request did not finish as actual display.");
            CaptureWindows();
            var last = communication.States.FlyoutRequests.GetLastResult("counter")!;
            Check(receipts.Any(x => x.SessionId == originalSession && x.Sequence == last.RequestSequence && x.Code == "Queued"),
                "Queued and displayed receipts lost session/sequence correlation.");
            var displayed = Group();
            Mark("sdk-queued-to-displayed", new { sdkReceipt.Code, final = last, queue = receipts.ToArray(), actual = new
                { displayed.Entry, displayed.SessionId, displayed.Generation, displayed.Mode,
                    windows = displayed.Windows.Select(x => new { handle = x.Handle.ToInt64(), x.TemplateId, x.Bounds }) } });

            // SDK anchor binds one current component. Keeping the other component visible must not move the open group to it.
            var source = adapter.GetGroupFrame()!.Components.First(x => x.Key.ApplicationId == "counter" && x.Key.FeatureGroupId == "main").Key;
            var sourceIdentity = source.ComponentId == "controls" ? controlsIdentity : itemIdentity;
            Check(settings.SetVisibility(sourceIdentity, false).IsSuccess, "Could not hide the bound SDK source.");
            await Until(() => manager.Inspect().Count == 0, "Hidden SDK anchor was silently replaced with another component.");
            Check(adapter.IsAlive, "Hiding one source removed the unrelated visible component.");
            Check(settings.SetVisibility(sourceIdentity, true).IsSuccess, "Could not restore SDK source.");
            await Until(() => adapter.GetGroupFrame() is { IsComplete: true, Components.Count: 2 }, "Restored components did not settle.");
            Mark("source-hidden", new { bound = source, remainingNativeOwner = adapter.Handle.ToInt64() });

            var entryPolicy = State().Declaration!.FlyoutEntries.Single(x => x.Identity.LocalId.Value == "details");
            Check(communication.States.FlyoutRequests.SetEntryEnabled(entryPolicy, false).Accepted, "Could not disable flyout policy.");
            int queuedBefore = receipts.Count;
            var disabled = await communication.SendActionAsync(new("counter", "main", ActionEntryKind.Component, "controls", "request"),
                new(), expectedSessionId: originalSession).WaitAsync(TimeSpan.FromSeconds(8));
            Check(!disabled.Accepted && disabled.Code == "EntryDisabled" && receipts.Count == queuedBefore && manager.Inspect().Count == 0,
                "Disabled SDK entry was queued or displayed.");
            Check(communication.States.FlyoutRequests.SetEntryEnabled(entryPolicy, true).Accepted, "Could not restore flyout policy.");
            var allowed = await communication.SendActionAsync(new("counter", "main", ActionEntryKind.Component, "controls", "request"),
                new(), expectedSessionId: originalSession).WaitAsync(TimeSpan.FromSeconds(8));
            Check(allowed.Code == "Queued", "Re-enabled SDK entry remained rejected.");
            await Until(() => manager.Inspect().Count == 1, "Re-enabled entry did not display.");
            CaptureWindows();
            Check(communication.States.FlyoutRequests.SetEntryEnabled(entryPolicy, false).Accepted, "Open-entry policy disable failed.");
            host.RequestRefresh();
            await Until(() => manager.Inspect().Count == 0, "Already-open disabled SDK entry remained interactive.");
            Check(communication.States.FlyoutRequests.SetEntryEnabled(entryPolicy, true).Accepted, "Could not restore policy for reconnect test.");
            Mark("sdk-policy", new { rejected = disabled.Code, admitted = allowed.Code, openEntryClosed = true });

            // Phase 08 uses the same real SDK process, owned Host and committed settings as phase 07.
            var hintManager = coordinator.Hints;
            hintManager.ReducedMotionOverride = true;
            await Until(() => ButtonReady(island.ContentRoot, "mtp-template-hint") &&
                settings.GetSnapshot().HintEntries.Any(x => Counter(x.Identity) && x.Identity.LocalId.Value == "notice"),
                "SDK hint action or registered notice entry did not reach the production Host.");
            var noticeIdentity = settings.GetSnapshot().HintEntries.Single(x => Counter(x.Identity) && x.Identity.LocalId.Value == "notice").Identity;
            Check(settings.SetHints(new(FlyoutPosition.LowerCenter, false)).IsSuccess, "Could not select user hint-position priority.");
            var hintReceipt = await RequestHint("Displayed");
            var hint = Notice();
            nint noticeHandle = hint.Handle; windows.Add(noticeHandle);
            await Until(() => AtPosition(Notice(), FlyoutPosition.LowerCenter), "User LowerCenter preference did not override the app's TopLeft request.");
            Check(hint.Request.Position == FlyoutPosition.TopLeft && hint.Request.Owner is null,
                "SDK notice lost its application position or became associated without an owner.");
            Check(FlyoutNative.GetForegroundWindow() == baselineHandle, "Ordinary SDK hint stole foreground.");
            await RequestHint("Refreshed");
            Check(Notice().Handle == noticeHandle && hintManager.Inspect().Count == 1,
                "Repeated SDK notice created a second window instead of refreshing the existing instance.");
            Mark("hint-sdk-reuse", new { hintReceipt, handle = noticeHandle.ToInt64(), actual = FlyoutNative.Bounds(noticeHandle),
                declared = hint.Request.Position.ToString(), selected = "LowerCenter", sameHwnd = true });

            var positions = new[] { FlyoutPosition.TopLeft, FlyoutPosition.TopCenter, FlyoutPosition.TopRight,
                FlyoutPosition.BottomLeft, FlyoutPosition.BottomCenter, FlyoutPosition.BottomRight, FlyoutPosition.Center, FlyoutPosition.LowerCenter };
            var positionEvidence = new List<object>(8);
            foreach (var position in positions)
            {
                Check(settings.SetHints(new(position, false)).IsSuccess, "Could not commit a finite hint position.");
                await Until(() => hintManager.Inspect().Any(x => x.Handle == noticeHandle) && AtPosition(Notice(), position),
                    "Existing hint did not move to its committed user position: " + position);
                positionEvidence.Add(new { position = position.ToString(), handle = Notice().Handle.ToInt64(), native = FlyoutNative.Bounds(noticeHandle) });
            }
            Check(settings.SetHints(new(FlyoutPosition.BottomRight, true)).IsSuccess, "Could not allow the registered application preference.");
            await Until(() => AtPosition(Notice(), FlyoutPosition.TopLeft), "Allowed app TopLeft preference did not override user BottomRight.");
            Mark("hint-position-settings", new { positions = positionEvidence, allowedApplication = "TopLeft", sameHwnd = noticeHandle.ToInt64() });

            Check(settings.SetHintVisibility(noticeIdentity, false).IsSuccess, "Could not disable the full stable notice identity.");
            await Until(() => hintManager.Inspect().Count == 0 && !FlyoutNative.IsWindow(noticeHandle) &&
                !communication.States.FlyoutRequests.IsEntryEnabled("counter", originalSession, "main", "notice"),
                "Disabling notice did not hide its native window and synchronize request policy.");
            int hintQueueBefore = receipts.Count;
            var hiddenHint = await communication.SendActionAsync(new("counter", "main", ActionEntryKind.Component, "controls", "hint"),
                new(), expectedSessionId: originalSession).WaitAsync(TimeSpan.FromSeconds(8));
            Check(!hiddenHint.Accepted && hiddenHint.Code == "EntryDisabled" && receipts.Count == hintQueueBefore && hintManager.Inspect().Count == 0,
                "A hidden hint entry was queued or displayed by its next SDK request.");
            Check(settings.SetHintVisibility(noticeIdentity, true).IsSuccess, "Could not re-enable the registered notice.");
            Check(settings.SetHints(new(FlyoutPosition.LowerCenter, false)).IsSuccess, "Could not restore default hint settings.");
            await Until(() => communication.States.FlyoutRequests.IsEntryEnabled("counter", originalSession, "main", "notice"),
                "Re-enabled notice policy did not reach the request router.");
            await RequestHint("Displayed");
            nint restoredNotice = Notice().Handle; windows.Add(restoredNotice);
            Check(restoredNotice != 0, "Re-enabled SDK hint did not create a native window.");
            Check(settings.SetHintVisibility(noticeIdentity, false).IsSuccess, "Could not close the re-enabled notice fixture.");
            await Until(() => hintManager.Inspect().Count == 0 && !FlyoutNative.IsWindow(restoredNotice), "Re-enabled notice cleanup failed.");
            Check(settings.SetHintVisibility(noticeIdentity, true).IsSuccess, "Could not preserve notice preference for subsequent session recovery.");
            Mark("hint-entry-policy", new { identity = HostSettingsController.IdentityKey(noticeIdentity), rejected = hiddenHint.Code,
                queueUnchanged = true, reenabled = true, oldWindowDestroyed = true });

            var errorGroupRequest = await communication.SendActionAsync(new("counter", "main", ActionEntryKind.Component, "controls", "request"),
                new(), expectedSessionId: originalSession).WaitAsync(TimeSpan.FromSeconds(8));
            Check(errorGroupRequest.Accepted && errorGroupRequest.Code == "Queued", "Could not request the associated-error group through SDK.");
            await Until(() => manager.Inspect().Count == 1 && PanelReady("panel", "fail"), "Associated-error business control did not load.");
            CaptureWindows();
            var errorGroup = Group();
            var errorController = Field<TemplateInteractionController>(PanelRenderer(), "controller");
            var failedAction = await errorController.ActivateAsync("fail").WaitAsync(TimeSpan.FromSeconds(8));
            Check(!failedAction.Accepted && failedAction.Code == "DemoFailure", "SDK failure action did not return its controlled reason.");
            await Until(() => hintManager.Inspect().Count == 1 && hintManager.Inspect()[0].Request.Owner?.Generation == errorGroup.Generation,
                "Production failed panel action did not create its unique associated error hint.");
            var associated = hintManager.Inspect().Single();
            nint associatedHandle = associated.Handle; windows.Add(associatedHandle);
            var errorAge = Stopwatch.StartNew();
            Check(associated.Request.Owner?.Entry == errorGroup.Entry && associated.Text == failedAction.Message &&
                manager.Inspect().Single().Generation == errorGroup.Generation,
                "Associated error lost its message/owner or closed the taskbar group.");
            CheckAssociatedBounds(associated);
            Check(host.CurrentError is { } readable && readable.Contains(failedAction.Message, StringComparison.Ordinal),
                "Failed action reason is not readable through Host.CurrentError.");
            await Task.Delay(2_100);
            Check(hintManager.Inspect().Single().Handle == associatedHandle, "Associated hint ended before its initial three-second lifetime.");
            var failedAgain = await errorController.ActivateAsync("fail").WaitAsync(TimeSpan.FromSeconds(8));
            Check(!failedAgain.Accepted && failedAgain.Code == "DemoFailure" && hintManager.Inspect().Single().Handle == associatedHandle,
                "A second panel failure did not reuse the same associated native hint.");
            await Task.Delay(1_200);
            Check(errorAge.Elapsed >= TimeSpan.FromSeconds(3) && hintManager.Inspect().Single().Handle == associatedHandle &&
                manager.Inspect().Single().Generation == errorGroup.Generation,
                "Repeated failure failed to restart hint life or disturbed its owner group.");
            CheckAssociatedBounds(hintManager.Inspect().Single());
            Check(manager.CloseScreen(errorGroup.ScreenId, errorGroup.Generation).IsSuccess, "Could not close the associated-error owner.");
            host.RequestRefresh();
            await Until(() => hintManager.Inspect().Count == 0 && !FlyoutNative.IsWindow(associatedHandle),
                "Closing the owner left a live associated hint or converted it into an independent hint.");
            Mark("hint-associated-failure", new { failedAction.Code, failedAgain = failedAgain.Code, handle = associatedHandle.ToInt64(),
                ownerGeneration = errorGroup.Generation, elapsedPastFirstLifetimeMs = errorAge.ElapsedMilliseconds,
                sameHwnd = true, fullWidth = true, ownerCleanup = true, reason = host.CurrentError });

            itemKey = adapter.GroupSnapshot!.ItemsByKey.Keys.Single();
            itemHandle = adapter.GroupSnapshot.ItemsByKey[itemKey].Handle;
            await Until(() => ButtonReady(island.ContentRoot, ItemId(itemKey)), "Restored item Button did not load.");
            itemButton = ItemButton(itemKey);
            log("flyout-production-before-reconnect-open: " + JsonSerializer.Serialize(new
            {
                baseline = baselineHandle.ToInt64(), island = islandHandle.ToInt64(), bridge = bridge.ToInt64(),
                foreground = FlyoutNative.GetForegroundWindow().ToInt64(), focus = FlyoutNative.CurrentFocus().ToInt64(),
                itemButton.FocusState
            }));
            Invoke(itemButton);
            await Until(() => manager.Inspect().Count == 1 && PanelReady("panel", "confirm"), "Old-session panel controls did not load.");
            CaptureWindows();
            var oldPanel = Main()!;
            var oldController = Field<TemplateInteractionController>(PanelRenderer(), "controller");
            var oldGroup = Group();
            var broker = processes.Single(x => x.Id == communication.BrokerProcessId);
            broker.Kill(); // Only this fixture's own Broker. Its owned SDK service remains alive for rebind.
            await broker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => State() is { IsInteractive: true, IsConnected: true } now && now.SessionId != originalSession &&
                manager.Inspect().Count == 0 && adapter.GroupSnapshot?.ItemsByKey.Count == 1,
                "Finite Broker replacement did not establish a new SDK session and close old group.");
            CaptureProcesses();
            Check(!(await oldController.ActivateAsync("confirm").WaitAsync(TimeSpan.FromSeconds(2))).Accepted,
                "Disposed old panel controller dispatched into replacement session.");
            Check(display.ItemPresentations!.Resolve(itemHandle) is null && !itemButton.IsEnabled,
                "Old item occurrence remained valid after session replacement.");
            var staleAction = await communication.SendActionAsync(new("counter", "main", ActionEntryKind.TaskbarFlyout, "details", "confirm"),
                new(), expectedSessionId: originalSession).WaitAsync(TimeSpan.FromSeconds(2));
            Check(!staleAction.Accepted && staleAction.Code == "StaleSession", "Old-session Host action reached replacement service.");
            var replacementKey = adapter.GroupSnapshot!.ItemsByKey.Keys.Single();
            await Until(() => ButtonReady(island.ContentRoot, ItemId(replacementKey)), "Replacement-session item Button did not load.");
            var capturedReplacement = adapter.GroupSnapshot!.ItemsByKey[replacementKey];
            var replacementButton = ItemButton(replacementKey);
            var capturedButton = new { replacementButton.IsEnabled, replacementButton.IsLoaded, replacementButton.IsHitTestVisible,
                id = AutomationProperties.GetAutomationId(replacementButton), replacementButton.FocusState };
            Invoke(replacementButton);
            try { await Until(() => manager.Inspect().Count == 1, "Replacement-session native item did not open."); }
            catch
            {
                var currentState = State();
                var resolved = display.ItemPresentations!.Resolve(capturedReplacement.Handle);
                log("flyout-production-replacement-failure: " + JsonSerializer.Serialize(new
                {
                    replacementKey, captured = ItemState(capturedReplacement), capturedButton,
                    currentProjection = adapter.GroupSnapshot?.ItemsByKey.Select(x => new { key = x.Key, item = ItemState(x.Value) }),
                    resolved = ItemState(resolved), currentState.SessionId, currentState.IsConnected, currentState.IsInteractive,
                    currentPolicyEnabled = communication.States.FlyoutRequests.IsEntryEnabled("counter", currentState.SessionId, "main", "details"),
                    projectedPolicyEnabled = communication.States.FlyoutRequests.IsEntryEnabled("counter", capturedReplacement.SessionId, "main", "details"),
                    actualButton = new { replacementButton.IsEnabled, replacementButton.IsLoaded, replacementButton.IsHitTestVisible,
                        helpText = AutomationProperties.GetHelpText(replacementButton),
                        presentInCurrentTree = island.ContentRoot is not null && Descendants(island.ContentRoot).Any(x => ReferenceEquals(x, replacementButton)),
                        sameXamlRoot = ReferenceEquals(replacementButton.XamlRoot, island.ContentRoot?.XamlRoot),
                        ancestors = Ancestors(replacementButton) },
                    errors = host.Errors.TakeLast(5).ToArray()
                }));
                throw;
            }
            CaptureWindows();
            Check(!manager.CloseScreen(Group().ScreenId, oldGroup.Generation).IsSuccess && manager.Inspect().Count == 1,
                "Old-generation close destroyed replacement group.");
            Mark("session-replacement", new { previous = originalSession, current = State().SessionId,
                brokerPid = communication.BrokerProcessId, servicePids = communication.ServiceProcessIds,
                staleAction.Code, previousHandleRejected = true, recovery = communication.BrokerRecovery });
            Check(manager.CloseScreen(Group().ScreenId, Group().Generation).IsSuccess, "Replacement group cleanup failed.");

            BrokerApplicationSnapshot State() => communication.States.GetSnapshot("counter") ?? throw new InvalidOperationException("SDK snapshot unavailable.");
            TaskbarFlyoutCoordinator? Coordinator() => (TaskbarFlyoutCoordinator?)host.GetType().GetField("flyouts", Members)!.GetValue(host);
            TaskbarFlyoutObservation Group() => manager.Inspect().Single();
            TaskbarFlyoutWindow? Main() => manager.WindowForTesting("owned-primary");
            bool PanelReady(string templateId, string nodeId) => Main() is { } panel && panel.TemplateId == templateId &&
                Descendants(panel.Root).OfType<TemplateRenderer>().Count(x => x.IsLoaded && x.IsEnabled) == 1 &&
                ButtonReady(panel.Root, "mtp-template-" + nodeId);
            TemplateRenderer PanelRenderer() => Descendants(Main()!.Root).OfType<TemplateRenderer>().Single(x => x.IsLoaded && x.IsEnabled);
            Button ControlButton(string id) => Find<Button>(island.ContentRoot!, "mtp-template-" + id);
            Button PanelButton(string id) => Find<Button>(Main()!.Root, "mtp-template-" + id);
            Button ItemButton(TaskbarItemKey key) => Find<Button>(island.ContentRoot!, ItemId(key));
            void CaptureWindows() { foreach (var group in manager.Inspect()) foreach (var panel in group.Windows) windows.Add(panel.Handle); }
            ShortHintObservation Notice() => hintManager.Inspect().Single(x => x.Request.Entry.Entry.Kind == TemplateEntryKind.Hint &&
                x.Request.Entry.Entry.EntryId == "notice" && x.Request.Owner is null);
            async Task<FlyoutRequestReceipt> RequestHint(string expectedPresentation)
            {
                long previous = communication.States.FlyoutRequests.GetLastResult("counter")?.RequestSequence ?? 0;
                var queued = await communication.SendActionAsync(new("counter", "main", ActionEntryKind.Component, "controls", "hint"),
                    new(), expectedSessionId: originalSession).WaitAsync(TimeSpan.FromSeconds(8));
                Check(queued.Accepted && queued.Code == "Queued", "SDK hint acknowledgement did not report Queued.");
                await Until(() => communication.States.FlyoutRequests.GetLastResult("counter") is { } receipt &&
                    receipt.RequestSequence > previous && receipt.Result.Code == expectedPresentation &&
                    hintManager.Inspect().Any(x => x.Request.Entry.Entry.EntryId == "notice" && x.Request.Owner is null),
                    "SDK hint request did not reach its final " + expectedPresentation + " receipt.");
                var final = communication.States.FlyoutRequests.GetLastResult("counter")!;
                Check(receipts.Any(x => x.SessionId == originalSession && x.Sequence == final.RequestSequence && x.Code == "Queued"),
                    "SDK hint queued/presented receipts lost sequence correlation.");
                return final;
            }
            bool AtPosition(ShortHintObservation value, FlyoutPosition position)
            {
                if (!FlyoutNative.IsWindow(value.Handle)) return false;
                var bounds = FlyoutNative.Bounds(value.Handle);
                var area = value.Request.WorkArea;
                double margin = 16 * value.Request.Dpi / 96d;
                double x = position switch
                {
                    FlyoutPosition.TopLeft or FlyoutPosition.BottomLeft => area.X + margin,
                    FlyoutPosition.TopRight or FlyoutPosition.BottomRight => area.Right - margin - bounds.Width,
                    _ => area.X + (area.Width - bounds.Width) / 2d
                };
                double y = position switch
                {
                    FlyoutPosition.TopLeft or FlyoutPosition.TopCenter or FlyoutPosition.TopRight => area.Y + margin,
                    FlyoutPosition.BottomLeft or FlyoutPosition.BottomCenter or FlyoutPosition.BottomRight => area.Bottom - margin - bounds.Height,
                    FlyoutPosition.Center => area.Y + (area.Height - bounds.Height) / 2d,
                    _ => area.Y + area.Height * 0.8 - bounds.Height / 2d
                };
                y = Math.Clamp(y, area.Y + margin, area.Bottom - margin - bounds.Height);
                return Math.Abs(bounds.X - x) <= 2 && Math.Abs(bounds.Y - y) <= 2 && value.Bounds == bounds;
            }
            void CheckAssociatedBounds(ShortHintObservation value)
            {
                var panels = Group().Windows.Where(panel => !panel.Closing && FlyoutNative.IsWindow(panel.Handle))
                    .Select(panel => new { handle = panel.Handle.ToInt64(), panel.TemplateId, bounds = FlyoutNative.Bounds(panel.Handle) }).ToArray();
                Check(panels.Length > 0, "Associated error owner has no live native panels.");
                int left = panels.Min(panel => panel.bounds.X), top = panels.Min(panel => panel.bounds.Y);
                long right = panels.Max(panel => panel.bounds.Right), bottom = panels.Max(panel => panel.bounds.Bottom);
                var union = new PixelRect(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
                var bounds = FlyoutNative.Bounds(value.Handle);
                double gap = 8 * value.Request.Dpi / 96d;
                log("flyout-production-associated-bounds: " + JsonSerializer.Serialize(new { panels, union,
                    hint = new { handle = value.Handle.ToInt64(), bounds, mode = value.Mode.ToString() }, value.Request.Dpi, gap }));
                Check(Math.Abs(bounds.X - union.X) <= 2 && Math.Abs(bounds.Width - union.Width) <= 2 &&
                    (value.Mode == HintPlacementMode.Above && Math.Abs(bounds.Bottom + gap - union.Y) <= 2 ||
                     value.Mode == HintPlacementMode.Below && Math.Abs(union.Bottom + gap - bounds.Y) <= 2),
                    "Associated error hint did not cover its actual owner width with the declared gap.");
            }
        }
        catch (Exception error)
        {
            failure = error;
            log("flyout-production-opener-failure: " + JsonSerializer.Serialize(new { present = opener is not null,
                enabled = opener?.IsEnabled, helpText = opener is null ? null : AutomationProperties.GetHelpText(opener) }));
            throw;
        }
        finally
        {
            if (communication is not null && originalQueue is not null) communication.QueueFlyoutPresentation = originalQueue;
            if (communication is not null) CaptureProcesses();
            bool closed = host.Shutdown();
            baseline.Close();
            SetCursorPos(savedCursor.X, savedCursor.Y);
            if (savedForeground != 0 && FlyoutNative.IsWindow(savedForeground)) SetForegroundWindow(savedForeground);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(45 - scenario.Elapsed.TotalSeconds, 0.01, 5)));
            try
            {
                await Task.WhenAll(processes.Select(x => x.WaitForExitAsync(deadline.Token)));
                foreach (var process in processes) log($"flyoutProductionOwnedPid={process.Id}; exited={process.HasExited}");
                Check(closed && !FlyoutNative.IsWindow(islandHandle) && !FlyoutNative.IsWindow(bridge) && windows.All(x => !FlyoutNative.IsWindow(x)),
                    "Production cleanup retained native ownership.");
            }
            catch (Exception cleanup) when (failure is not null) { log("flyout-production-cleanup-after-failure: " + cleanup); }
            finally
            {
                File.WriteAllText(Path.Combine(evidence, "sdk-queue-receipts.json"), JsonSerializer.Serialize(receipts.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
                foreach (var process in processes) process.Dispose();
            }
        }
        Check(checkpoints >= 10 && scenario.Elapsed < TimeSpan.FromSeconds(45), "Production scenario lacked checkpoints or exceeded 45 seconds.");
        log($"PASS: production flyout SDK/Host/WinUI; checkpoints={checkpoints}; elapsedMs={scenario.ElapsedMilliseconds}; owned cleanup verified; evidence={evidence}");

        async Task ActivateBaseline()
        {
            await Until(() => baselineButton.IsLoaded, "Owned baseline did not load.");
            var bounds = FlyoutNative.Bounds(baselineHandle);
            FlyoutNative.Position(baselineHandle, bounds);
            var buttonBounds = TaskbarFlyoutNativeRegression.ElementScreenBounds(baselineButton, baseline);
            var point = new FlyoutNative.Point { X = buttonBounds.X + buttonBounds.Width / 2, Y = buttonBounds.Y + buttonBounds.Height / 2 };
            nint targetRoot = FlyoutNative.Root(FlyoutNative.WindowFromPoint(point));
            try
            {
                Check(targetRoot == baselineHandle, "Refusing to click an occluded owned baseline Button.");
                Check(SetCursorPos(point.X, point.Y), "Could not position pointer over the baseline Button.");
                Input[] click = [new() { Flags = 2 }, new() { Flags = 4 }];
                Check(SendInput(2, click, Marshal.SizeOf<Input>()) == 2, "Owned baseline click failed.");
                await Until(() => FlyoutNative.GetForegroundWindow() == baselineHandle, "Owned baseline did not become foreground.");
            }
            catch
            {
                log("flyout-production-baseline-failure: " + JsonSerializer.Serialize(new { buttonBounds,
                    point = new { point.X, point.Y }, expectedRoot = baselineHandle.ToInt64(), targetRoot = targetRoot.ToInt64(),
                    currentTargetRoot = FlyoutNative.Root(FlyoutNative.WindowFromPoint(point)).ToInt64(),
                    actualForeground = FlyoutNative.GetForegroundWindow().ToInt64(), baselineButton.IsEnabled, baselineButton.IsHitTestVisible }));
                throw;
            }
        }
        async Task Until(Func<bool> condition, string error)
        {
            var wait = Stopwatch.StartNew();
            while (!condition())
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(8) || scenario.Elapsed > TimeSpan.FromSeconds(40))
                    throw new InvalidOperationException(error + " Host errors: " + string.Join("; ", host.Errors.TakeLast(5)));
                await Task.Delay(15);
            }
        }
        void CaptureProcesses()
        {
            foreach (int pid in communication!.ServiceProcessIds.Prepend(communication.BrokerProcessId))
            {
                if (processes.Any(x => x.Id == pid)) continue;
                var process = Process.GetProcessById(pid); _ = process.Handle; processes.Add(process);
            }
        }
        void Mark(string phase, object value)
        {
            Check(checkpoints++ < 16, "Production evidence exceeded its checkpoint budget.");
            log("flyout-production: " + JsonSerializer.Serialize(new { phase, elapsedMs = scenario.ElapsedMilliseconds, value }));
        }
    }

    private sealed record QueueObservation(string ApplicationId, string SessionId, long Sequence, string Code);
    private static bool Counter(StableIdentity value) => value.Segments.Count == 3 && value.Segments[0].Value == "counter";
    private static object? ItemState(HostItemPresentation? item) => item is null ? null : new
        { item.Handle, item.SessionId, item.PresenceGeneration, item.IsInteractive };
    private static object[] Ancestors(DependencyObject value)
    {
        var result = new List<object>(16);
        for (DependencyObject? current = value; current is not null && result.Count < 16; current = VisualTreeHelper.GetParent(current))
            result.Add(new { type = current.GetType().Name, id = AutomationProperties.GetAutomationId(current),
                loaded = (current as FrameworkElement)?.IsLoaded, hitTestVisible = (current as UIElement)?.IsHitTestVisible });
        return result.ToArray();
    }
    private static string ItemId(TaskbarItemKey key) => "mtp-item/" + string.Join("/", Uri.EscapeDataString(key.Component.ApplicationId),
        Uri.EscapeDataString(key.Component.FeatureGroupId), Uri.EscapeDataString(key.Component.ComponentId), Uri.EscapeDataString(key.ItemId),
        key.PresenceGeneration.ToString(CultureInfo.InvariantCulture));
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static bool ButtonReady(DependencyObject? root, string id) => root is not null && Descendants(root).OfType<Button>()
        .Count(x => x.IsLoaded && x.IsEnabled && AutomationProperties.GetAutomationId(x) == id) == 1;
    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(x => AutomationProperties.GetAutomationId(x) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(value, i))) yield return child;
    }
    private static T Field<T>(object value, string name) where T : class => (T)value.GetType().GetField(name, Members)!.GetValue(value)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int X;
        [FieldOffset(12)] public int Y;
        [FieldOffset(16)] public uint MouseData;
        [FieldOffset(20)] public uint Flags;
        [FieldOffset(24)] public uint Time;
        [FieldOffset(32)] public nuint Extra;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;
using Windows.Graphics.Imaging;

namespace Mtp.Host.WindowTests;

/// <summary>Serial interactive desktop regression; does not assert human acceptance.</summary>
internal static class TaskbarFlyoutNativeRegression
{
    /// <summary>One 230ms delay after Apply crosses the animation deadline without depending on machine speed.</summary>
    internal static async Task RunAnimationSchedulingRegressionAsync(Action<string> log)
    {
        using var baseline = new SchedulingFocusBaseline();
        await baseline.PrepareAsync(log);
        var entry = new TemplateEntryReference("group", TemplateEntryKind.TaskbarFlyout, "flyout");
        var secondEntry = entry with { EntryId = "other" };
        var store = new BrokerStateStore(["flyout-test"]);
        var template = CreateTemplate();
        var declaration = new ApplicationDeclaration("flyout-test", [new("group", [new("component", [new("primary")])],
            [new("flyout", [new("commit")], Template: template), new("other", [new("commit")], Template: template)])]);
        Connect(store, declaration, entry, secondEntry, "scheduling");
        using var images = new RegisteredImageCache();
        using var manager = new TaskbarFlyoutManager(store, (key, id, navigate) => new TemplateRenderer(
            new TemplateInteractionController(store, key.ApplicationId, key.Entry,
                (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), id, navigate), images),
            (kind, detail) => log(kind + ": " + System.Text.Json.JsonSerializer.Serialize(detail)));
        manager.ReducedMotionOverride = false;
        var work = DisplayArea.Primary.WorkArea;
        var request = new FlyoutOpenRequest(new("flyout-test", entry), "scheduling", "owned-scheduling",
            new(work.X + work.Width - 100, work.Y + work.Height - 8, 48, 8), new(work.X, work.Y, work.Width, work.Height), 96);
        int delays = 0;
        void CrossDeadline() { delays++; Thread.Sleep(230); }
        manager.AfterNextFrameAppliedForTesting = CrossDeadline;
        Check(manager.Toggle(request).IsSuccess, "Scheduling fixture failed to open.");
        var window = manager.WindowForTesting(request.ScreenId)!;
        nint handle = window.Handle;
        try
        {
            Check(delays == 1 && window.Root.Opacity < 1, "Fixture did not hold an applied intermediate opening frame.");
            await Until(() => window.Root.Opacity == 1 && FlyoutNative.ReadOpacity(handle) == 255,
                "A deadline crossed during Apply left the opening window frozen before its terminal frame.");
            log("PASS: scheduling open applied terminal frame after one bounded 230ms delay.");
            manager.AfterNextFrameAppliedForTesting = CrossDeadline;
            Check(manager.Toggle(request).Value == FlyoutToggleResult.Closed, "Scheduling fixture failed to begin closing.");
            Check(delays == 2, "Closing did not exercise its bounded deadline crossing.");
            await Until(() => manager.Inspect().Count == 0 && !FlyoutNative.IsWindow(handle) && !manager.HasResources,
                "A deadline crossed during Apply left the closing group or its resources alive.");
            log("PASS: scheduling close applied terminal frame and released resources after one bounded 230ms delay.");

            int diagnosticAttempts = 0;
            using var isolated = new TaskbarFlyoutManager(store, (key, id, navigate) => new TemplateRenderer(
                new TemplateInteractionController(store, key.ApplicationId, key.Entry,
                    (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), id, navigate), images),
                (_, _) => { Interlocked.Increment(ref diagnosticAttempts); throw new IOException("Injected diagnostic sink failure."); });
            isolated.ReducedMotionOverride = true;
            isolated.ApplyAppearance(new(HostTheme.Light, MaterialKind.Solid, 0.25));
            Check(isolated.Toggle(request).IsSuccess, "A throwing diagnostic sink prevented native presentation.");
            var isolatedWindow = isolated.WindowForTesting(request.ScreenId)!;
            nint isolatedHandle = isolatedWindow.Handle;
            bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            var background = ((Grid)isolatedWindow.Root).Background as SolidColorBrush;
            Check(background is not null && background.Color.A == (highContrast ? 255 : 63),
                "A diagnostic sink failure changed the selected Solid material into opaque fallback.");
            Check(isolated.Toggle(request).Value == FlyoutToggleResult.Closed && !isolated.HasResources &&
                !FlyoutNative.IsWindow(isolatedHandle) && diagnosticAttempts >= 2,
                "A diagnostic sink failure interrupted owned window or observer cleanup.");
            log($"PASS: always-throwing diagnostic sink isolated; attempts={diagnosticAttempts}; selected Solid opacity retained; resources=0.");

            manager.ReducedMotionOverride = true;
            Check(manager.Toggle(request).IsSuccess, "Native-close retry fixture failed to open.");
            var retryWindow = manager.WindowForTesting(request.ScreenId)!;
            nint retryHandle = retryWindow.Handle;
            long retryGeneration = manager.Inspect().Single().Generation;
            Window? nativeWindow = null;
            int closeAttempts = 0;
            retryWindow.CloseWindowForTesting = value =>
            {
                nativeWindow = value;
                if (++closeAttempts == 1) throw new COMException("Injected native Window.Close failure.");
                value.Close();
            };
            try
            {
                var failedClose = manager.CloseScreen(request.ScreenId, retryGeneration);
                Check(!failedClose.IsSuccess && failedClose.Error?.Code == "FlyoutCleanupPending" &&
                    closeAttempts == 1 && FlyoutNative.IsWindow(retryHandle) && manager.HasResources &&
                    manager.Inspect().Single().Closing,
                    "A native Close exception did not retain the live HWND and group ownership.");
                var retriedClose = manager.CloseScreen(request.ScreenId, retryGeneration);
                Check(retriedClose.IsSuccess && closeAttempts == 2 && !FlyoutNative.IsWindow(retryHandle) &&
                    manager.Inspect().Count == 0 && !manager.HasResources,
                    "Explicit retry after native Close threw did not invoke Close again and release ownership.");
                log("PASS: native Window.Close exception retained ownership; one explicit retry invoked Close again; resources=0.");
            }
            finally
            {
                retryWindow.CloseWindowForTesting = null;
                if (FlyoutNative.IsWindow(retryHandle)) nativeWindow?.Close();
                manager.CloseScreen(request.ScreenId, retryGeneration);
            }

            Check(manager.Toggle(request with { InitialTemplateId = "details", Invocation = FlyoutInvocationKind.Keyboard }).IsSuccess,
                "Retiring keyboard-input fixture failed to open.");
            var retiringWindow = manager.WindowForTesting(request.ScreenId)!;
            await Until(() => Find<Button>(retiringWindow.Root, "commit") is { IsLoaded: true },
                "Retiring business control did not load.");
            var retiringButton = Find<Button>(retiringWindow.Root, "commit")!;
            retiringWindow.EnterKeyboard("commit");
            await Until(() => retiringButton.FocusState == FocusState.Keyboard,
                "Retiring business control did not receive keyboard focus.");
            manager.ReducedMotionOverride = false;
            Check(manager.Toggle(request).Value == FlyoutToggleResult.Closed && manager.Inspect().Single().Closing,
                "Keyboard-input fixture did not enter its exit animation.");
            Check(!retiringButton.IsEnabled,
                "An exiting group kept its focused business control enabled for keyboard or automation activation.");
            await Until(() => manager.Inspect().Count == 0 && !manager.HasResources,
                "Retiring keyboard-input fixture did not finish cleanup.");
            log("PASS: exit animation immediately disables focused business controls before native destruction; resources=0.");

            var currentAction = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            TemplateInteractionController? pendingController = null;
            int actionSends = 0;
            using var pendingManager = new TaskbarFlyoutManager(store, (key, id, navigate) =>
            {
                var controller = new TemplateInteractionController(store, key.ApplicationId, key.Entry,
                    (_, _, _, _) => { Interlocked.Increment(ref actionSends); return currentAction.Task; }, id, navigate);
                if (id == "details") pendingController = controller;
                return new TemplateRenderer(controller, images);
            }, (_, _) => { });
            pendingManager.ReducedMotionOverride = true;
            Check(pendingManager.Toggle(request with { InitialTemplateId = "details" }).IsSuccess && pendingController is not null,
                "Pending-action exit fixture failed to open.");
            var runningAction = pendingController!.ActivateAsync("commit");
            var pendingAction = pendingController.ActivateAsync("commit");
            Check(actionSends == 1 && !runningAction.IsCompleted && !pendingAction.IsCompleted,
                "Fixture did not create one current action and one unsent target.");
            try
            {
                pendingManager.ReducedMotionOverride = false;
                Check(pendingManager.Toggle(request).Value == FlyoutToggleResult.Closed,
                    "Pending-action fixture did not begin closing.");
                var lateAction = pendingController.ActivateAsync("commit");
                Check(pendingAction.IsCompleted && !pendingAction.Result.Accepted &&
                    lateAction.IsCompleted && !lateAction.Result.Accepted && actionSends == 1,
                    "Exit animation did not cancel the unsent target and reject new business activation before native destruction.");
                currentAction.TrySetResult(ProtocolResult.Success());
                await runningAction.WaitAsync(TimeSpan.FromSeconds(2));
                await Until(() => pendingManager.Inspect().Count == 0 && !pendingManager.HasResources,
                    "Pending-action fixture did not finish owned cleanup.");
                Check(actionSends == 1, "The completion of the retired view sent its queued business target.");
                log("PASS: beginning exit rejects new business activation and cancels the unsent target; late completion sends no action; resources=0.");
            }
            finally
            {
                currentAction.TrySetResult(ProtocolResult.Success());
                pendingManager.TryClose();
            }
        }
        catch
        {
            log("flyout-scheduling-failure: " + System.Text.Json.JsonSerializer.Serialize(new { delays,
                rootOpacity = window.Root.Opacity, nativeAlive = FlyoutNative.IsWindow(handle),
                nativeAlpha = FlyoutNative.IsWindow(handle) ? FlyoutNative.ReadOpacity(handle) : (byte?)null,
                remainingGroups = manager.Inspect().Count, manager.HasResources }));
            throw;
        }
    }

    /// <summary>Creation/cleanup only; no foreground, mouse or keyboard prerequisites.</summary>
    internal static Task RunCreateDiagnosisAsync(Action<string> log)
    {
        var entry = new TemplateEntryReference("group", TemplateEntryKind.TaskbarFlyout, "flyout");
        var secondEntry = entry with { EntryId = "other" };
        var store = new BrokerStateStore(["flyout-test"]);
        var template = CreateTemplate();
        var declaration = new ApplicationDeclaration("flyout-test", [new("group", [new("component", [new("primary")])],
            [new("flyout", [new("commit")], Template: template), new("other", [new("commit")], Template: template)])]);
        Connect(store, declaration, entry, secondEntry, "create-diagnosis");
        using var images = new RegisteredImageCache();
        using var manager = new TaskbarFlyoutManager(store, (key, id, navigate) => new TemplateRenderer(
            new TemplateInteractionController(store, key.ApplicationId, key.Entry,
                (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), id, navigate), images),
            (kind, detail) => log(kind + ": " + System.Text.Json.JsonSerializer.Serialize(detail)));
        manager.ReducedMotionOverride = true;
        var work = DisplayArea.Primary.WorkArea;
        var request = new FlyoutOpenRequest(new("flyout-test", entry), "create-diagnosis", "owned-create-diagnosis",
            new(work.X + work.Width - 100, work.Y + work.Height - 8, 48, 8), new(work.X, work.Y, work.Width, work.Height), 96);
        var opened = manager.Toggle(request);
        log("flyout-create-diagnosis-result: " + System.Text.Json.JsonSerializer.Serialize(new
            { opened.IsSuccess, opened.Error, count = manager.Inspect().Count }));
        var handles = manager.Inspect().SelectMany(x => x.Windows).Select(x => x.Handle).ToArray();
        var closed = manager.TryClose();
        Check(closed.IsSuccess && !manager.HasResources && handles.All(x => !FlyoutNative.IsWindow(x)),
            "Creation diagnosis failed to release owned resources.");
        Check(opened.IsSuccess, "Creation diagnosis failed; inspect flyout-create-failed type/HResult/stack.");
        log("PASS: flyout creation and cleanup without foreground/input assertions.");
        return Task.CompletedTask;
    }

    internal static async Task RunAsync(Action<string> log)
    {
        var priorForeground = FlyoutNative.GetForegroundWindow();
        FlyoutNative.GetCursorPos(out var priorCursor);
        var outside = new Window { Title = "MTP owned outside-click test" };
        int originalClicks = 0, pointerPressed = 0, pointerReleased = 0;
        var outsideButton = new Button { Content = "Owned original input target", HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch };
        outsideButton.Click += (_, _) => originalClicks++;
        outsideButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => pointerPressed++), true);
        outsideButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => pointerReleased++), true);
        outside.Content = outsideButton;
        var outsideHandle = WinRT.Interop.WindowNative.GetWindowHandle(outside);
        var work = DisplayArea.GetFromWindowId(outside.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        outside.AppWindow.MoveAndResize(new(work.X + 40, work.Y + 40, 420, 260));
        outside.Activate();
        var entry = new TemplateEntryReference("group", TemplateEntryKind.TaskbarFlyout, "flyout");
        var secondEntry = entry with { EntryId = "other" };
        var store = new BrokerStateStore(["flyout-test"]);
        var template = CreateTemplate();
        var declaration = new ApplicationDeclaration("flyout-test", [new("group", [new("component", [new("primary")])],
            [new("flyout", [new("commit")], Template: template), new("other", [new("commit")], Template: template)])]);
        Connect(store, declaration, entry, secondEntry, "first");
        using var images = new RegisteredImageCache();
        var controllers = new Dictionary<string, TemplateInteractionController>(StringComparer.Ordinal);
        int businessCalls = 0;
        using var manager = new TaskbarFlyoutManager(store, (key, id, navigate) =>
        {
            var controller = new TemplateInteractionController(store, key.ApplicationId, key.Entry,
                (_, _, _, _) => { businessCalls++; return Task.FromResult(ProtocolResult.Success()); }, id, navigate);
            controllers[id] = controller;
            return new TemplateRenderer(controller, images);
        }, (kind, detail) => log(kind + ": " + System.Text.Json.JsonSerializer.Serialize(detail)));
        var request = new FlyoutOpenRequest(new("flyout-test", entry), "first", "owned-screen",
            new(work.X + work.Width - 100, work.Y + work.Height - 8, 48, 8), new(work.X, work.Y, work.Width, work.Height), GetDpiForWindow(outsideHandle));
        var handles = new HashSet<nint>();
        string evidence = Path.Combine(AppContext.BaseDirectory, "flyout-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var frames = new List<AnimationSample>(64);
        var frameClock = Stopwatch.StartNew();
        try
        {
            await Until(() => outsideButton.IsLoaded, "Owned outside target did not load.");
            // Foreground-lock policy can reject Activate/SetForegroundWindow. Use an actual click only on our own window.
            var outsideBounds = FlyoutNative.Bounds(outsideHandle);
            FlyoutNative.Position(outsideHandle, outsideBounds);
            var baselinePoint = new FlyoutNative.Point
                { X = outsideBounds.X + outsideBounds.Width / 2, Y = outsideBounds.Y + outsideBounds.Height / 2 };
            Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(baselinePoint)) == outsideHandle,
                "Owned baseline activation target is occluded; refusing to click another window.");
            SetCursorPos(baselinePoint.X, baselinePoint.Y);
            var baselineClick = new[] { MouseInput(0x0002), MouseInput(0x0004) };
            Check(SendInput((uint)baselineClick.Length, baselineClick, Marshal.SizeOf<Input>()) == baselineClick.Length,
                "Owned baseline activation click failed.");
            await Until(() => FlyoutNative.GetForegroundWindow() == outsideHandle, "Owned baseline could not become foreground.");
            log($"flyout-baseline-ready: foregroundOwned=true; activationClickCount={originalClicks}; buttonWidth={outsideButton.ActualWidth}; buttonHeight={outsideButton.ActualHeight}");
            originalClicks = pointerPressed = pointerReleased = 0;
            Check(manager.Toggle(request).IsSuccess, "Automatic group did not open.");
            var main = manager.WindowForTesting(request.ScreenId)!;
            await SampleFrames(main, "open", 14, frames, frameClock);
            await Until(() => manager.WindowForTesting(request.ScreenId) is { Root.IsLoaded: true } w && w.Root.Opacity == 1,
                "Automatic group animation did not finish.");
            Check(frames.Any(x => x.Phase == "open" && x.NativeAlpha is > 0 and < 255 && x.ContentOpacity is > 0 and < 1),
                "Opening has no actual intermediate HWND/content opacity evidence.");
            Check(FlyoutNative.ReadOpacity(main.Handle) == 255, "Stable native window is not opaque.");
            await ConsoleScreenshot.SaveAsync(main.Root, Path.Combine(evidence, "main-stable.png"));
            await SaveNativeAsync(main.Handle, Path.Combine(evidence, "main-native.png"));
            var initialHandle = main.Handle; handles.Add(initialHandle);
            Check(FlyoutNative.GetForegroundWindow() == outsideHandle, "Automatic group stole activation.");
            Check(manager.Inspect().Count == 1, "Existing external focus incorrectly closed the group.");
            var mainBounds = FlyoutNative.Bounds(main.Handle);
            Invoke(Find<Button>(main.Root, "open")!);
            await SampleFrames(main, "navigate", 12, frames, frameClock);
            await Until(() => main.TemplateId == "details" && Find<Button>(main.Root, "back") is { IsLoaded: true }, "Associated panel did not appear.");
            await Task.Delay(260);
            var detailBounds = FlyoutNative.Bounds(main.Handle);
            Check(frames.Any(x => x.Phase == "navigate" && x.Bounds.Height != mainBounds.Height && x.Bounds.Height != detailBounds.Height &&
                x.ContentOpacity is > 0 and < 1), "Hierarchical content/rectangle transition has no intermediate native frame.");
            Check(FlyoutNative.ReadOpacity(main.Handle) == 255 && main.Root.Opacity == 1, "Detail screenshot was not stable opaque content.");
            await ConsoleScreenshot.SaveAsync(main.Root, Path.Combine(evidence, "details-stable.png"));
            await SaveNativeAsync(main.Handle, Path.Combine(evidence, "details-native.png"));
            Check(main.Handle == initialHandle && manager.Inspect().Single().Windows.Count == 1,
                "Hierarchical navigation replaced the normal panel window.");
            int narrowWidth = (int)(260 * request.Dpi / 96d);
            var narrowArea = new PixelRect(work.X + work.Width - narrowWidth, work.Y, narrowWidth, work.Height);
            Check(manager.UpdateEnvironment(request.ScreenId, request.Anchor, narrowArea, request.Dpi).IsSuccess, "Resize animation start failed.");
            await SampleFrames(main, "shrink", 7, frames, frameClock);
            var beforeInterrupt = Capture(main, "before-retarget", frames, frameClock);
            Check(beforeInterrupt.Bounds.Width < detailBounds.Width, "Resize was not in progress before interruption.");
            Check(manager.UpdateEnvironment(request.ScreenId, request.Anchor, request.WorkArea, request.Dpi).IsSuccess, "Interrupted resize failed.");
            var afterInterrupt = Capture(main, "after-retarget", frames, frameClock);
            Check(Math.Abs(beforeInterrupt.Bounds.Width - afterInterrupt.Bounds.Width) <= Math.Ceiling(16 * request.Dpi / 96d) &&
                afterInterrupt.Bounds.Width < detailBounds.Width, "Retarget jumped to the new endpoint instead of continuing the current native frame.");
            await SampleFrames(main, "restore", 7, frames, frameClock);
            await Task.Delay(260);
            Check(FlyoutNative.Bounds(main.Handle) == detailBounds, "Interrupted resize did not return to its target bounds.");
            Invoke(Find<Button>(main.Root, "commit")!);
            await Until(() => businessCalls == 1, "Panel business request did not dispatch exactly once.");
            Invoke(Find<Button>(main.Root, "back")!);
            await Until(() => main.TemplateId == "main", "Back did not return to main.");
            await Task.Delay(260);
            Check(Find<Button>(main.Root, "open") is not null, "Main controls lost after returning.");
            log("PASS: normal WinUI panel no-activation; existing outside focus baseline retained; associated navigation reuses HWND; business control and Back reachable.");

            // A real click delivered to the already-foreground window must close the group without losing the original click.
            Check(FlyoutNative.GetForegroundWindow() == outsideHandle, "Baseline changed before unchanged-foreground click test.");
            var buttonBounds = ElementScreenBounds(outsideButton, outside);
            var outsidePoint = new FlyoutNative.Point { X = buttonBounds.X + buttonBounds.Width / 2, Y = buttonBounds.Y + buttonBounds.Height / 2 };
            var hitBefore = FlyoutNative.Root(FlyoutNative.WindowFromPoint(outsidePoint));
            var foregroundBefore = FlyoutNative.GetForegroundWindow();
            // Activation's routed Click can arrive after the foreground event. Compare this
            // deliberate test click with a settled baseline, not a reset before activation drains.
            int clicksBefore = originalClicks, pressedBefore = pointerPressed, releasedBefore = pointerReleased;
            try
            {
                Check(hitBefore == outsideHandle, "Outside Button center is occluded; refusing to click another window.");
                Check(SetCursorPos(outsidePoint.X, outsidePoint.Y), "Could not position pointer over actual outside Button.");
                var clicks = new[] { MouseInput(0x0002), MouseInput(0x0004) };
                Check(SendInput((uint)clicks.Length, clicks, Marshal.SizeOf<Input>()) == clicks.Length, "SendInput failed.");
                await SampleFrames(main, "close", 12, frames, frameClock);
                await Until(() => originalClicks - clicksBefore == 1 && manager.Inspect().Count == 0, "Outside click either did not close or was swallowed.");
                Check(pointerPressed - pressedBefore == 1 && pointerReleased - releasedBefore == 1,
                    "The original outside click was duplicated or lost at its input target.");
            }
            catch
            {
                log("flyout-outside-click-failure: " + System.Text.Json.JsonSerializer.Serialize(new { buttonBounds,
                    point = new { outsidePoint.X, outsidePoint.Y }, expectedRoot = outsideHandle.ToInt64(), targetRoot = hitBefore.ToInt64(),
                    currentTargetRoot = FlyoutNative.Root(FlyoutNative.WindowFromPoint(outsidePoint)).ToInt64(),
                    foregroundBefore = foregroundBefore.ToInt64(), foregroundAfter = FlyoutNative.GetForegroundWindow().ToInt64(),
                    pointerPressed, pointerReleased, originalClicks, clicksBefore, pressedBefore, releasedBefore, enabled = outsideButton.IsEnabled,
                    hitTestVisible = outsideButton.IsHitTestVisible, remainingGroups = manager.Inspect().Count }));
                throw;
            }
            Check(frames.Any(x => x.Phase == "close" && x.NativeAlpha is > 0 and < 255 && x.RootOpacity is > 0 and < 1),
                "Closing has no intermediate whole-window native alpha evidence.");
            Check(!FlyoutNative.IsWindow(initialHandle), "Closed group HWND survived.");
            Check(FlyoutNative.GetForegroundWindow() == outsideHandle, "Outside-click fixture changed foreground unexpectedly.");
            Check(!manager.HasResources, "Last-group observer or window resources survived close.");
            log("PASS: Raw Input observes outside click with unchanged foreground; original WinUI Click delivered once; HWND and subscriptions released.");

            manager.ReducedMotionOverride = true;
            Check(manager.Toggle(request with { Invocation = FlyoutInvocationKind.Keyboard }).IsSuccess, "Keyboard group failed.");
            main = manager.WindowForTesting(request.ScreenId)!; handles.Add(main.Handle);
            await Until(() => FlyoutNative.BelongsTo(FlyoutNative.GetForegroundWindow(), [main.Handle]), "Keyboard entry did not activate owned group.");
            await Until(() => Find<Button>(main.Root, "open") is { IsLoaded: true }, "Keyboard content did not load.");
            var observation = manager.Inspect().Single();
            Check(manager.EnterKeyboard(request.ScreenId, observation.Generation).IsSuccess, "Explicit keyboard scope entry failed.");
            Check(main.Root.TabFocusNavigation == KeyboardNavigationMode.Cycle, "Tab scope is not cyclic.");
            Check(FocusManager.TryMoveFocus(FocusNavigationDirection.Next, new() { SearchRoot = main.Root }), "Tab could not move inside group.");
            Check(FocusManager.TryMoveFocus(FocusNavigationDirection.Previous, new() { SearchRoot = main.Root }), "Shift+Tab could not move inside group.");
            Invoke(Find<Button>(main.Root, "open")!);
            Check(main.TemplateId == "details" && main.Root.Opacity == 1, "Reduced-motion navigation did not jump to completed presentation.");
            Check(FlyoutNative.ReadOpacity(main.Handle) == 255, "Reduced-motion native alpha did not complete immediately.");
            Invoke(Find<Button>(main.Root, "back")!);
            Check(main.TemplateId == "main", "Keyboard return failed.");
            Check(ReferenceEquals(FocusManager.GetFocusedElement(main.Root.XamlRoot), Find<Button>(main.Root, "open")), "Back did not restore source-node focus.");
            Check(!manager.CloseScreen(request.ScreenId, observation.Generation - 1).IsSuccess, "Old-generation close was accepted.");
            Check(manager.Inspect().Count == 1, "Old-generation close destroyed new group.");
            Check(manager.Toggle(request).Value == FlyoutToggleResult.Closed, "Repeated entry did not close group.");
            Check(manager.Inspect().Count == 0, "Reduced-motion close was not immediate.");
            Check(manager.Toggle(request with { Invocation = FlyoutInvocationKind.Keyboard }).IsSuccess, "Escape fixture failed to open.");
            main = manager.WindowForTesting(request.ScreenId)!; handles.Add(main.Handle);
            await Until(() => main.Root.IsLoaded && FlyoutNative.BelongsTo(FlyoutNative.GetForegroundWindow(), [main.Handle]), "Escape fixture did not gain keyboard focus.");
            var escape = new[] { KeyInput(0x1B, false), KeyInput(0x1B, true) };
            var menuButton = Descendants(main.Root).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "打开关联面板");
            var menu = (MenuFlyout)menuButton.Flyout;
            bool menuOpen = false;
            menu.Opened += (_, _) => menuOpen = true;
            menu.Closed += (_, _) => menuOpen = false;
            menu.ShowAt(menuButton);
            await Until(() => menuOpen && menu.Items.OfType<MenuFlyoutItem>().Any(x => x.IsLoaded), "Owned native menu did not open.");
            Check(manager.Inspect().Count == 1, "Entering the owned menu closed its group.");
            Check(SendInput((uint)escape.Length, escape, Marshal.SizeOf<Input>()) == escape.Length, "Menu Escape input failed.");
            await Until(() => !menuOpen, "First Escape did not close the native menu.");
            Check(manager.Inspect().Count == 1, "Menu-consumed Escape also closed its group.");
            log("PASS: native menu participates in group scope and consumes first Escape without closing the group.");
            Check(SendInput((uint)escape.Length, escape, Marshal.SizeOf<Input>()) == escape.Length, "Escape input failed.");
            await Until(() => manager.Inspect().Count == 0, "Escape did not close group.");
            log("PASS: keyboard entry, cyclic next/previous focus, source focus after Back, reduced motion, stale close rejection and repeated-entry close.");

            outside.Activate();
            await Until(() => FlyoutNative.GetForegroundWindow() == outsideHandle, "Outside baseline did not restore.");
            Check(manager.Toggle(request).IsSuccess, "Failure fixture failed to open.");
            main = manager.WindowForTesting(request.ScreenId)!; handles.Add(main.Handle);
            main.SimulateCloseFailure = true;
            var replace = manager.Toggle(request with { Entry = new("flyout-test", secondEntry) });
            Check(!replace.IsSuccess && replace.Error?.Code == "FlyoutCleanupPending" && manager.Inspect().Single().Entry.Entry == entry,
                "Replacement bypassed or concealed failed close.");
            Check(manager.Inspect().Single().Closing && manager.HasResources, "Failed close lost resource ownership.");
            await Task.Delay(300);
            Check(manager.Inspect().Count == 1, "Failure caused an uncontrolled replacement.");
            main.SimulateCloseFailure = false;
            // Explicit retry closes the retained old group; no unbounded automatic retry loop.
            Check(manager.CloseScreen(request.ScreenId, manager.Inspect().Single().Generation).IsSuccess, "Failed-close explicit retry failed.");
            Check(manager.Toggle(request with { Entry = new("flyout-test", secondEntry) }).IsSuccess, "Replacement after cleanup failed.");
            var staleController = controllers["main"];
            Connect(store, declaration, entry, secondEntry, "second");
            manager.Refresh();
            Check(manager.Inspect().Count == 0, "Old session retained group.");
            Check(!(await staleController.ActivateAsync("open")).Accepted, "Old renderer emitted a navigation after disconnect.");
            var parallelTemplate = template with { MainTemplateId = "primary",
                Templates = [new("primary", template.Templates[0].Root with { Action = new(TemplateActionKind.OpenPanel, "main") }),
                    new("main", template.Templates[1].Root)], Panels = [new("main", PanelPresentation.Parallel)] };
            var parallelDeclaration = new ApplicationDeclaration("flyout-test", [new("group", [new("component", [new("primary")])],
                [new("flyout", [new("commit")], Template: parallelTemplate), new("other", [new("commit")], Template: parallelTemplate)])]);
            Connect(store, parallelDeclaration, entry, secondEntry, "parallel");
            var parallelRequest = request with { SessionId = "parallel" };
            Check(manager.Toggle(parallelRequest).IsSuccess, "Parallel fixture failed to open.");
            Check(manager.Inspect().Single().Windows.Count == 2 && manager.Inspect().Single().Mode == FlyoutGroupMode.Parallel,
                "Related template named main collided with the Host primary slot.");
            foreach (var panel in manager.Inspect().Single().Windows) handles.Add(panel.Handle);
            Check(manager.UpdateEnvironment(request.ScreenId, request.Anchor,
                request.WorkArea with { Width = (int)(350 * request.Dpi / 96d) }, request.Dpi).IsSuccess, "Narrow reflow failed.");
            await Task.Delay(50);
            Check(manager.Inspect().Count == 1 && manager.Inspect().Single().Windows.Count == 1,
                "Retiring a parallel window incorrectly closed the entire group.");
            Check(manager.UpdateEnvironment(request.ScreenId, request.Anchor, request.WorkArea, request.Dpi).IsSuccess, "Wide reflow failed.");
            Check(manager.Inspect().Single().Windows.Count == 2, "Parallel presentation did not recover.");
            foreach (var panel in manager.Inspect().Single().Windows) handles.Add(panel.Handle);
            Check(manager.CloseEntry(request.Entry, "parallel").IsSuccess, "Parallel cleanup failed.");
            log("PASS: parallel windows, reserved-name collision resistance, narrow-space hierarchy, retired-window ownership and wide-space recovery.");
            using (var failedCreation = new TaskbarFlyoutManager(store, (_, _, _) => throw new InvalidOperationException("Injected renderer creation failure"), (_, _) => { }))
            {
                var failed = failedCreation.Toggle(parallelRequest);
                Check(!failed.IsSuccess && failed.Error?.Code == "FlyoutCreateFailed" && !failedCreation.HasResources,
                    "Renderer creation failure leaked native subscriptions or group resources.");
            }
            Check(manager.Toggle(parallelRequest).IsSuccess, "Policy invalidation fixture failed to open.");
            foreach (var panel in manager.Inspect().Single().Windows) handles.Add(panel.Handle);
            var policyEntry = store.GetSnapshot("flyout-test")!.Declaration!.FlyoutEntries.Single(x => x.Identity.LocalId.Value == "flyout");
            Check(store.FlyoutRequests.SetEntryEnabled(policyEntry, false).Accepted, "Policy disable failed.");
            manager.Refresh();
            Check(manager.Inspect().Count == 0 && manager.Toggle(parallelRequest).Error?.Code == "EntryDisabled",
                "Manager bypassed disabled-entry policy or retained an already-open disabled entry.");
            log("PASS: manager directly enforces TaskbarFlyout Host policy and closes an opened entry after disable.");
            Check(manager.TryClose().IsSuccess && !manager.HasResources, "Final resources did not clear.");
            Check(handles.All(h => !FlyoutNative.IsWindow(h)), "Owned window remained alive.");
            log($"PASS: {frames.Count} bounded flyout animation frames; native rectangle/alpha and XAML opacity readback; in-place interruption continuity; hit rectangles; screenshots={evidence}");
            log("PASS: injected close failure retains owner and blocks replacement; explicit bounded retry; session replacement invalidates old view; final native cleanup.");
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "animation-frames.json"), System.Text.Json.JsonSerializer.Serialize(frames,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            foreach (var group in manager.Inspect())
                if (manager.WindowForTesting(group.ScreenId) is { } retained) retained.SimulateCloseFailure = false;
            var closed = manager.TryClose();
            outside.Close();
            SetCursorPos(priorCursor.X, priorCursor.Y);
            if (priorForeground != 0 && FlyoutNative.IsWindow(priorForeground)) SetForegroundWindow(priorForeground);
            Check(closed.IsSuccess, "Regression cleanup failed: " + closed.Error?.Message);
        }
    }

    private static EntryTemplateDeclaration CreateTemplate() => new("main",
        [new("main", new("open", TemplateNodeKind.Button, Value: new(new(TemplateValueKind.Text, Text: "Open details")),
            AccessibleName: "Open details", Action: new(TemplateActionKind.OpenPanel, "details"))),
         new("details", new("details-root", TemplateNodeKind.Vertical, Children:
            [new("commit", TemplateNodeKind.Button, Value: new(new(TemplateValueKind.Text, Text: "Commit")), AccessibleName: "Commit", Action: new(TemplateActionKind.Business, "commit")),
             new("back", TemplateNodeKind.Button, Value: new(new(TemplateValueKind.Text, Text: "Back")), AccessibleName: "Back", Action: new(TemplateActionKind.Back))]))], [],
        Panels: [new("details")]);

    private static void Connect(BrokerStateStore store, ApplicationDeclaration declaration, TemplateEntryReference entry,
        TemplateEntryReference second, string session)
    {
        Check(store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "flyout-test", SessionId = session }).Result!.Accepted, "Welcome rejected.");
        var result = store.Handle(new() { Kind = MessageKind.Declare, ApplicationId = "flyout-test", SessionId = session,
            Declaration = declaration, State = new(0, [], TemplateEntries: [new(entry, []), new(second, [])]) }).Result!;
        Check(result.Accepted, "Declare rejected: " + result.Message);
    }
    private static T? Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().FirstOrDefault(x => AutomationProperties.GetAutomationId(x) == "mtp-template-" + id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(value, i))) yield return child;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private sealed record AnimationSample(string Phase, double Milliseconds, PixelRect Bounds, byte NativeAlpha,
        double RootOpacity, double ContentOpacity, double ContentWidth, double ContentHeight, bool? CenterHitsWindow);
    private static async Task SampleFrames(TaskbarFlyoutWindow window, string phase, int count,
        List<AnimationSample> frames, Stopwatch clock)
    {
        for (int i = 0; i < count && FlyoutNative.IsWindow(window.Handle); i++)
        {
            Capture(window, phase, frames, clock);
            await Task.Delay(12);
        }
    }
    private static AnimationSample Capture(TaskbarFlyoutWindow window, string phase, List<AnimationSample> frames, Stopwatch clock)
    {
        Check(frames.Count < 64, "Frame evidence exceeded its 64-sample budget.");
        var bounds = FlyoutNative.Bounds(window.Handle);
        Check(bounds == window.LastBounds, "Native rectangle did not match the applied shared animation frame.");
        byte alpha = FlyoutNative.ReadOpacity(window.Handle);
        var incoming = Descendants(window.Root).OfType<ScrollViewer>().FirstOrDefault(x => x.IsEnabled && x.IsHitTestVisible);
        bool? hit = alpha > 32 ? FlyoutNative.Root(FlyoutNative.WindowFromPoint(new()
            { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 })) == window.Handle : null;
        if (hit is false) throw new InvalidOperationException("Visible animation frame's native hit rectangle did not move with its window.");
        var sample = new AnimationSample(phase, clock.Elapsed.TotalMilliseconds, bounds, alpha,
            window.Root.Opacity, incoming?.Opacity ?? 0, window.Root.Width, window.Root.Height, hit);
        frames.Add(sample);
        return sample;
    }
    private static async Task Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException(message); await Task.Delay(20); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task SaveNativeAsync(nint window, string path)
    {
        // Crop only the test-owned visible window; unlike RenderTargetBitmap, includes its real compositor/backdrop.
        var bounds = FlyoutNative.Bounds(window);
        Check((long)bounds.Width * bounds.Height <= 4 * 1024 * 1024, "Native screenshot pixel budget exceeded.");
        nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
        byte[] pixels;
        try
        {
            Check(screen != 0, "Screenshot screen DC unavailable.");
            memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            Check(memory != 0 && bitmap != 0, "Screenshot GDI allocation failed.");
            previous = SelectObject(memory, bitmap);
            Check(BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40CC0020), "Native window capture failed.");
            SelectObject(memory, previous); previous = 0;
            var info = new BitmapInfo { Size = 40, Width = bounds.Width, Height = -bounds.Height, Planes = 1, Bits = 32 };
            pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
            Check(GetDIBits(screen, bitmap, 0, (uint)bounds.Height, pixels, ref info, 0) == bounds.Height, "Native screenshot readback failed.");
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
        using var stream = new FileStream(path, FileMode.Create);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bounds.Width, (uint)bounds.Height, 96, 96, pixels);
        await encoder.FlushAsync();
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize;
        public int XPixels, YPixels; public uint ColorsUsed, ColorsImportant, Color;
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
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
    private static Input MouseInput(uint flags) => new() { Type = 0, Flags = flags };
    private sealed class SchedulingFocusBaseline : IDisposable
    {
        private readonly nint previousForeground = FlyoutNative.GetForegroundWindow();
        private readonly FlyoutNative.Point previousCursor;
        private readonly Window window;
        private readonly Button button;
        private readonly nint handle;
        internal SchedulingFocusBaseline()
        {
            FlyoutNative.GetCursorPos(out previousCursor);
            button = new Button { Content = "MTP scheduling focus baseline", HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch };
            window = new Window { Title = "MTP scheduling regression", Content = button };
            handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var work = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            window.AppWindow.MoveAndResize(new(work.X + 40, work.Y + 40, 420, 260));
            window.AppWindow.Show(false);
        }
        internal async Task PrepareAsync(Action<string> log)
        {
            await Until(() => button.IsLoaded && button.ActualWidth > 0 && button.ActualHeight > 0,
                "Scheduling baseline Button did not load.");
            FlyoutNative.Position(handle, FlyoutNative.Bounds(handle));
            var bounds = ElementScreenBounds(button, window);
            var point = new FlyoutNative.Point { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
            Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(point)) == handle,
                "Scheduling baseline is occluded; refusing to click another window.");
            Check(SetCursorPos(point.X, point.Y), "Scheduling baseline pointer placement failed.");
            var click = new[] { MouseInput(0x0002), MouseInput(0x0004) };
            Check(SendInput((uint)click.Length, click, Marshal.SizeOf<Input>()) == click.Length, "Scheduling baseline click failed.");
            await Until(() => FlyoutNative.GetForegroundWindow() == handle, "Scheduling baseline did not become foreground.");
            Check(button.Focus(FocusState.Programmatic), "Scheduling baseline Button could not receive focus.");
            await Until(() => FlyoutNative.Root(FlyoutNative.CurrentFocus()) == handle && FlyoutNative.GetForegroundWindow() == handle,
                "Scheduling baseline focus did not settle in the owned window.");
            log("flyout-scheduling-baseline-ready: " + System.Text.Json.JsonSerializer.Serialize(new { bounds,
                foreground = handle.ToInt64(), focus = FlyoutNative.CurrentFocus().ToInt64() }));
        }
        public void Dispose()
        {
            window.Close();
            SetCursorPos(previousCursor.X, previousCursor.Y);
            if (previousForeground != 0 && FlyoutNative.IsWindow(previousForeground)) SetForegroundWindow(previousForeground);
        }
    }
    internal static PixelRect ElementScreenBounds(FrameworkElement element, Window owner)
    {
        var bounds = element.TransformToVisual((UIElement)owner.Content).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        var origin = new FlyoutNative.Point();
        Check(ClientToScreen(hwnd, ref origin), "Could not resolve owned Button client origin.");
        double scale = GetDpiForWindow(hwnd) / 96d;
        int left = checked(origin.X + (int)Math.Floor(bounds.X * scale));
        int top = checked(origin.Y + (int)Math.Floor(bounds.Y * scale));
        int right = checked(origin.X + (int)Math.Ceiling(bounds.Right * scale));
        int bottom = checked(origin.Y + (int)Math.Ceiling(bounds.Bottom * scale));
        return new(left, top, checked(right - left), checked(bottom - top));
    }
    private static Input KeyInput(ushort key, bool up) => new() { Type = 1, X = key, Y = up ? 2 : 0 };
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref FlyoutNative.Point point);
}

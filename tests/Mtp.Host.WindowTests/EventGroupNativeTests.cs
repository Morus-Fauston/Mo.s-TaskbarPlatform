using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>Owned native windows, actual focus/menu controls, deterministic monotonic timing.</summary>
internal static class EventGroupNativeTests
{
    private const string App = "event-native";
    internal static async Task RunAsync(Action<string> log)
    {
        var clock = new ManualClock();
        var states = Ready();
        using var images = new RegisteredImageCache();
        using var manager = new EventGroupManager(states, images, (_, _, _, _) => Task.FromResult(ProtocolResult.Success()),
            (kind, value) => log(kind + ": " + System.Text.Json.JsonSerializer.Serialize(value)), clock) { ReducedMotionOverride = true };
        var area = DisplayArea.Primary.WorkArea;
        var work = new PixelRect(area.X, area.Y, area.Width, area.Height);
        TaskbarDockDisplay? Display(string screen) => new(screen, true, work, work, 96);
        EventGroupOpenRequest Request(int id, string screen = "events") => new(new(App, new("main", TemplateEntryKind.EventChannel, "event" + id)),
            "session", screen, work, 96);
        var outside = new Window { Title = "MTP event test owned focus target", Content = new Button { Content = "Owned focus target" } };
        nint previousForeground = FlyoutNative.GetForegroundWindow();
        FlyoutNative.GetCursorPos(out var previousCursor);
        nint outsideHandle = WinRT.Interop.WindowNative.GetWindowHandle(outside);
        var handles = new HashSet<nint>();
        void Remember() { foreach (var item in manager.Inspect().SelectMany(value => value.Windows)) handles.Add(item.Handle); }
        void Clear()
        {
            foreach (var item in manager.Inspect()) Check(manager.CloseEntry(item.Entry, item.Identity.SessionId).IsSuccess, "Explicit event cleanup failed.");
            Check(!manager.HasResources, "Event owners remained after explicit cleanup.");
        }
        void Settings(int limit) => manager.ApplySettings(new(new(), [], Events: new(MaximumGroupsPerScreen: limit)));
        try
        {
            outside.AppWindow.MoveAndResize(new((int)work.Right - 180, work.Y + 24, 150, 90));
            outside.Activate(); ((Button)outside.Content).Focus(FocusState.Keyboard);
            Check(SetForegroundWindow(outsideHandle), "Owned event focus target could not request foreground.");
            SetCursorPos((int)work.Right - 120, work.Y + 60);
            await Pump();
            Check(FlyoutNative.GetForegroundWindow() == outsideHandle, "Owned event focus baseline was not established before showing events.");
            log("event-native-foreground-baseline: " + outsideHandle);
            var invalid = manager.Show(Request(0) with { WorkArea = new(int.MaxValue - 100, 0, 600, 600) });
            Check(!invalid.Accepted && !manager.HasResources, "Invalid native event coordinates were accepted or leaked an owner.");

            manager.ReducedMotionOverride = false;
            Check(manager.Show(Request(0)).Accepted, "Initial automatic event was rejected.");
            var first = manager.Inspect().Single(); Remember();
            Check(FlyoutNative.GetForegroundWindow() == outsideHandle, "Automatic event stole foreground.");
            Check(FlyoutNative.ReadOpacity(first.Windows[0].Handle) == 0, "Event entrance did not start at zero alpha.");
            clock.Advance(TimeSpan.FromSeconds(10));
            await Until(() => manager.Inspect().Count == 1 && FlyoutNative.ReadOpacity(first.Windows[0].Handle) == 255,
                "Event expired while its initial frame was invisible.");
            manager.ReducedMotionOverride = true;
            clock.Advance(TimeSpan.FromMilliseconds(7999)); manager.Refresh(Display);
            Check(manager.Inspect().Count == 1, "Background Refresh reset or prematurely expired event lifetime.");
            clock.Advance(TimeSpan.FromMilliseconds(1)); manager.Refresh(Display);
            await Until(() => !manager.HasResources, "Auto event did not expire at eight visible idle seconds.");
            Check(!FlyoutNative.IsWindow(first.Windows[0].Handle), "Expired event HWND remains alive.");
            log("PASS: event native initial visibility excludes loading time; 7999/8000ms; automatic show preserves foreground.");

            Check(manager.Show(Request(0)).Accepted, "Automatic event recreation failed.");
            var refreshing = manager.Inspect().Single(); Remember();
            clock.Advance(TimeSpan.FromSeconds(7));
            Check(manager.Show(Request(0, "other-trigger-screen")).Accepted, "Same-channel refresh was rejected.");
            var updated = manager.Inspect().Single();
            Check(updated.Identity == refreshing.Identity && updated.Identity.ScreenId == "events" &&
                updated.Windows[0].Handle == refreshing.Windows[0].Handle, "Same-channel event changed screen, generation, or HWND.");
            clock.Advance(TimeSpan.FromMilliseconds(7999)); manager.Refresh(Display);
            Check(manager.HasResources, "New event did not reset its full eight seconds.");
            clock.Advance(TimeSpan.FromMilliseconds(1)); manager.Refresh(Display);
            await Until(() => !manager.HasResources, "Refreshed event did not expire after eight seconds.");
            log("PASS: same channel retains screen/generation/HWND and new event refreshes lifetime.");

            foreach (int limit in new[] { 1, 5, 10 })
            {
                Settings(limit);
                for (int id = 1; id <= limit; id++) Check(manager.Show(Request(id)).Accepted, "Event capacity setup failed.");
                Remember();
                var oldest = manager.Inspect().OrderBy(value => value.Identity.Generation).First();
                Check(manager.Show(Request(11)).Accepted, "Unprotected oldest event was not replaceable."); Remember();
                Check(manager.Inspect().Count == limit && manager.Inspect().All(value => value.Identity != oldest.Identity),
                    "Replacing oldest event violated per-screen capacity.");
                Check(oldest.Windows.All(value => !FlyoutNative.IsWindow(value.Handle)), "Replacement did not confirm old native destruction.");
                Clear();
                log($"PASS: native event capacity={limit}; oldest replacement destroys before new owner.");
            }

            Settings(1);
            Check(manager.Show(Request(1)).Accepted, "Immediate hover setup failed."); Remember();
            var immediate = manager.Inspect().Single();
            var immediateBounds = immediate.Windows[0].Bounds;
            SetCursorPos(immediateBounds.X + immediateBounds.Width / 2, immediateBounds.Y + immediateBounds.Height / 2);
            Check(manager.Show(Request(2)).Code == "AllProtected",
                "A new event replaced a group under the actual cursor before the next timer poll.");
            Check(manager.Inspect().Single().Identity == immediate.Identity, "Immediate hover protection lost the original owner.");
            Clear(); SetCursorPos((int)work.Right - 120, work.Y + 60);
            Settings(2);
            Check(manager.Show(Request(1)).Accepted && manager.Show(Request(2)).Accepted, "Immediate limit setup failed."); Remember();
            var instantOldest = manager.Inspect().OrderBy(value => value.Identity.Generation).First();
            var instantBounds = instantOldest.Windows[0].Bounds;
            SetCursorPos(instantBounds.X + instantBounds.Width / 2, instantBounds.Y + instantBounds.Height / 2);
            Settings(1);
            Check(manager.Inspect().Single().Identity == instantOldest.Identity,
                "Lowering the limit evicted a group under the actual cursor before the next timer poll.");
            Clear(); SetCursorPos((int)work.Right - 120, work.Y + 60);
            log("PASS: request admission and lower-limit convergence read actual hover synchronously before eviction.");

            Settings(1);
            Check(manager.Show(Request(1)).Accepted, "Protected event setup failed.");
            var protectedGroup = manager.Inspect().Single(); Remember();
            Check(manager.EnterKeyboard(protectedGroup.Identity).IsSuccess, "Event keyboard entry failed.");
            await Until(() => manager.Inspect().Single().Protected, "Actual focused event did not gain interaction protection.");
            clock.Advance(TimeSpan.FromSeconds(20)); manager.Refresh(Display);
            Check(manager.Show(Request(2)).Code == "AllProtected", "Focused persistent event was replaced.");
            var eventWindow = manager.WindowForTesting(protectedGroup.Identity)!;
            var open = Descendants(eventWindow.Root).OfType<Button>().Single(value => AutomationProperties.GetAutomationId(value) == "mtp-template-open");
            ((IInvokeProvider)new ButtonAutomationPeer(open).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => manager.Inspect().Single().Windows.Any(value => value.TemplateId == "child"), "Native event OpenPanel did not navigate.");
            Check(manager.Inspect().Single().Identity == protectedGroup.Identity, "Event panel navigation replaced its group identity.");
            outside.Activate(); ((Button)outside.Content).Focus(FocusState.Keyboard);
            SetCursorPos((int)work.Right - 120, work.Y + 60); await Pump(); manager.Refresh(Display);
            Check(manager.HasResources, "Outside focus closed a persistent event.");
            clock.Advance(TimeSpan.FromMilliseconds(4999)); manager.Refresh(Display);
            Check(manager.Show(Request(2)).Code == "AllProtected", "Post-interaction protection ended before five seconds.");
            clock.Advance(TimeSpan.FromMilliseconds(1)); manager.Refresh(Display);
            Check(manager.Show(Request(2)).Accepted, "Post-interaction protection did not end at five seconds."); Remember();
            Clear();
            Check(manager.Show(Request(1)).Accepted, "Event reopening failed.");
            var reopened = manager.Inspect().Single(); Remember();
            Check(reopened.Identity.Generation != protectedGroup.Identity.Generation && reopened.Windows[0].TemplateId == "main",
                "Reopened channel restored stale generation or temporary panel navigation.");
            Clear();
            log("PASS: actual keyboard protects group; native panel navigation preserves group; outside focus keeps persistent group; 4999/5000ms protection; reopen resets navigation.");

            Settings(2);
            Check(manager.Show(Request(1)).Accepted && manager.Show(Request(2)).Accepted, "Convergence setup failed."); Remember();
            var two = manager.Inspect().OrderBy(value => value.Identity.Generation).ToArray();
            Check(manager.EnterKeyboard(two[0].Identity).IsSuccess, "Convergence keyboard entry failed.");
            var hovered = two[1].Windows[0].Bounds;
            SetCursorPos(hovered.X + hovered.Width / 2, hovered.Y + hovered.Height / 2);
            await Until(() => manager.Inspect().All(value => value.Protected), "Hover and keyboard did not protect separate groups.");
            Settings(1);
            Check(manager.Inspect().Count == 2 && manager.Show(Request(3)).Code == "Converging", "Lower limit removed protected groups or admitted another channel.");
            outside.Activate(); ((Button)outside.Content).Focus(FocusState.Keyboard);
            SetCursorPos((int)work.Right - 120, work.Y + 60); await Pump(); manager.Refresh(Display);
            clock.Advance(TimeSpan.FromSeconds(5)); manager.Refresh(Display);
            await Until(() => manager.Inspect().Count == 1, "Lower limit did not converge after protection ended.");
            Clear(); log("PASS: native hover+keyboard protect two groups; reduced limit waits and refuses new channel until convergence.");

            Settings(5);
            Check(manager.Show(Request(1)).Accepted && manager.Show(Request(2, "unrelated-screen")).Accepted, "Cleanup isolation setup failed."); Remember();
            var failed = manager.Inspect().Single(value => value.Identity.ScreenId == "events");
            var unrelated = manager.Inspect().Single(value => value.Identity.ScreenId == "unrelated-screen");
            var failingWindow = manager.WindowForTesting(failed.Identity)!;
            failingWindow.SimulateCloseFailure = true;
            Check(!manager.CloseEntry(failed.Entry, failed.Identity.SessionId).IsSuccess, "Injected close failure was reported as complete.");
            Check(manager.Inspect().Any(value => value.Identity == failed.Identity && value.CleanupPending) &&
                FlyoutNative.IsWindow(failed.Windows[0].Handle), "Cleanup failure lost its live native owner.");
            Check(manager.Show(Request(3)).Code == "EventCleanupPending", "Failed cleanup admitted another owner on its screen.");
            Check(manager.Inspect().Any(value => value.Identity == unrelated.Identity && !value.Closing), "Cleanup failure affected unrelated screen.");
            failingWindow.SimulateCloseFailure = false;
            Check(manager.CloseEntry(failed.Entry, failed.Identity.SessionId).IsSuccess, "Explicit cleanup retry did not release owner.");
            Clear(); log("PASS: native destruction failure retains owner and blocks affected screen only; explicit retry succeeds.");
        }
        finally
        {
            var cleanup = manager.TryClose();
            try { outside.Close(); }
            finally
            {
                SetCursorPos(previousCursor.X, previousCursor.Y);
                if (previousForeground != 0 && FlyoutNative.IsWindow(previousForeground)) SetForegroundWindow(previousForeground);
            }
            Check(cleanup.IsSuccess, "Event manager shutdown did not release native windows.");
            Check(handles.All(handle => !FlyoutNative.IsWindow(handle)), "Event regression left a recorded HWND alive.");
            log($"event-native-cleanup: handles={handles.Count}; remaining=0");
        }
    }
    private static BrokerStateStore Ready()
    {
        var store = new BrokerStateStore([App]);
        var template = new EntryTemplateDeclaration("main", [new("main", new("body", TemplateNodeKind.Vertical, Children:
            [Text("title"), new("open", TemplateNodeKind.Button, Value: new(Literal: new(TemplateValueKind.Text, Text: "详情")),
                AccessibleName: "详情", Action: new(TemplateActionKind.OpenPanel, "child"))])),
            new("child", new("back", TemplateNodeKind.Button, Value: new(Literal: new(TemplateValueKind.Text, Text: "返回")),
                AccessibleName: "返回", Action: new(TemplateActionKind.Back)))], [], Panels: [new("child")]);
        var declaration = new ApplicationDeclaration(App, [new("main", [new("component", [new("activate")])],
            [new("panel", [new("activate")])], EventChannels: Enumerable.Range(0, 12).Select(id =>
                new EventChannelDeclaration("event" + id, id == 0 ? EventClosePolicy.AutoClose : EventClosePolicy.Persistent, template)).ToArray())]);
        store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = App, SessionId = "session" });
        var accepted = store.Handle(new() { Kind = MessageKind.Declare, ApplicationId = App, SessionId = "session", Declaration = declaration,
            State = new(0, [new("main", "component", "events")], TemplateEntries: Enumerable.Range(0, 12).Select(id =>
                new TemplateEntryState(new("main", TemplateEntryKind.EventChannel, "event" + id), [])).ToArray()) }).Result!;
        Check(accepted.Accepted, "Event native fixture declaration failed: " + accepted.Message);
        return store;
    }
    private static TemplateNode Text(string id) => new(id, TemplateNodeKind.Text, Value: new(Literal: new(TemplateValueKind.Text, Text: "事件组原生回归")));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static Task Pump() => Task.Delay(200);
    private static async Task Until(Func<bool> predicate, string failure)
    { var watch = Stopwatch.StartNew(); while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException(failure); await Task.Delay(25); } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ManualClock : TimeProvider
    {
        private long value;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => value;
        internal void Advance(TimeSpan duration) => value = checked(value + duration.Ticks);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}

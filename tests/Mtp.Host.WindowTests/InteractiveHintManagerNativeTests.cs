using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class InteractiveHintManagerNativeTests
{
    private static readonly TemplateEntryReference Hint = new("group", TemplateEntryKind.Hint, "adjust");
    private static readonly TemplateEntryReference Panel = new("group", TemplateEntryKind.TaskbarFlyout, "details");
    internal static async Task RunAsync(Action<string> log)
    {
        var states = new BrokerStateStore(["interactive-test"]);
        Connect(states, "first");
        using var images = new RegisteredImageCache();
        using var groups = new TaskbarFlyoutManager(states, (entry, template, navigate) => new TemplateRenderer(
            new TemplateInteractionController(states, entry.ApplicationId, entry.Entry,
                (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), template, navigate), images),
            (kind, value) => log(kind + ": " + JsonSerializer.Serialize(value)));
        groups.ReducedMotionOverride = true;
        var clock = new ManualClock();
        int expansions = 0;
        using var hints = new InteractiveHintManager(states, groups, images,
            (_, _, _, _, _) => Task.FromResult(ProtocolResult.Success()),
            (source, target) => { expansions++; return source.Owner is { } owner
                ? groups.ExpandFromHint(owner, target.PanelTemplateId) : ProtocolResult.Reject("AnchorUnavailable", "fixture has no island"); },
            (kind, value) => log(kind + ": " + JsonSerializer.Serialize(value)), clock);
        hints.ReducedMotionOverride = true;
        var work = DisplayArea.Primary.WorkArea;
        var area = new PixelRect(work.X, work.Y, work.Width, work.Height);
        var dpiProbe = new Microsoft.UI.Xaml.Window();
        dpiProbe.AppWindow.Move(new(work.X, work.Y));
        uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(dpiProbe));
        dpiProbe.Close();
        Check(dpi > 0, "owned display DPI probe failed");
        var request = new ShortHintRequest(new("interactive-test", Hint), "first", "screen", area, dpi);
        var handles = new List<nint>();
        try
        {
            Check(hints.Show(request).Accepted, "interactive hint did not open");
            var opened = hints.Inspect().Single();
            var window = hints.WindowForTesting(opened.Generation)!;
            handles.Add(window.Handle); handles.Add(window.BackgroundHandle);
            clock.Advance(TimeSpan.FromMilliseconds(4999));
            hints.Refresh(); await Pump();
            Check(hints.Inspect().Count == 1 && window.IsVisible, "background refresh changed five-second lifetime");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => !hints.HasResources, "five-second expiry did not close both windows");
            Check(handles.All(x => !FlyoutNative.IsWindow(x)), "expired interactive hint leaked native resource");
            log("interactive-manager-pass: visible 4999ms retained; ordinary refresh did not extend; 5000ms closed both HWNDs");

            var groupRequest = new FlyoutOpenRequest(new("interactive-test", Panel), "first", "screen",
                new((int)area.Right - 120, (int)area.Bottom - 8, 60, 8), area, dpi);
            Check(groups.Toggle(groupRequest).IsSuccess, "owner group failed to open");
            var group = groups.Inspect().Single();
            var owner = new HintOwner(group.ScreenId, group.Generation, group.Entry, group.SessionId);
            var associated = request with { Owner = owner };
            Check(hints.Show(associated).Accepted, "associated interactive hint failed");
            opened = hints.Inspect().Single(); window = hints.WindowForTesting(opened.Generation)!;
            handles.Add(window.Handle); handles.Add(window.BackgroundHandle);
            clock.Advance(TimeSpan.FromSeconds(4));
            Check(hints.Show(associated).Code == "Refreshed" && hints.Inspect().Single().Handle == opened.Handle,
                "new request must refresh the same native pair");
            clock.Advance(TimeSpan.FromMilliseconds(4999)); hints.Refresh(); await Pump();
            Check(hints.HasResources, "new request did not reset lifetime");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => !hints.HasResources, "associated hint lifetime incorrectly follows open main group");
            Check(groups.Inspect().Single().Generation == group.Generation && !groups.Inspect().Single().Closing,
                "hint timeout closed main group");
            log("interactive-manager-pass: repeated request retained HWND and reset five seconds; hint timeout kept owner group");

            Check(hints.Show(associated).Accepted, "second associated hint failed");
            opened = hints.Inspect().Single(); window = hints.WindowForTesting(opened.Generation)!;
            handles.Add(window.Handle); handles.Add(window.BackgroundHandle);
            Check(groups.CloseScreen(group.ScreenId, group.Generation).IsSuccess, "owner group close failed");
            hints.Refresh();
            Check(!hints.HasResources, "owner close converted associated hint to independent");
            Check(hints.Show(associated).Code == "StaleHint", "old owner generation accepted new hint");
            Check(hints.Show(request).Accepted, "independent hint after owner close failed");
            opened = hints.Inspect().Single(); window = hints.WindowForTesting(opened.Generation)!;
            handles.Add(window.Handle); handles.Add(window.BackgroundHandle);
            Connect(states, "second"); hints.Refresh();
            Check(!hints.HasResources && hints.Show(request).Code == "StaleHint", "reconnect retained old preview/window");
            request = request with { SessionId = "second" };
            Check(hints.Show(request).Accepted, "current session could not open");
            opened = hints.Inspect().Single(); window = hints.WindowForTesting(opened.Generation)!;
            handles.Add(window.Handle); handles.Add(window.BackgroundHandle);
            var identity = new StableIdentity(new StableId("interactive-test")).CreateChild(new("group")).CreateChild(new("adjust"));
            hints.ApplySettings(new(new(), [], HintVisibility: new Dictionary<string, bool>
                { [HostSettingsController.IdentityKey(identity)] = false }));
            Check(!hints.HasResources && hints.Show(request).Code == "EntryDisabled", "disabled hint not cleared/rejected");
            Check(handles.All(x => !FlyoutNative.IsWindow(x)), "owned native hint handle survived cleanup");
            log("interactive-manager-pass: owner generation, session replacement, visibility and both native resources cleaned");
            hints.ApplySettings(new(new(), []));
            Check(hints.Show(request).Accepted, "native-close fixture could not open");
            var closedWindow = hints.WindowForTesting(hints.Inspect().Single().Generation)!;
            Check(closedWindow.TryClose().IsSuccess, "native-close fixture did not destroy pair");
            await Until(() => !hints.HasResources, "native window termination retained manager owner");
            log("interactive-manager-pass: externally ended native pair removed by owner driver");
            Check(hints.Show(request).Accepted, "cleanup failure fixture could not open");
            var failing = hints.Inspect().Single();
            var failingWindow = hints.WindowForTesting(failing.Generation)!;
            int attempts = 0;
            failingWindow.CloseForegroundForTesting = value =>
            {
                if (++attempts == 1) throw new System.Runtime.InteropServices.COMException("Injected interactive manager close failure");
                value.Close();
            };
            clock.Advance(TimeSpan.FromSeconds(2));
            Check(hints.Show(request with { ScreenId = "other-screen" }).Accepted, "independent cleanup fixture failed");
            var independent = hints.Inspect().Single(x => x.Generation != failing.Generation);
            var independentWindow = hints.WindowForTesting(independent.Generation)!;
            nint independentBack = independentWindow.BackgroundHandle;
            clock.Advance(TimeSpan.FromSeconds(3));
            await Until(() => attempts == 1, "expired interactive owner did not exercise close failure");
            Check(failingWindow.IsAlive && hints.Inspect().Count == 2, "close failure lost owner or other hint");
            clock.Advance(TimeSpan.FromSeconds(2));
            hints.Refresh(); await Until(() => hints.Inspect().Count == 1, "close failure froze independent five-second lifetime");
            Check(attempts == 1 && !FlyoutNative.IsWindow(independent.Handle) && !FlyoutNative.IsWindow(independentBack),
                "cleanup retried automatically or leaked independent pair");
            Check(hints.TryClose().IsSuccess && attempts == 2 && !hints.HasResources && !FlyoutNative.IsWindow(failing.Handle),
                "explicit close did not release retained native owner");
            log("interactive-manager-pass: one injected foreground-close failure isolated; other pair expired; explicit retry resources=0");
        }
        finally
        {
            Check(hints.TryClose().IsSuccess, "interactive manager cleanup failed");
            Check(groups.TryClose().IsSuccess, "owner group cleanup failed");
        }
    }

    internal static ApplicationDeclaration Declaration() => new("interactive-test", [new("group",
        [new("component", [new("noop")])],
        [new("details", [new("noop")], Template: new("main", [
            new("main", new("title", TemplateNodeKind.Text, Value: new(new(TemplateValueKind.Text, Text: "所属主面板")))),
            new("expanded", new("details", TemplateNodeKind.Text, Value: new(new(TemplateValueKind.Text, Text: "已展开详情"))))], [],
            Panels: [new("expanded")]))],
        Hints: [new("adjust", FlyoutKind.InteractiveHint, new("compact", [new("compact",
            new("row", TemplateNodeKind.Horizontal, Children: [
                new("label", TemplateNodeKind.Text, Value: new(new(TemplateValueKind.Text, Text: "模拟调节"))),
                new("value", TemplateNodeKind.Slider, Value: new(StateFieldId: "level"), AccessibleName: "模拟值", Action: new(TemplateActionKind.Business, "set"), Minimum: 0, Maximum: 100, Step: 1),
                new("expand", TemplateNodeKind.IconButton, AccessibleName: "展开", Icon: HostIcon.ChevronRight, Action: new(TemplateActionKind.ExpandHint))]))],
            [new("level", TemplateValueKind.Number)]), [new("set", ActionParameterKind.Number)], new("details", "expanded"))])]);

    internal static void Connect(BrokerStateStore states, string session)
    {
        Check(states.RequireSessionReady("interactive-test").Accepted, "fixture SessionReady requirement failed");
        Check(states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "interactive-test", SessionId = session }).Result!.Accepted, "fixture welcome failed");
        var declared = states.Handle(new() { Kind = MessageKind.Declare, ApplicationId = "interactive-test", SessionId = session,
            Declaration = Declaration(), State = new(0, [new("group", "component", "模拟值")],
                TemplateEntries: [new(Hint, [new("level", new(TemplateValueKind.Number, Number: 40))]), new(Panel, [])]) }).Result!;
        Check(declared.Accepted, "fixture declare failed: " + declared.Code + " " + declared.Message);
        Check(states.Handle(new() { Kind = MessageKind.SessionReady, ApplicationId = "interactive-test", SessionId = session }).Result!.Accepted, "fixture ready failed");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Pump() { await Task.Delay(60); }
    private static async Task Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(4)) { if (condition()) return; await Task.Delay(16); }
        Check(condition(), message);
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan time) => ticks += time.Ticks;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}

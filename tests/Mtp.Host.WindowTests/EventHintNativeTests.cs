using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>Production event/hint owners and real native frames; run only in a serialized desktop slot.</summary>
internal static class EventHintNativeTests
{
    private const string App = "event-hint-native";
    internal static async Task RunAsync(Action<string> log)
    {
        var deadline = Stopwatch.StartNew();
        var states = Ready();
        using var images = new RegisteredImageCache();
        using var taskbar = new TaskbarFlyoutManager(states, (key, id, navigate) => new TemplateRenderer(
            new TemplateInteractionController(states, key.ApplicationId, key.Entry,
                (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), id, navigate), images), (_, _) => { })
            { ReducedMotionOverride = true };
        using var events = new EventGroupManager(states, images, (_, _, _, _) => Task.FromResult(ProtocolResult.Success()),
            (_, _) => { }) { ReducedMotionOverride = true };
        using var hints = new ShortHintManager(states, taskbar, images, (_, _) => { }, events: events)
            { ReducedMotionOverride = true };
        int hintNotifications = 0;
        hints.FrameApplied += () => throw new InvalidOperationException("Injected frame observer failure");
        hints.FrameApplied += () => hintNotifications++;
        var primary = DisplayArea.Primary.WorkArea;
        var work = new PixelRect(primary.X, primary.Y, Math.Min(primary.Width, 900), Math.Min(primary.Height, 620));
        var handles = new HashSet<nint>();
        EventGroupOpenRequest Request(string id, FlyoutPosition position = FlyoutPosition.BottomLeft) =>
            new(new(App, new("main", TemplateEntryKind.EventChannel, id)), "session", "screen", work, 96, position);
        HintOwner Owner(string id)
        {
            var value = events.Inspect().Single(x => x.Entry.Entry.EntryId == id);
            return new(value.Identity.ScreenId, value.Identity.Generation, value.Entry, value.Identity.SessionId);
        }
        ShortHintRequest Hint(HintOwner owner) => new(owner.Entry, owner.SessionId, owner.ScreenId, work, 96, Owner: owner);
        TaskbarDockDisplay? Display(string id) => new(id, true, work, work, 96);
        void Remember()
        {
            foreach (var window in events.Inspect().SelectMany(x => x.Windows).Concat(taskbar.Inspect().SelectMany(x => x.Windows))) handles.Add(window.Handle);
            foreach (var hint in hints.Inspect()) handles.Add(hint.Handle);
        }
        async Task Until(Func<bool> condition, string error)
        {
            var step = Stopwatch.StartNew();
            while (!condition())
            {
                if (step.Elapsed > TimeSpan.FromSeconds(5) || deadline.Elapsed > TimeSpan.FromSeconds(40)) throw new InvalidOperationException(error);
                await Task.Delay(20);
            }
        }
        try
        {
            events.ApplySettings(new(new(), [], Events: new(AllowApplicationPosition: true)));
            var entry = new FlyoutEntryKey(App, new("main", TemplateEntryKind.TaskbarFlyout, "panel"));
            Check(taskbar.Toggle(new(entry, "session", "screen", new((int)work.Right - 24, (int)work.Bottom - 8, 8, 8), work, 96)).IsSuccess,
                "Taskbar owner setup failed.");
            Check(events.Show(Request("short")).Accepted, "Event owner setup failed.");
            var owner = Owner("short");
            var bar = taskbar.Inspect().Single();
            var barOwner = new HintOwner(bar.ScreenId, bar.Generation, bar.Entry, bar.SessionId);
            Check(owner.Generation == barOwner.Generation, "Collision fixture must use the same screen and numeric generation.");
            Check(hints.Show(Hint(owner), "事件动作失败").Accepted && hints.Show(Hint(barOwner), "任务栏动作失败").Accepted,
                "Distinct owner kinds did not admit their own errors.");
            Remember();
            var eventHint = hints.Inspect().Single(x => x.Request.Owner == owner);
            Check(eventHint.Mode == HintPlacementMode.Above, "Bottom event hint did not prefer above.");
            var ownerBounds = events.PresentedBoundsForHint(owner)!.Value;
            Check(eventHint.Bounds.Width == (int)Math.Round(ownerBounds.Width), "Event hint did not match owner width.");
            Check(hints.Show(Hint(owner with { SessionId = "stale" }), "迟到").Code == "StaleHint" &&
                hints.Show(Hint(owner with { Generation = owner.Generation + 1 }), "迟到").Code == "StaleHint" &&
                events.BoundsForHint(owner with { Entry = owner.Entry with { ApplicationId = "other" } }) is null,
                "An incomplete owner identity admitted a hint.");
            Check(taskbar.CloseScreen(bar.ScreenId, bar.Generation).IsSuccess, "Taskbar close failed.");
            await Until(() => hints.Inspect().Count == 1, "Taskbar close did not isolate its associated hint.");
            Check(hints.Inspect().Single().Request.Owner == owner && events.Inspect().Count == 1,
                "Same-number taskbar close touched the event owner.");
            log("PASS: complete event owner identity; same screen/generation across kinds isolated; above placement and actual native width.");

            Check(events.CloseEntry(owner.Entry, owner.SessionId).IsSuccess, "Initial event close failed.");
            await Until(() => hints.Inspect().Count == 0, "Event close retained its associated hint.");
            Check(events.Show(Request("short", FlyoutPosition.TopLeft)).Accepted, "Top event setup failed.");
            var topOwner = Owner("short");
            Check(hints.Show(Hint(topOwner), "顶部事件错误").Accepted, "Top associated hint was rejected."); Remember();
            Check(hints.Inspect().Single().Mode == HintPlacementMode.Below, "Top owner did not fall back below.");
            Check(events.BoundsForHint(owner) is null && hints.Show(Hint(owner), "旧代").Code == "StaleHint",
                "Reopened event accepted its previous owner generation.");
            events.ReducedMotionOverride = false; hints.ReducedMotionOverride = false;
            int frames = 0;
            var nativeFrames = new List<object>();
            var frameErrors = new List<string>();
            void Sample(HintOwner current)
            {
                if (current != topOwner || events.PresentedBoundsForHint(current) is not { } bounds) return;
                var currentHint = hints.Inspect().SingleOrDefault(x => x.Request.Owner == current);
                if (currentHint is null) return;
                if ((Math.Abs(currentHint.Bounds.X - bounds.X) > 1 || Math.Abs(currentHint.Bounds.Width - bounds.Width) > 1) && frameErrors.Count < 8)
                    frameErrors.Add($"Hint diverged from actual event frame: owner={bounds}; hint={currentHint.Bounds}");
                if (nativeFrames.Count < 64) nativeFrames.Add(new { owner = bounds, hint = currentHint.Bounds });
                frames++;
            }
            events.PresentedFrameApplied += Sample;
            try
            {
                Check(events.Show(Request("short", FlyoutPosition.BottomRight)).Accepted, "Event move failed.");
                await Until(() => frames >= 3, "Event move supplied no actual intermediate associated frames.");
                events.ReducedMotionOverride = true; hints.ReducedMotionOverride = true; events.Refresh(Display);
                Check(FlyoutNative.ToPixels(events.PresentedBoundsForHint(topOwner)!.Value, 96) ==
                    FlyoutNative.ToPixels(events.BoundsForHint(topOwner)!.Value, 96), "Reduced motion missed its event target.");
                Check(frameErrors.Count == 0, string.Join("; ", frameErrors));
            }
            finally { events.PresentedFrameApplied -= Sample; }
            log("event-hint-frames: " + System.Text.Json.JsonSerializer.Serialize(nativeFrames));
            log("PASS: below fallback, old owner rejected, actual frame width/translation and reduced-motion terminal bounds.");
            Check(events.CloseEntry(topOwner.Entry, topOwner.SessionId).IsSuccess, "Moved event cleanup failed.");
            await Until(() => hints.Inspect().Count == 0, "Moved event left its hint alive.");

            work = new(primary.X, primary.Y, Math.Min(primary.Width, 900), 320);
            Check(events.Show(Request("tall")).Accepted && events.Show(Request("sibling", FlyoutPosition.TopRight)).Accepted,
                "Reservation owner/sibling setup failed.");
            var tallOwner = Owner("tall"); var sibling = Owner("sibling");
            var original = events.BoundsForHint(tallOwner)!.Value;
            var siblingBefore = events.PresentedBoundsForHint(sibling);
            Check(hints.Show(Hint(tallOwner), "空间不足时仅所属事件组内部滚动").Accepted, "Event reservation hint failed.");
            Remember();
            var reserved = events.BoundsForHint(tallOwner)!.Value;
            var reservedHint = hints.Inspect().Single();
            Check(reserved.Height < original.Height && reserved.Height >= FlyoutGroupLayout.MinimumHeightDip &&
                reserved.Y >= original.Y + reservedHint.Bounds.Height + HintLayout.GapDip,
                "Event did not reserve its own top area while retaining minimum operations.");
            Check(events.PresentedBoundsForHint(sibling) == siblingBefore && events.Inspect().Count == 2,
                "Associated hint moved or closed an unrelated group.");
            var native = hints.WindowForTesting(reservedHint.Generation)!;
            native.CloseWindowForTesting = _ => throw new COMException("Injected associated hint close failure");
            Check(!hints.TryClose().IsSuccess && hints.HasResources && native.IsAlive && events.BoundsForHint(tallOwner) == reserved,
                "Failed hint destruction lost owner/reservation.");
            native.CloseWindowForTesting = null;
            int beforeCloseNotification = hintNotifications;
            Check(hints.TryClose().IsSuccess && !hints.HasResources && events.BoundsForHint(tallOwner) == original &&
                events.PresentedBoundsForHint(sibling) == siblingBefore,
                "Explicit hint cleanup did not release only its owner's reservation.");
            Check(hintNotifications > beforeCloseNotification, "Native hint destruction did not notify the remaining frame observer.");
            log("PASS: local event height reservation retains operations, sibling position/lifetime unchanged, native hint close failure retains reservation until successful retry.");
        }
        finally
        {
            foreach (var hint in hints.Inspect()) if (hints.WindowForTesting(hint.Generation) is { } window) window.CloseWindowForTesting = null;
            Remember();
            Check(hints.TryClose().IsSuccess && events.TryClose().IsSuccess && taskbar.TryClose().IsSuccess,
                "Associated event hint fixture cleanup failed.");
            Check(!hints.HasResources && !events.HasResources && !taskbar.HasResources && handles.All(x => !FlyoutNative.IsWindow(x)),
                "Associated event hint fixture left native resources.");
        }
        Check(deadline.Elapsed < TimeSpan.FromSeconds(45), "Associated event hint exceeded its total budget.");
        log($"PASS: event-hint native combination; elapsedMs={deadline.ElapsedMilliseconds}; ownedHandles={handles.Count}; resources=0.");
    }
    private static BrokerStateStore Ready()
    {
        var shortTemplate = new EntryTemplateDeclaration("main", [new("main", Text("short", "事件动作"))], []);
        var tallTemplate = new EntryTemplateDeclaration("main", [new("main", new("body", TemplateNodeKind.Vertical,
            Children: Enumerable.Range(0, 20).Select(x => Text("line" + x, "事件详情需要滚动：" + x)).ToArray()))], []);
        var panel = new TemplateEntryReference("main", TemplateEntryKind.TaskbarFlyout, "panel");
        var store = new BrokerStateStore([App]);
        var declaration = new ApplicationDeclaration(App, [new("main", [new("component", [new("activate")])],
            [new("panel", [new("activate")], shortTemplate)], EventChannels:
            [new("short", EventClosePolicy.Persistent, shortTemplate), new("tall", EventClosePolicy.Persistent, tallTemplate),
                new("sibling", EventClosePolicy.Persistent, shortTemplate)])]);
        store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = App, SessionId = "session" });
        var result = store.Handle(new() { Kind = MessageKind.Declare, ApplicationId = App, SessionId = "session", Declaration = declaration,
            State = new(0, [new("main", "component", "ready")], TemplateEntries:
                [new(panel, []), new(new("main", TemplateEntryKind.EventChannel, "short"), []),
                    new(new("main", TemplateEntryKind.EventChannel, "tall"), []), new(new("main", TemplateEntryKind.EventChannel, "sibling"), [])]) }).Result!;
        Check(result.Accepted, "Event hint fixture declaration rejected: " + result.Message);
        return store;
    }
    private static TemplateNode Text(string id, string text) => new(id, TemplateNodeKind.Text,
        Value: new(Literal: new(TemplateValueKind.Text, Text: text)));
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
}

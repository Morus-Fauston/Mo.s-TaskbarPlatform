using System.Diagnostics;
using Microsoft.UI.Windowing;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>Serial real-HWND frame regression, bounded to eight seconds; never human acceptance.</summary>
internal static class ShortHintMotionNativeTests
{
    internal static async Task RunAsync(Action<string> log)
    {
        const string app = "hint-motion-test", session = "motion-session", screen = "motion-screen";
        var watch = Stopwatch.StartNew();
        var entry = new TemplateEntryReference("group", TemplateEntryKind.TaskbarFlyout, "panel");
        var template = new EntryTemplateDeclaration("main", [new("main", new("body", TemplateNodeKind.Vertical, Children:
            [new("text", TemplateNodeKind.Text, Value: new(new(TemplateValueKind.Text, Text: "关联提示跟随正在呈现的浮窗组"))),
             new("detail", TemplateNodeKind.Text, Value: new(new(TemplateValueKind.Text, Text: "原生矩形逐帧验证")))]))], []);
        var declaration = new ApplicationDeclaration(app, [new("group", [new("component", [new("primary")])],
            [new("panel", [new("commit")], Template: template)])]);
        var store = new BrokerStateStore([app]);
        Check(store.RequireSessionReady(app).Accepted, "Motion fixture readiness policy rejected.");
        Check(store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = app, SessionId = session }).Result!.Accepted,
            "Motion fixture Welcome rejected.");
        var declared = store.Handle(new() { Kind = MessageKind.Declare, ApplicationId = app, SessionId = session,
            Declaration = declaration, State = new(0, [new("group", "component", "motion", 0)], TemplateEntries: [new(entry, [])]) }).Result!;
        Check(declared.Accepted, "Motion fixture declaration rejected: " + declared.Message);
        Check(store.Handle(new() { Kind = MessageKind.SessionReady, ApplicationId = app, SessionId = session }).Result!.Accepted,
            "Motion fixture SessionReady rejected.");
        using var images = new RegisteredImageCache();
        using var groups = new TaskbarFlyoutManager(store, (key, id, navigate) => new TemplateRenderer(
            new TemplateInteractionController(store, key.ApplicationId, key.Entry,
                (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), id, navigate), images), (_, _) => { });
        ShortHintManager? hints = null;
        var frames = new List<Frame>();
        var failures = new List<string>();
        string phase = "opening";
        bool capture = false;
        void Record(string kind, object? detail)
        {
            if (kind.Contains("failed", StringComparison.Ordinal) || kind == "hint-cleanup-pending")
                log(kind + ": " + System.Text.Json.JsonSerializer.Serialize(detail));
            if (!capture || kind != "hint-frame-applied" || hints is null) return;
            try
            {
                var hint = hints.Inspect().SingleOrDefault();
                var group = groups.Inspect().SingleOrDefault();
                if (hint is null || group is null || hint.Handle == 0 || group.Windows.Count != 1) return;
                nint owner = group.Windows[0].Handle;
                byte alpha = FlyoutNative.ReadOpacity(hint.Handle);
                if (alpha == 0) return;
                var hintBounds = FlyoutNative.Bounds(hint.Handle);
                var ownerBounds = FlyoutNative.Bounds(owner);
                if (frames.Count >= 256) throw new InvalidOperationException("Motion evidence exceeded 256-frame budget.");
                frames.Add(new(phase, watch.Elapsed.TotalMilliseconds, ownerBounds, hintBounds, alpha));
                if (hintBounds.X != ownerBounds.X || hintBounds.Width != ownerBounds.Width)
                    throw new InvalidOperationException($"Hint diverged from presented owner: {phase}; owner={ownerBounds}; hint={hintBounds}.");
                long gap = (long)ownerBounds.Y - hintBounds.Bottom;
                if (phase == "opening" ? gap is < -1 or > 9 : gap is < 7 or > 9)
                    throw new InvalidOperationException($"Hint detached from its owner's presented upper edge: {phase}; gap={gap}.");
            }
            catch (Exception error) { if (failures.Count < 8) failures.Add(error.Message); }
        }
        using var ownedHints = new ShortHintManager(store, groups, images, Record);
        hints = ownedHints;
        groups.ReducedMotionOverride = false;
        hints.ReducedMotionOverride = false;
        var primary = DisplayArea.Primary.WorkArea;
        Check(primary.Width >= 600 && primary.Height >= 500, "Motion regression needs a 600x500 owned work-area region.");
        var work = new PixelRect(primary.X + 32, primary.Y + 32, 500, 420);
        var anchor = new PixelRect((int)work.Right - 64, (int)work.Bottom - 8, 48, 8);
        var request = new FlyoutOpenRequest(new(app, entry), session, screen, anchor, work, 96);
        try
        {
            Check(groups.Toggle(request).IsSuccess, "Motion fixture group failed to open.");
            await Until(() => groups.WindowForTesting(screen) is { Root.Opacity: 1 }, watch, "Group opening did not finish.");
            var group = groups.Inspect().Single();
            var hintRequest = new ShortHintRequest(group.Entry, session, screen, work, 96,
                Owner: new(screen, group.Generation, group.Entry, session));
            Check(hints.Show(hintRequest, "组尺寸变化时保持等宽及贴合").Accepted, "Associated motion hint failed to open.");
            nint handle = hints.Inspect().Single().Handle;
            capture = true;
            await Until(() => FlyoutNative.ReadOpacity(handle) == 255, watch, "Hint opening did not finish.");
            Check(frames.Any(x => x.Phase == "opening" && x.Alpha is > 0 and < 255),
                "No actual intermediate hint opening opacity frame was observed.");

            phase = "narrowing";
            Reflow(work with { Width = 230 });
            await Until(() => frames.Any(x => x.Phase == "narrowing" && x.Owner.Width is > 198 and < 320),
                watch, "Group narrowing did not present any intermediate width.");
            var turnFrom = FlyoutNative.Bounds(groups.WindowForTesting(screen)!.Handle);
            phase = "retarget";
            Reflow(work with { Width = 300 });
            var turnTo = FlyoutNative.Bounds(groups.WindowForTesting(screen)!.Handle);
            Check(Math.Abs(turnTo.X - turnFrom.X) <= 1 && Math.Abs(turnTo.Width - turnFrom.Width) <= 1,
                "Retarget jumped away from the last actually presented group rectangle.");
            await Until(() => groups.WindowForTesting(screen)!.LastBounds.Width == 268 &&
                hints.Inspect().SingleOrDefault()?.Bounds.Width == 268, watch, "Retarget did not reach its actual 268px terminal width.");

            phase = "expanding";
            Reflow(work);
            await Until(() => frames.Any(x => x.Phase == "expanding" && x.Owner.Width is > 268 and < 320),
                watch, "Group expansion did not present any intermediate width.");
            await Until(() => groups.WindowForTesting(screen)!.LastBounds.Width == 320 &&
                hints.Inspect().SingleOrDefault()?.Bounds.Width == 320, watch, "Expansion did not reach terminal group and hint widths.");

            phase = "reduced";
            groups.ReducedMotionOverride = true;
            hints.ReducedMotionOverride = true;
            Reflow(work with { Width = 230 });
            var reducedOwner = FlyoutNative.Bounds(groups.WindowForTesting(screen)!.Handle);
            var reducedHint = FlyoutNative.Bounds(handle);
            Check(reducedOwner.Width == 198 && reducedHint.Width == 198 && reducedOwner.X == reducedHint.X &&
                reducedOwner.Y - reducedHint.Bottom == 8 && FlyoutNative.ReadOpacity(handle) == 255,
                "Reduced motion did not immediately align the terminal native bounds.");
            Check(failures.Count == 0, "Native associated motion mismatches: " + string.Join(" | ", failures));
            Check(frames.Count > 5 && frames.Count < 256, "Motion evidence lacks bounded intermediate frames.");
            foreach (var frame in frames) log("hint-motion-frame: " + System.Text.Json.JsonSerializer.Serialize(frame));
            capture = false;
            Check(hints.TryClose().IsSuccess && groups.TryClose().IsSuccess && !hints.HasResources && !groups.HasResources,
                "Motion regression leaked native resources.");
            Check(watch.Elapsed <= TimeSpan.FromSeconds(8), "Motion regression exceeded its eight-second budget.");
            log($"PASS: {frames.Count} real associated hint/group frames; intermediate opening and width changes; actual x/width/edge alignment; continuous retarget; reduced motion; resources=0; elapsed={watch.ElapsedMilliseconds}ms.");
        }
        finally
        {
            if (capture) foreach (var frame in frames) log("hint-motion-frame-before-failure: " + System.Text.Json.JsonSerializer.Serialize(frame));
            capture = false;
            Check(hints.TryClose().IsSuccess, "Hint motion fixture cleanup failed.");
            Check(groups.TryClose().IsSuccess, "Group motion fixture cleanup failed.");
        }

        void Reflow(PixelRect next)
        {
            Check(groups.UpdateEnvironment(screen, anchor, next, 96).IsSuccess, "Motion group reflow failed.");
            hints.Refresh(_ => new TaskbarDockDisplay(screen, true, next, next, 96));
        }
    }

    private static async Task Until(Func<bool> condition, Stopwatch budget, string failure)
    {
        var local = Stopwatch.StartNew();
        while (!condition())
        {
            if (local.Elapsed > TimeSpan.FromSeconds(2) || budget.Elapsed > TimeSpan.FromSeconds(8))
                throw new InvalidOperationException(failure);
            await Task.Delay(12);
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Frame(string Phase, double Milliseconds, PixelRect Owner, PixelRect Hint, byte Alpha);
}

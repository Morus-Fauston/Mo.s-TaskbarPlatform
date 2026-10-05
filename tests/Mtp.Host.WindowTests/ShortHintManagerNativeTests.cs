using System.Diagnostics;
using Microsoft.UI.Windowing;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>Real owned WinUI windows with an explicit monotonic clock; desktop execution must be serialized.</summary>
internal static class ShortHintManagerNativeTests
{
    private const string ApplicationId = "hint-native-test";
    private static readonly TemplateEntryReference HintEntry = new("group", TemplateEntryKind.Hint, "hint");
    private static readonly TemplateEntryReference GroupEntry = new("group", TemplateEntryKind.TaskbarFlyout, "panel");

    internal static async Task RunAsync(Action<string> log)
    {
        var store = new BrokerStateStore([ApplicationId]);
        var declaration = CreateDeclaration();
        Connect(store, declaration, "session-one");
        using var images = new RegisteredImageCache();
        using var groups = new TaskbarFlyoutManager(store, (key, id, navigate) => new TemplateRenderer(
            new TemplateInteractionController(store, key.ApplicationId, key.Entry,
                (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), id, navigate), images),
            (kind, data) => log(kind + ": " + System.Text.Json.JsonSerializer.Serialize(data)));
        groups.ReducedMotionOverride = true;
        var clock = new ManualClock();
        using var hints = new ShortHintManager(store, groups, images,
            (kind, data) => log(kind + ": " + System.Text.Json.JsonSerializer.Serialize(data)), clock);
        var nativeWork = DisplayArea.Primary.WorkArea;
        var work = new PixelRect(nativeWork.X, nativeWork.Y, nativeWork.Width, nativeWork.Height);
        var request = new ShortHintRequest(new(ApplicationId, HintEntry), "session-one", "hint-screen", work, 96);
        var handles = new List<nint>();
        try
        {
            var invalidNative = hints.Show(request with { WorkArea = new(int.MaxValue - 100, 0, 600, 600) }, "无法呈现");
            Check(!invalidNative.Accepted && !hints.HasResources, "Failed native geometry was acknowledged as Displayed or leaked ownership.");
            // Loading time is not lifetime: keep the first animation frame invisible for ten clock seconds.
            hints.ReducedMotionOverride = false;
            Check(hints.Show(request, "首次可见才计时").Accepted, "Hint request was rejected.");
            var first = hints.Inspect().Single(); handles.Add(first.Handle);
            Check(FlyoutNative.ReadOpacity(first.Handle) == 0, "Animated hint did not begin at zero opacity.");
            clock.Advance(TimeSpan.FromSeconds(10));
            await Until(() => hints.Inspect().Count == 1 && FlyoutNative.ReadOpacity(first.Handle) == 255,
                "Hint expired before its first actual visible frame.");
            hints.ReducedMotionOverride = true;
            clock.Advance(TimeSpan.FromMilliseconds(2999));
            hints.Refresh();
            await Pump();
            Check(hints.Inspect().Count == 1 && FlyoutNative.IsWindow(first.Handle), "Hint expired before three visible seconds.");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => hints.Inspect().Count == 0, "Ordinary layout refresh incorrectly extended the three-second lifetime.");
            Check(!FlyoutNative.IsWindow(first.Handle), "Expired hint left its HWND alive.");
            log("PASS: first visible frame starts three seconds; creation delay excluded; Refresh at 2999ms does not extend lifetime; 3000ms closes.");

            Check(hints.Show(request, "旧错误").Accepted, "New hint request failed after expiry.");
            nint repeated = hints.Inspect().Single().Handle; handles.Add(repeated);
            clock.Advance(TimeSpan.FromSeconds(2));
            Check(hints.Show(request, "新错误").Accepted, "Replacing hint reason failed.");
            Check(hints.Inspect().Single() is { Text: "新错误" } updated && updated.Handle == repeated,
                "A new failure created another HWND or retained the old reason.");
            clock.Advance(TimeSpan.FromMilliseconds(2999)); await Pump();
            Check(hints.Inspect().Count == 1, "New error did not restart its full lifetime.");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => hints.Inspect().Count == 0, "Refreshed error did not expire at three seconds.");
            log("PASS: new failure reuses HWND, replaces reason and restarts exactly three seconds.");

            // Full screen permits both sides; the synthetic narrow work area forces the owned group reservation.
            await CheckAssociatedPlacement(groups, hints, clock, request, work,
                new(work.X + work.Width - 80, (int)work.Bottom - 8, 48, 8), HintPlacementMode.Above, handles, log);
            await CheckAssociatedPlacement(groups, hints, clock, request, work,
                new(work.X + work.Width - 80, work.Y + 24, 48, 8), HintPlacementMode.Below, handles, log);
            var unrelatedRequest = new FlyoutOpenRequest(new(ApplicationId, GroupEntry), request.SessionId,
                "unrelated-screen", new(work.X + 400, (int)work.Bottom - 8, 48, 8), work, 96);
            Check(groups.Toggle(unrelatedRequest).IsSuccess, "Unrelated group fixture failed.");
            var unrelated = groups.Inspect().Single(x => x.ScreenId == unrelatedRequest.ScreenId);
            handles.Add(unrelated.Windows.Single().Handle);
            var narrow = new PixelRect(work.X + 24, work.Y + 24, Math.Min(work.Width - 48, 600), 220);
            await CheckAssociatedPlacement(groups, hints, clock, request, narrow,
                new((int)narrow.Right - 80, (int)narrow.Bottom - 8, 48, 8), HintPlacementMode.GroupReflow, handles, log);
            var untouched = groups.Inspect().Single(x => x.Generation == unrelated.Generation);
            Check(untouched.Windows.Single().Bounds == unrelated.Windows.Single().Bounds && !untouched.Closing,
                "Narrow-space associated hint moved or closed an unrelated group.");
            Check(groups.CloseScreen(unrelated.ScreenId, unrelated.Generation).IsSuccess, "Unrelated group cleanup failed.");
            log("PASS: associated hint reservation leaves unrelated group's native bounds and lifetime unchanged.");

            hints.ApplyAppearance(new(HostTheme.Dark, MaterialKind.Solid, 0.4));
            Check(hints.Show(request, "外观设置与减少动画").Accepted, "Hint failed after appearance update.");
            nint appearanceHandle = hints.Inspect().Single().Handle; handles.Add(appearanceHandle);
            Check(FlyoutNative.ReadOpacity(appearanceHandle) == 255,
                "Reduced motion should immediately apply the terminal opening frame.");
            hints.ApplyAppearance(new(HostTheme.Light, MaterialKind.None, 1));
            Check(hints.Inspect().Single().Handle == appearanceHandle, "Appearance update replaced the live hint HWND.");
            var stable = new StableIdentity(new StableId(ApplicationId)).CreateChild(new("group")).CreateChild(new("hint"));
            hints.ApplySettings(new(new(), [], HintVisibility: new Dictionary<string, bool>
                { [HostSettingsController.IdentityKey(stable)] = false }));
            Check(!hints.HasResources && !FlyoutNative.IsWindow(appearanceHandle), "Disabling an entry did not immediately hide it.");
            Check(hints.Show(request, "禁止显示").Code == "EntryDisabled", "Disabled entry accepted a new hint.");
            hints.ApplySettings(new(new(), []));
            log("PASS: reduced motion opens at terminal alpha; live appearance update preserves HWND; disabling entry immediately destroys and rejects new requests.");

            Check(hints.Show(request, "旧会话提示").Accepted, "Old session fixture failed.");
            nint oldSessionHandle = hints.Inspect().Single().Handle; handles.Add(oldSessionHandle);
            Connect(store, declaration, "session-two");
            hints.Refresh();
            Check(!hints.HasResources && !FlyoutNative.IsWindow(oldSessionHandle), "Old session hint survived reconnection.");
            Check(hints.Show(request, "迟到旧请求").Code == "StaleHint", "Late old-session request was accepted.");
            Check(hints.Show(request with { SessionId = "session-two" }, "新会话提示").Accepted, "Current session request was rejected.");
            handles.Add(hints.Inspect().Single().Handle);
            Check(hints.TryClose().IsSuccess && !hints.HasResources, "Final hint cleanup failed.");
            Check(groups.TryClose().IsSuccess && !groups.HasResources, "Final group cleanup failed.");
            Check(handles.All(handle => !FlyoutNative.IsWindow(handle)), "An owned HWND survived final cleanup.");
            log("PASS: reconnect destroys old session window; old request rejected; current session accepted; final resources=0.");
            await CheckCleanupIsolation(store, groups, images, request with { SessionId = "session-two" }, log);
        }
        finally
        {
            Check(hints.TryClose().IsSuccess, "Short hint manager fixture cleanup failed.");
            Check(groups.TryClose().IsSuccess, "Taskbar group fixture cleanup failed.");
        }
    }

    private static async Task CheckCleanupIsolation(BrokerStateStore store, TaskbarFlyoutManager groups,
        RegisteredImageCache images, ShortHintRequest request, Action<string> log)
    {
        var clock = new ManualClock();
        using var manager = new ShortHintManager(store, groups, images,
            (kind, data) => log(kind + ": " + System.Text.Json.JsonSerializer.Serialize(data)), clock);
        manager.ReducedMotionOverride = true;
        Check(manager.Show(request, "关闭失败后保留所有权").Accepted, "Failing-close hint fixture did not open.");
        var first = manager.Inspect().Single();
        var failingWindow = manager.WindowForTesting(first.Generation)
            ?? throw new InvalidOperationException("Failing-close native owner is unavailable.");
        int closeAttempts = 0;
        failingWindow.CloseWindowForTesting = value =>
        {
            if (++closeAttempts == 1) throw new System.Runtime.InteropServices.COMException("Injected hint Close failure.");
            value.Close();
        };
        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            Check(manager.Show(request with { ScreenId = "independent-cleanup-screen" }, "独立提示继续计时").Accepted,
                "Independent hint fixture did not open.");
            var second = manager.Inspect().Single(x => x.Generation != first.Generation);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Until(() => closeAttempts == 1, "Expiry did not exercise the injected native Close failure.");
            Check(manager.Inspect().Count == 2 && failingWindow.IsAlive && FlyoutNative.IsWindow(second.Handle),
                "Failed Close lost its owner or affected the still-live independent hint.");
            manager.Refresh();
            manager.ApplySettings(new(new(), []));
            clock.Advance(TimeSpan.FromSeconds(2));
            await Until(() => manager.Inspect().Count == 1 && !FlyoutNative.IsWindow(second.Handle),
                "One hint's failed Close froze the independent hint's three-second timer.");
            await Pump();
            Check(closeAttempts == 1 && failingWindow.IsAlive && manager.HasResources,
                "Cleanup failure was retried automatically or discarded its live native owner.");
            Check(manager.TryClose().IsSuccess && closeAttempts == 2 && !manager.HasResources &&
                !FlyoutNative.IsWindow(first.Handle), "Explicit cleanup retry did not release the retained owner.");
            log("PASS: one native Close failure retains owner without automatic retry; independent hint still expires at three seconds; explicit TryClose retries once; resources=0.");
        }
        finally
        {
            failingWindow.CloseWindowForTesting = null;
            Check(manager.TryClose().IsSuccess, "Failed-close isolation fixture cleanup failed.");
        }
    }

    private static async Task CheckAssociatedPlacement(TaskbarFlyoutManager groups, ShortHintManager hints,
        ManualClock clock, ShortHintRequest independent, PixelRect work, PixelRect anchor,
        HintPlacementMode expected, List<nint> handles, Action<string> log)
    {
        var groupRequest = new FlyoutOpenRequest(new(ApplicationId, GroupEntry), independent.SessionId,
            independent.ScreenId, anchor, work, 96);
        Check(groups.Toggle(groupRequest).IsSuccess, "Associated group did not open.");
        var group = groups.Inspect().Single(x => x.ScreenId == independent.ScreenId);
        var before = group.Windows.Single().Bounds;
        handles.Add(group.Windows.Single().Handle);
        var owner = new HintOwner(group.ScreenId, group.Generation, group.Entry, group.SessionId);
        var request = independent with { Entry = group.Entry, WorkArea = work, Owner = owner };
        Check(hints.Show(request, "关联动作失败").Accepted, "Associated hint did not open.");
        var hint = hints.Inspect().Single(); handles.Add(hint.Handle);
        var after = groups.Inspect().Single(x => x.Generation == group.Generation).Windows.Single().Bounds;
        Check(hint.Bounds.Width == after.Width && hint.Bounds.X == after.X,
            "Associated hint does not share its group's actual width and left edge.");
        Check(work.Contains(hint.Bounds) && work.Contains(after), "Associated hint or group escaped its work area.");
        if (expected == HintPlacementMode.Above)
            Check(hint.Mode == HintPlacementMode.Above && hint.Bounds.Bottom + 8 <= after.Y,
                "Associated hint did not prefer available space above.");
        else if (expected == HintPlacementMode.Below)
            Check(hint.Mode == HintPlacementMode.Below && hint.Bounds.Y >= after.Bottom + 8,
                "Associated hint did not fall below when above was unavailable.");
        else
            Check(after.Height < before.Height && hint.Bounds.Bottom + 8 <= after.Y && after.Height >= 80,
                "Narrow space did not reserve hint space while preserving the group's minimum operation area.");
        clock.Advance(TimeSpan.FromSeconds(3));
        await Until(() => hints.Inspect().Count == 0, "Associated hint did not expire.");
        Check(groups.Inspect().Any(x => x.Generation == group.Generation && !x.Closing),
            "Associated hint timeout closed its owning group.");
        Check(groups.Inspect().Single(x => x.Generation == group.Generation).Windows.Single().Bounds == before,
            "Hint timeout did not release its temporary group reservation.");
        Check(hints.Show(request, "所属组结束前的提示").Accepted, "Second associated hint failed.");
        nint secondHint = hints.Inspect().Single().Handle; handles.Add(secondHint);
        Check(groups.CloseScreen(group.ScreenId, group.Generation).IsSuccess, "Owning group did not close.");
        await Until(() => !hints.HasResources, "Group end left an orphaned associated hint.");
        Check(!FlyoutNative.IsWindow(secondHint), "Group end retained the associated native window.");
        Check(hints.Show(request, "迟到组代次").Code == "StaleHint", "Old group generation was accepted as an independent hint.");
        log($"PASS: associated {expected}; equal actual width; inside work area; timeout preserves group and releases reservation; group end destroys hint; stale generation rejected.");
    }

    private static ApplicationDeclaration CreateDeclaration()
    {
        var rows = Enumerable.Range(0, 12).Select(i => new TemplateNode("line" + i, TemplateNodeKind.Text,
            Value: new(new(TemplateValueKind.Text, Text: "关联提示布局验证 " + i)))).ToArray();
        var panel = new EntryTemplateDeclaration("main", [new("main", new("body", TemplateNodeKind.Vertical, Children: rows))], []);
        var hint = new EntryTemplateDeclaration("main", [new("main", new("message", TemplateNodeKind.Text,
            Value: new(new(TemplateValueKind.Text, Text: "普通提示"))))], []);
        return new(ApplicationId, [new("group", [new("component", [new("primary")])],
            [new("panel", [new("commit")], Template: panel)],
            Hints: [new("hint", FlyoutKind.ShortHint, hint)])]);
    }
    private static void Connect(BrokerStateStore states, ApplicationDeclaration declaration, string session)
    {
        Check(states.RequireSessionReady(ApplicationId).Accepted, "Hint fixture could not require complete session readiness.");
        Check(states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = ApplicationId, SessionId = session }).Result!.Accepted,
            "Hint fixture Welcome rejected.");
        var result = states.Handle(new() { Kind = MessageKind.Declare, ApplicationId = ApplicationId, SessionId = session,
            Declaration = declaration, State = new(0, [new("group", "component", "提示回归夹具", 0)],
                TemplateEntries: [new(HintEntry, []), new(GroupEntry, [])]) }).Result!;
        Check(result.Accepted, "Hint fixture declaration rejected: " + result.Message);
        Check(states.GetSnapshot(ApplicationId) is { IsConnected: true, IsInteractive: false },
            "Incomplete session was prematurely interactive.");
        Check(states.Handle(new() { Kind = MessageKind.SessionReady, ApplicationId = ApplicationId, SessionId = session }).Result!.Accepted,
            "Hint fixture SessionReady rejected.");
        Check(states.GetSnapshot(ApplicationId) is { IsConnected: true, IsInteractive: true } &&
            states.FlyoutRequests.IsEntryEnabled(ApplicationId, session, "group", "hint"),
            "Ready session did not synchronize the hint display policy.");
    }
    private static Task Pump() => Task.Delay(80);
    private static async Task Until(Func<bool> predicate, string failure)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        { if (timeout.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException(failure); await Task.Delay(20); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(timestamp);
        internal void Advance(TimeSpan value) => timestamp = checked(timestamp + value.Ticks);
    }
}

using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>Counts real layout and XAML measurement work through the production refresh entry.</summary>
internal static class TaskbarFlyoutRefreshRegression
{
    internal static void CheckCoordinatorEnvironment(TaskbarFlyoutCoordinator coordinator,
        PixelRect workArea, Action<PixelRect> setWorkArea, Action<string> log)
    {
        var group = coordinator.Manager.Inspect().Single();
        var window = coordinator.Manager.WindowForTesting(group.ScreenId)!;
        var before = FlyoutNative.Bounds(window.Handle);
        try
        {
            setWorkArea(workArea with { Height = workArea.Height - 120 });
            coordinator.Refresh();
            var clamped = FlyoutNative.Bounds(window.Handle);
            Check(clamped.Y < before.Y && coordinator.Manager.EnvironmentForHint(group.ScreenId, group.Generation)?.WorkArea ==
                workArea with { Height = workArea.Height - 120 }, "Production Coordinator deferred the new work area.");
            Check(coordinator.Manager.Inspect().Single().Generation == group.Generation &&
                ReferenceEquals(coordinator.Manager.WindowForTesting(group.ScreenId), window), "Environment update replaced the group or window.");
            log("flyout-refresh-environment: " + JsonSerializer.Serialize(new { before, clamped, sameHwnd = window.Handle.ToInt64() }));
        }
        finally
        {
            setWorkArea(workArea);
            coordinator.Refresh();
        }
        Check(FlyoutNative.Bounds(window.Handle) == before, "Production environment did not restore its native bounds.");
    }

    internal static IDisposable TraceAdditionalWork(TaskbarFlyoutManager manager, string evidence) => new WorkTrace(manager, evidence);

    internal static void CheckRefreshBehavior(Action<string> log)
    {
        const string app = "refresh-test";
        var entry = new TemplateEntryReference("group", TemplateEntryKind.TaskbarFlyout, "flyout");
        var states = new BrokerStateStore([app]);
        var template = new EntryTemplateDeclaration("main", [new("main", new("root", TemplateNodeKind.Vertical, Children:
            [new("text", TemplateNodeKind.Text, Value: new(StateFieldId: "message")),
             new("action", TemplateNodeKind.Button, Value: new(new(TemplateValueKind.Text, Text: "Action")),
                 AccessibleName: "Action", Enabled: new(StateFieldId: "enabled"), Action: new(TemplateActionKind.Business, "run"))]))],
            [new("message", TemplateValueKind.Text), new("enabled", TemplateValueKind.Boolean)]);
        var declaration = new ApplicationDeclaration(app, [new("group", [new("component", [new("primary")])],
            [new("flyout", [new("run")], Template: template)])]);
        ApplicationState State(long revision, string message, bool enabled) => new(revision, [], TemplateEntries:
            [new(entry, [new("message", new(TemplateValueKind.Text, Text: message)), new("enabled", new(TemplateValueKind.Boolean, Boolean: enabled))])]);
        void Connect(string session)
        {
            Check(states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = app, SessionId = session }).Result!.Accepted, "Refresh Welcome failed.");
            var result = states.Handle(new() { Kind = MessageKind.Declare, ApplicationId = app, SessionId = session,
                Declaration = declaration, State = State(0, "before", true) }).Result!;
            Check(result.Accepted, "Refresh declaration failed: " + result.Code + " " + result.Message);
        }
        Connect("first");
        using var images = new RegisteredImageCache();
        TemplateRenderer? renderer = null;
        using var manager = new TaskbarFlyoutManager(states, (key, id, navigate) => renderer = new TemplateRenderer(
            new TemplateInteractionController(states, key.ApplicationId, key.Entry,
                (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), id, navigate), images),
            (kind, detail) => log(kind + ": " + JsonSerializer.Serialize(detail)));
        manager.ReducedMotionOverride = true;
        var reasons = new List<string>();
        manager.ReflowForTesting = (_, reason) => reasons.Add(reason);
        var request = new FlyoutOpenRequest(new(app, entry), "first", "refresh-screen",
            new(-29200, -29300, 40, 20), new(-30000, -30000, 1000, 720), 96);
        Check(manager.Toggle(request).IsSuccess, "Refresh group did not open.");
        var window = manager.WindowForTesting(request.ScreenId)!;
        nint handle = window.Handle;
        var view = renderer!;
        var text = (TextBlock)view.GetNodeElement("text")!;
        var action = (Button)view.GetNodeElement("action")!;
        Check(states.Handle(new() { Kind = MessageKind.State, ApplicationId = app, SessionId = "first",
            State = State(1, "after", false) }).Result!.Accepted, "Changed content was rejected.");
        manager.Refresh(current => current);
        Check(text.Text == "after" && !action.IsEnabled &&
            ReferenceEquals(renderer, view) && ReferenceEquals(manager.WindowForTesting(request.ScreenId), window),
            "Unchanged environment skipped content/availability or replaced native identity.");
        var before = FlyoutNative.Bounds(handle);
        manager.Refresh(current => current with { Anchor = current.Anchor with { X = current.Anchor.X - 100 } });
        Check(FlyoutNative.Bounds(handle).X == before.X - 100, "Merged refresh deferred its new anchor.");
        var narrow = request.WorkArea with { Width = 280 };
        manager.Refresh(current => current with { WorkArea = narrow, Dpi = 144 });
        Check(manager.EnvironmentForHint(request.ScreenId, manager.Inspect().Single().Generation) == (narrow, 144u) &&
            FlyoutNative.Bounds(handle).Width <= 280 - 48, "Merged refresh did not use new work area and DPI in this round.");
        Check(manager.UpdateEnvironment(request.ScreenId, request.Anchor, request.WorkArea, request.Dpi).IsSuccess &&
            FlyoutNative.Bounds(handle) == before, "Standalone environment update lost immediate native layout.");
        log("PASS: unchanged environment refreshes content/availability; merged anchor/workarea/DPI immediate; standalone update immediate; same renderer/HWND.");

        long generation = manager.Inspect().Single().Generation;
        int beforeReservation = reasons.Count;
        Check(manager.ReserveHintSpace(request.ScreenId, generation, 80) && manager.ReserveHintSpace(request.ScreenId, generation, 0) &&
            reasons.Count == beforeReservation + 2 && reasons.TakeLast(2).All(x => x == "hint-reservation"),
            "Independent hint reservation/release lost its necessary reflows.");
        int beforeInvalidation = reasons.Count;
        window.SimulateCloseFailure = true;
        try
        {
            Connect("second");
            manager.Refresh(current => current);
            Check(manager.Inspect() is { Count: 1 } retained && retained[0].Closing && retained[0].Error is not null &&
                manager.HasResources && FlyoutNative.IsWindow(handle) && reasons.Count == beforeInvalidation,
                "Invalid session performed layout before close, or close failure lost ownership/diagnostics.");
            int lateReads = 0;
            manager.Refresh(current => { lateReads++; return current; });
            Check(lateReads == 0 && manager.Inspect().Single().Closing, "Late refresh read the environment or revived a closing group.");
        }
        finally { window.SimulateCloseFailure = false; }
        long oldGeneration = manager.Inspect().Single().Generation;
        Check(manager.CloseScreen(request.ScreenId, oldGeneration).IsSuccess && !FlyoutNative.IsWindow(handle), "Retained close retry failed.");
        int closedReads = 0;
        manager.Refresh(current => { closedReads++; return current; });
        Check(closedReads == 0, "Closed groups read the environment again.");
        Check(manager.Toggle(request with { SessionId = "second" }).IsSuccess, "New-session group failed.");
        try
        {
            Check(!manager.CloseScreen(request.ScreenId, oldGeneration).IsSuccess && manager.Inspect().Single().Generation != oldGeneration,
                "Old generation closed the new group.");
            manager.Refresh(_ => null);
            Check(manager.Inspect().Count == 0 && !manager.HasResources, "Missing origin environment retained the group.");
            log("PASS: invalid session closes before reflow; close failure retains ownership/diagnostics; late refresh inert; old generation rejected; missing origin closes.");
            log("flyout-refresh-behavior-reflows: " + JsonSerializer.Serialize(reasons.GroupBy(x => x).Select(x => new { reason = x.Key, count = x.Count() })));
        }
        finally { foreach (var group in manager.Inspect()) manager.WindowForTesting(group.ScreenId)!.SimulateCloseFailure = false; }
    }

    internal static void CountStableRefreshes(TaskbarFlyoutCoordinator coordinator, string evidence, Action<string> log)
    {
        var manager = coordinator.Manager;
        coordinator.Refresh();
        var group = manager.Inspect().Single();
        var window = manager.WindowForTesting(group.ScreenId)!;
        var foreground = FlyoutNative.GetForegroundWindow();
        var focus = FlyoutNative.CurrentFocus();
        var bounds = FlyoutNative.Bounds(window.Handle);
        var observations = new List<Work>(140);
        int round = -5;
        string reason = "outside-reflow";
        Check(manager.ReflowForTesting is null && TaskbarFlyoutWindow.MeasuredForTesting is null,
            "Layout observation is already owned by another test.");
        manager.ReflowForTesting = (generation, cause) =>
        {
            reason = cause;
            observations.Add(new(round, "reflow", generation, cause, null));
        };
        TaskbarFlyoutWindow.MeasuredForTesting = renderer =>
            observations.Add(new(round, "measure", group.Generation, reason, renderer.CapturedSnapshot?.Template.TemplateId));
        try
        {
            // No dispatcher yield, navigation, new input or hint between these fixed refreshes.
            // Loaded and opening have completed before the caller enters this scope.
            for (; round < 30; round++) coordinator.Refresh();
        }
        finally
        {
            manager.ReflowForTesting = null;
            TaskbarFlyoutWindow.MeasuredForTesting = null;
            File.WriteAllText(Path.Combine(evidence, "refresh-work.json"), JsonSerializer.Serialize(new
            {
                refreshEntry = "TaskbarFlyoutCoordinator.Refresh", warmup = 5, samples = 30,
                group.Generation, group.Entry, window = window.Handle.ToInt64(),
                observations,
                rounds = Enumerable.Range(0, 30).Select(index => new
                {
                    round = index,
                    reflows = observations.Count(x => x.Round == index && x.Operation == "reflow"),
                    measures = observations.Count(x => x.Round == index && x.Operation == "measure"),
                    reasons = observations.Where(x => x.Round == index && x.Operation == "reflow").Select(x => x.Reason).ToArray()
                }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        int reflows = observations.Count(x => x.Round >= 0 && x.Operation == "reflow");
        int measures = observations.Count(x => x.Round >= 0 && x.Operation == "measure");
        log($"flyout-refresh-counts: warmup=5; samples=30; reflows={reflows}; measures={measures}; evidence={evidence}");
        Check(manager.Inspect().Single().Generation == group.Generation &&
            ReferenceEquals(manager.WindowForTesting(group.ScreenId), window) &&
            manager.Inspect().Single().Windows.Select(x => x.Handle).ToHashSet().SetEquals(group.Windows.Select(x => x.Handle)) &&
            window.TemplateId == "panel" && FlyoutNative.Bounds(window.Handle) == bounds &&
            FlyoutNative.GetForegroundWindow() == foreground && FlyoutNative.CurrentFocus() == focus,
            "Stable production refresh changed window, navigation, native bounds or focus identity.");
        // The SDK fixture declares panel plus initially parallel side, even when space folds side away.
        Check(Enumerable.Range(0, 30).All(index => observations.Count(x => x.Round == index && x.Operation == "reflow") == 1 &&
            observations.Count(x => x.Round == index && x.Operation == "measure") == 2 &&
            observations.Count(x => x.Round == index && x.Operation == "measure" && x.TemplateId == "panel") == 1 &&
            observations.Count(x => x.Round == index && x.Operation == "measure" && x.TemplateId == "side") == 1),
            $"Production refresh repeated full layout or measurement: reflows={reflows}, measures={measures}; expected 30/60.");
    }

    private sealed record Work(int Round, string Operation, long Generation, string Reason, string? TemplateId);
    private sealed class WorkTrace : IDisposable
    {
        private readonly TaskbarFlyoutManager manager;
        private readonly string evidence;
        private readonly List<Work> work = [];
        private long generation;
        private string reason = "outside-reflow";
        private bool overflow;
        internal WorkTrace(TaskbarFlyoutManager manager, string evidence)
        {
            this.manager = manager; this.evidence = evidence;
            Check(manager.ReflowForTesting is null && TaskbarFlyoutWindow.MeasuredForTesting is null, "Layout trace is already owned.");
            manager.ReflowForTesting = (value, cause) => { generation = value; reason = cause; Add("reflow", null); };
            TaskbarFlyoutWindow.MeasuredForTesting = view => Add("measure", view.CapturedSnapshot?.Template.TemplateId);
        }
        private void Add(string operation, string? template)
        {
            if (work.Count == 1024) { overflow = true; return; }
            work.Add(new(work.Count, operation, generation, reason, template));
        }
        public void Dispose()
        {
            manager.ReflowForTesting = null; TaskbarFlyoutWindow.MeasuredForTesting = null;
            File.WriteAllText(Path.Combine(evidence, "additional-layout-work.json"), JsonSerializer.Serialize(new
            {
                overflow, work,
                totals = work.GroupBy(x => new { x.Reason, x.Operation }).Select(x => new { x.Key.Reason, x.Key.Operation, count = x.Count() }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            Check(!overflow, "Additional layout trace exceeded its bounded observation budget.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

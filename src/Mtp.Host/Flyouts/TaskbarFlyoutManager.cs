using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Mtp.Contracts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

/// <summary>UI-thread owner of at most one group per screen; no business or request replay queue.</summary>
internal sealed class TaskbarFlyoutManager : IDisposable
{
    private readonly BrokerStateStore states;
    private readonly Func<FlyoutEntryKey, string, Func<TemplateNavigationIntent, ProtocolResult>, TemplateRenderer?> rendererFactory;
    private readonly Action<string, object?> record;
    private readonly Dictionary<string, Group> groups = new(StringComparer.Ordinal);
    private readonly FlyoutInputObserver input;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long generation;
    private bool stopping;
    private HostAppearancePreferences appearance = new();
    internal bool? ReducedMotionOverride { get; set; }
    internal Action? AfterNextFrameAppliedForTesting { get; set; }
    internal event Action<string, long>? PresentedFrameApplied;
    internal string? Error { get; private set; }
    internal bool HasResources => groups.Count > 0 || input.HasResources;
    internal IReadOnlyList<TaskbarFlyoutObservation> Inspect() => groups.Values.Select(group => new TaskbarFlyoutObservation(
        group.Request.Entry, group.Request.SessionId, group.Request.ScreenId, group.Generation,
        group.Layout?.Mode ?? FlyoutGroupMode.Hierarchical, group.Closing, group.Error,
        Array.AsReadOnly(group.Windows.Values.Select(window => new FlyoutWindowObservation(window.Handle, window.TemplateId, window.LastBounds, window.IsClosing)).ToArray()))).ToArray();
    internal TaskbarFlyoutWindow? WindowForTesting(string screenId, string key = "main") => groups.GetValueOrDefault(screenId)?.Windows.GetValueOrDefault(key);

    internal TaskbarDipRect? BoundsForHint(string screenId, long expectedGeneration) =>
        groups.TryGetValue(screenId, out var group) && group.Generation == expectedGeneration && !group.Closing ? group.Layout?.Bounds : null;

    internal (PixelRect WorkArea, uint Dpi)? EnvironmentForHint(string screenId, long expectedGeneration) =>
        groups.TryGetValue(screenId, out var group) && group.Generation == expectedGeneration && !group.Closing
            ? (group.Request.WorkArea, group.Request.Dpi) : null;

    internal TaskbarDipRect? PresentedBoundsForHint(string screenId, long expectedGeneration)
    {
        if (!groups.TryGetValue(screenId, out var group) || group.Generation != expectedGeneration || group.Closing) return null;
        var bounds = group.Windows.Where(x => group.VisibleKeys.Contains(x.Key) && x.Value.LastBounds.IsValid)
            .Select(x => FlyoutNative.ToDip(x.Value.LastBounds, group.Request.Dpi)).ToArray();
        if (bounds.Length == 0) return null;
        double left = bounds.Min(x => x.X), top = bounds.Min(x => x.Y);
        return new(left, top, bounds.Max(x => x.Right) - left, bounds.Max(x => x.Bottom) - top);
    }

    internal bool ReserveHintSpace(string screenId, long expectedGeneration, double heightDip)
    {
        if (!double.IsFinite(heightDip) || heightDip < 0 || heightDip > 512 ||
            !groups.TryGetValue(screenId, out var group) || group.Generation != expectedGeneration || group.Closing) return false;
        if (group.HintReservation == heightDip) return true;
        double previous = group.HintReservation;
        group.HintReservation = heightDip;
        try { Reflow(group, false); return true; }
        catch (Exception error) { group.HintReservation = previous; record("flyout-hint-reflow-failed", error.Message); return false; }
    }

    internal void ApplyAppearance(HostAppearancePreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(value.Theme) || !new MaterialSpec(value.Material, value.Opacity).IsValid)
            throw new ArgumentException("Invalid Host appearance", nameof(value));
        if (appearance == value) return;
        appearance = value;
        foreach (var group in groups.Values)
            foreach (var window in group.Windows.Values) window.ApplyAppearance(value);
    }

    internal TaskbarFlyoutManager(BrokerStateStore states,
        Func<FlyoutEntryKey, string, Func<TemplateNavigationIntent, ProtocolResult>, TemplateRenderer?> rendererFactory,
        Action<string, object?> record)
    {
        this.states = states; this.rendererFactory = rendererFactory;
        // Diagnostics must not change presentation, dispatch, or native resource ownership.
        this.record = (kind, value) => { try { record(kind, value); } catch (Exception) { } };
        input = new(ObserveInput, ObserverFailed);
    }

    internal CoreResult<FlyoutToggleResult> Toggle(FlyoutOpenRequest request)
    {
        if (stopping) return Failure<FlyoutToggleResult>("FlyoutClosed", "浮窗管理器正在关闭");
        var declaration = Resolve(request);
        if (!declaration.IsSuccess) return CoreResult<FlyoutToggleResult>.Failure(declaration.Error!);
        if (groups.TryGetValue(request.ScreenId, out var existing))
        {
            if (existing.Request.Entry == request.Entry && existing.Request.SessionId == request.SessionId)
            {
                existing.Pending = null;
                var closed = BeginClose(existing);
                if (!closed.IsSuccess) return CoreResult<FlyoutToggleResult>.Failure(closed.Error!);
                return CoreResult<FlyoutToggleResult>.Success(FlyoutToggleResult.Closed);
            }
            if (existing.Pending is not null) record("flyout-replacement-superseded", existing.Request.ScreenId);
            existing.Pending = request; // exactly one latest replacement, not an unbounded action queue
            var replacing = BeginClose(existing);
            if (!replacing.IsSuccess) return CoreResult<FlyoutToggleResult>.Failure(replacing.Error!);
            return CoreResult<FlyoutToggleResult>.Success(FlyoutToggleResult.Replacing);
        }
        if (groups.Count >= ItemPresentationLimits.MaximumScreens) return Failure<FlyoutToggleResult>("FlyoutCapacityExceeded", "浮窗屏幕数超过预算");
        var started = input.TryStart();
        if (!started.IsSuccess) return CoreResult<FlyoutToggleResult>.Failure(started.Error!);
        var group = new Group(request, checked(++generation), declaration.Value!);
        groups.Add(request.ScreenId, group);
        group.Timer.Tick += (_, _) => Advance(group);
        try
        {
            group.Path.Add(new(request.InitialTemplateId ?? group.Declaration.MainTemplateId, null));
            // Parallel preference is honored at initial presentation; all declared templates remain reachable.
            foreach (var panel in group.Declaration.Panels ?? [])
                if (panel.Presentation == PanelPresentation.Parallel && group.Path.All(x => x.Id != panel.TemplateId)) group.Parallel.Add(panel.TemplateId);
            Reflow(group, true);
            if (!Current(group) || group.Error is not null) return Failure<FlyoutToggleResult>("FlyoutCreateFailed", group.Error ?? "浮窗初始化未完成");
            record("flyout-opened", new { request.Entry, request.ScreenId, group.Generation, request.Invocation,
                foreground = FlyoutNative.GetForegroundWindow().ToInt64(),
                windows = group.Windows.Values.Select(x => x.Handle.ToInt64()).ToArray() });
            return CoreResult<FlyoutToggleResult>.Success(FlyoutToggleResult.Opened);
        }
        catch (Exception error)
        {
            record("flyout-create-failed", new { group.Generation, elapsedMs = clock.Elapsed.TotalMilliseconds,
                type = error.GetType().FullName, error.HResult, error.StackTrace });
            group.Error = string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
            group.Closing = true;
            var cleanup = FinishClose(group);
            return Failure<FlyoutToggleResult>(cleanup.IsSuccess ? "FlyoutCreateFailed" : "FlyoutCleanupPending", group.Error);
        }
    }
    internal void Refresh()
    {
        if (input.Error is { } observerError && groups.Count > 0)
        {
            Error = observerError;
            foreach (var affected in groups.Values.ToArray()) CloseScreen(affected.Request.ScreenId, affected.Generation);
            return;
        }
        foreach (var group in groups.Values.ToArray())
        {
            if (group.Closing) continue;
            var current = Resolve(group.Request);
            if (!current.IsSuccess || !ReferenceEquals(current.Value, group.Declaration)) { CloseScreen(group.Request.ScreenId, group.Generation); continue; }
            try { Reflow(group, false); }
            catch (Exception error) { group.Error = error.Message; record("flyout-refresh-failed", error.Message); CloseScreen(group.Request.ScreenId, group.Generation); }
        }
    }
    internal CoreResult<bool> UpdateEnvironment(string screenId, PixelRect anchor, PixelRect workArea, uint dpi)
    {
        if (!groups.TryGetValue(screenId, out var group)) return CoreResult<bool>.Success(true);
        var next = group.Request with { Anchor = anchor, WorkArea = workArea, Dpi = dpi };
        var valid = Resolve(next);
        if (!valid.IsSuccess) { CloseScreen(screenId, group.Generation); return CoreResult<bool>.Failure(valid.Error!); }
        group.Request = next;
        try { Reflow(group, false); return CoreResult<bool>.Success(true); }
        catch (Exception error) { group.Error = error.Message; CloseScreen(screenId, group.Generation); return Failure<bool>("FlyoutLayoutFailed", error.Message); }
    }
    internal CoreResult<bool> EnterKeyboard(string screenId, long expectedGeneration)
    {
        if (!groups.TryGetValue(screenId, out var group) || group.Generation != expectedGeneration || group.Closing)
            return Failure<bool>("StaleFlyout", "浮窗组已更换");
        group.Keyboard = false; group.Windows.GetValueOrDefault("main")?.EnterKeyboard();
        return CoreResult<bool>.Success(true);
    }
    internal CoreResult<bool> CloseEntry(FlyoutEntryKey entry, string expectedSessionId)
    {
        CoreResult<bool> result = CoreResult<bool>.Success(true);
        foreach (var group in groups.Values.Where(x => x.Request.Entry == entry && x.Request.SessionId == expectedSessionId).ToArray())
        { var closed = CloseScreen(group.Request.ScreenId, group.Generation); if (!closed.IsSuccess) result = closed; }
        return result;
    }
    internal CoreResult<bool> CloseScreen(string screenId, long expectedGeneration)
    {
        if (!groups.TryGetValue(screenId, out var group)) return CoreResult<bool>.Success(true);
        if (group.Generation != expectedGeneration) return Failure<bool>("StaleFlyout", "旧代关闭请求已拒绝");
        if (!group.Closing) record("flyout-explicit-close", new { group.Generation, elapsedMs = clock.Elapsed.TotalMilliseconds });
        group.Pending = null; group.Closing = true; group.Timer.Stop(); group.Animation.Clear();
        foreach (var renderer in group.Renderers.Values) renderer.StopInteraction();
        return FinishClose(group);
    }
    internal CoreResult<bool> AssociateInteractionWindow(string screenId, long expectedGeneration, nint window, bool add)
    {
        if (!groups.TryGetValue(screenId, out var group) || group.Generation != expectedGeneration || group.Closing)
            return Failure<bool>("StaleFlyout", "浮窗组已更换");
        if (!add) { group.Associated.Remove(window); return CoreResult<bool>.Success(true); }
        if (window == 0 || !FlyoutNative.IsWindow(window) || group.Associated.Count >= 16 ||
            FlyoutNative.GetWindowThreadProcessId(window, out var pid) == 0 || pid != Environment.ProcessId)
            return Failure<bool>("InvalidInteractionWindow", "关联窗口必须由当前Host拥有且在预算内");
        group.Associated.Add(window); return CoreResult<bool>.Success(true);
    }
    internal CoreResult<bool> TryClose()
    {
        stopping = true;
        CoreResult<bool> result = CoreResult<bool>.Success(true);
        foreach (var group in groups.Values.ToArray())
        { var closed = CloseScreen(group.Request.ScreenId, group.Generation); if (!closed.IsSuccess) result = closed; }
        if (groups.Count == 0) { var stopped = input.TryStop(); if (!stopped.IsSuccess) result = stopped; }
        return result;
    }
    public void Dispose()
    {
        var closed = TryClose();
        if (!closed.IsSuccess) throw new InvalidOperationException(closed.Error!.Message);
    }

    private CoreResult<EntryTemplateDeclaration> Resolve(FlyoutOpenRequest request)
    {
        if (request is null || request.Entry is null || string.IsNullOrWhiteSpace(request.Entry.ApplicationId) || request.Entry.ApplicationId.Length > 256 ||
            request.Entry.Entry is null || request.Entry.Entry.Kind is not (TemplateEntryKind.Component or TemplateEntryKind.TaskbarFlyout) ||
            string.IsNullOrWhiteSpace(request.Entry.Entry.FeatureGroupId) || request.Entry.Entry.FeatureGroupId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.Entry.Entry.EntryId) || request.Entry.Entry.EntryId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.ScreenId) || request.ScreenId.Length > 256 || !request.Anchor.IsValid || !request.WorkArea.IsValid || request.Dpi == 0 ||
            !Enum.IsDefined(request.Invocation)) return Failure<EntryTemplateDeclaration>("InvalidFlyoutRequest", "浮窗归属或定位参数无效");
        try
        {
            if (request.IsOriginAvailable?.Invoke() == false)
                return Failure<EntryTemplateDeclaration>("AnchorUnavailable", "浮窗来源已隐藏或失效");
        }
        catch (Exception) { return Failure<EntryTemplateDeclaration>("AnchorUnavailable", "浮窗来源无法确认"); }
        var state = states.GetSnapshot(request.Entry.ApplicationId);
        if (state is null || state.SessionId != request.SessionId || !state.IsConnected || !state.IsInteractive) return Failure<EntryTemplateDeclaration>("StaleSession", "浮窗会话不可交互");
        if (request.Entry.Entry.Kind == TemplateEntryKind.TaskbarFlyout &&
            !states.FlyoutRequests.IsEntryEnabled(request.Entry.ApplicationId, request.SessionId,
                request.Entry.Entry.FeatureGroupId, request.Entry.Entry.EntryId))
            return Failure<EntryTemplateDeclaration>("EntryDisabled", "浮窗入口显示已由Host关闭");
        var declaration = state.Declaration?.Templates.FirstOrDefault(x => x.Entry == request.Entry.Entry)?.Declaration;
        if (declaration is null || state.State?.TemplateEntries?.Any(x => x.Entry == request.Entry.Entry) != true)
            return Failure<EntryTemplateDeclaration>("TemplateUnavailable", "浮窗模板尚未确认");
        if (request.InitialTemplateId is { } initial && initial != declaration.MainTemplateId && declaration.Panels?.Any(x => x.TemplateId == initial) != true)
            return Failure<EntryTemplateDeclaration>("UnknownPanel", "入口面板未声明");
        return CoreResult<EntryTemplateDeclaration>.Success(declaration);
    }
    private TemplateRenderer Renderer(Group group, string templateId)
    {
        if (group.Renderers.TryGetValue(templateId, out var existing)) return existing;
        if (group.Renderers.Count >= TemplateLimits.TemplatesPerEntry) throw new InvalidOperationException("浮窗模板预算已满");
        var renderer = rendererFactory(group.Request.Entry, templateId, intent => Navigate(group, intent)) ?? throw new InvalidOperationException("模板渲染器不可用");
        group.Renderers.Add(templateId, renderer); return renderer;
    }
    private ProtocolResult Navigate(Group group, TemplateNavigationIntent intent)
    {
        if (!Current(group) || group.Closing || intent.ApplicationId != group.Request.Entry.ApplicationId || intent.Entry != group.Request.Entry.Entry ||
            intent.SessionId != group.Request.SessionId || group.Layout?.VisiblePanels.Any(x => x.TemplateId == intent.SourceTemplateId) != true ||
            !group.Renderers.TryGetValue(intent.SourceTemplateId, out var source) || !source.IsCurrentNavigation(intent))
            return ProtocolResult.Reject("StaleFlyout", "导航来源已失效");
        return Navigate(group, intent.Action.Kind == TemplateActionKind.Back ? null : intent.Action.TargetId, intent.SourceNodeId);
    }
    private ProtocolResult Navigate(Group group, string? templateId, string? sourceNode = null)
    {
        if (!Current(group) || group.Closing) return ProtocolResult.Reject("StaleFlyout", "浮窗已关闭");
        group.Keyboard |= FlyoutNative.BelongsTo(FlyoutNative.GetForegroundWindow(), group.Windows.Values.Select(x => x.Handle));
        if (templateId is null)
        {
            if (group.Path.Count <= 1) return ProtocolResult.Reject("NoPreviousPanel", "已处于主面板");
            group.FocusNode = group.Path[^1].SourceNode;
            group.Path.RemoveAt(group.Path.Count - 1);
        }
        else
        {
            if (group.Declaration.Panels?.Any(x => x.TemplateId == templateId) != true) return ProtocolResult.Reject("UnknownPanel", "面板未声明");
            var existing = group.Path.FindIndex(x => x.Id == templateId);
            if (existing >= 0) group.Path.RemoveRange(existing + 1, group.Path.Count - existing - 1);
            else if (group.Path.Count < TemplateLimits.TemplatesPerEntry) group.Path.Add(new(templateId, sourceNode));
            else return ProtocolResult.Reject("PanelDepthExceeded", "导航路径预算已满");
            group.FocusNode = null;
        }
        try { Reflow(group, true); return ProtocolResult.Success(); }
        catch (Exception error) { group.Error = error.Message; CloseScreen(group.Request.ScreenId, group.Generation); return ProtocolResult.Reject("FlyoutLayoutFailed", error.Message); }
    }
    private void Reflow(Group group, bool forceTransition)
    {
        var requested = group.Path.Select(x => x.Id).Concat(group.Parallel).Distinct(StringComparer.Ordinal).ToArray();
        var panels = requested.Select(id => new FlyoutPanelMeasure(id, TaskbarFlyoutWindow.Measure(Renderer(group, id), 320),
            group.Declaration.Panels?.FirstOrDefault(x => x.TemplateId == id)?.Presentation == PanelPresentation.Parallel)).ToArray();
        var work = FlyoutNative.ToDip(group.Request.WorkArea, group.Request.Dpi);
        if (group.HintReservation > 0) work = work with { Y = work.Y + group.HintReservation, Height = work.Height - group.HintReservation };
        var layout = FlyoutGroupLayout.Calculate(work,
            FlyoutNative.ToDip(group.Request.Anchor, group.Request.Dpi), panels, group.Path[^1].Id);
        if (!layout.IsSuccess) throw new InvalidOperationException(layout.Error!.Message);
        group.Layout = layout.Value!;
        var targets = new List<FlyoutRectangleTarget>();
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in group.Layout.VisiblePanels)
        {
            var key = group.Layout.Mode == FlyoutGroupMode.Parallel && panel.TemplateId != requested[0]
                ? "panel:" + Array.FindIndex(group.Declaration.Templates.ToArray(), x => x.TemplateId == panel.TemplateId) : "main";
            needed.Add(key);
            if (!group.Windows.TryGetValue(key, out var window))
            {
                window = new(() => BeginClose(group), () => Navigate(group, (string?)null), id => Navigate(group, id), () => WindowClosed(group, key), record);
                group.Windows.Add(key, window); // own before initialization; retain even when initialization/cleanup fails
                window.ApplyAppearance(appearance);
                window.Initialize();
            }
            window.RefreshAppearance();
            var renderer = Renderer(group, panel.TemplateId);
            foreach (var other in group.Windows.Values.Where(x => !ReferenceEquals(x, window))) other.DetachRenderer(renderer);
            forceTransition |= window.SetRenderer(panel.TemplateId, renderer, group.Path.Count > 1,
                group.Declaration.Panels?.Select(x => x.TemplateId) ?? []);
            targets.Add(new(key, panel.Bounds));
        }
        var previous = group.LastAppliedFrame ?? group.Animation.Sample(clock.Elapsed);
        foreach (var pair in group.Windows.Where(x => !needed.Contains(x.Key)))
        {
            pair.Value.DisableInput();
            var prior = previous.Panels.FirstOrDefault(x => x.Key == pair.Key);
            if (prior is not null) targets.Add(prior with { Opacity = 0 });
        }
        if (targets.Count > TemplateLimits.TemplatesPerEntry) throw new InvalidOperationException("浮窗窗口预算已满");
        group.VisibleKeys = needed;
        group.AnimationTargets = targets;
        long revision = group.Animation.Revision;
        group.Animation.Retarget(targets, clock.Elapsed, ReduceMotion(), group.Animation.Generation, forceTransition,
            presentedFrame: group.LastAppliedFrame);
        if (group.Animation.Revision != revision)
            foreach (var window in group.Windows.Values) window.RetargetContent();
        Advance(group);
    }
    private bool ReduceMotion()
    {
        if (ReducedMotionOverride is bool forced) return forced;
        try { return !uiSettings.AnimationsEnabled; } catch (System.Runtime.InteropServices.COMException) { return true; }
    }
    private void Advance(Group group)
    {
        if (!Current(group)) { group.Timer.Stop(); return; }
        try
        {
            var frame = group.Animation.Sample(clock.Elapsed);
            if (ReduceMotion() && !frame.IsComplete)
            {
                var target = group.AnimationTargets;
                if (target is not null) group.Animation.Retarget(target, clock.Elapsed, true, group.Animation.Generation);
                frame = group.Animation.Sample(clock.Elapsed);
            }
            foreach (var panel in frame.Panels)
                group.Windows.GetValueOrDefault(panel.Key)?.Apply(panel, frame.Progress, group.Request.Dpi, group.Closing || !group.VisibleKeys.Contains(panel.Key));
            group.LastAppliedFrame = frame;
            if (PresentedFrameApplied is { } listeners)
                foreach (Action<string, long> listener in listeners.GetInvocationList())
                    try { listener(group.Request.ScreenId, group.Generation); } catch (Exception error) { record("flyout-associated-frame-failed", error.Message); }
            var afterApplied = AfterNextFrameAppliedForTesting;
            AfterNextFrameAppliedForTesting = null;
            afterApplied?.Invoke();
            // Applying native geometry may cross the deadline. Schedule from the frame actually applied,
            // so an unrendered later sample cannot prevent the terminal frame from reaching the windows.
            if (!frame.IsComplete) { group.Timer.Start(); return; }
            group.Timer.Stop();
            if (group.Closing) { FinishClose(group); return; }
            foreach (var key in group.Windows.Keys.Where(key => !group.VisibleKeys.Contains(key)).ToArray())
            {
                var removed = group.Windows[key].TryClose();
                if (!removed.IsSuccess) throw new InvalidOperationException(removed.Error!.Message);
                group.Windows.Remove(key);
            }
            if (group.Keyboard)
            {
                var focusWindow = group.Windows.Values.FirstOrDefault(x => x.TemplateId == group.Path[^1].Id);
                focusWindow?.EnterKeyboard(group.FocusNode); group.FocusNode = null; group.Keyboard = false;
            }
        }
        catch (Exception error)
        {
            group.Timer.Stop(); group.Error = error.Message; record("flyout-animation-failed", new
                { group.Generation, elapsedMs = clock.Elapsed.TotalMilliseconds, type = error.GetType().Name, error.Message });
            CloseScreen(group.Request.ScreenId, group.Generation);
        }
    }
    private CoreResult<bool> BeginClose(Group group)
    {
        if (!Current(group)) return CoreResult<bool>.Success(true);
        if (group.Closing) return group.Error is null ? CoreResult<bool>.Success(true) : Failure<bool>("FlyoutCleanupPending", group.Error);
        group.Closing = true;
        foreach (var window in group.Windows.Values) window.DisableInput();
        foreach (var renderer in group.Renderers.Values) renderer.StopInteraction();
        var frame = group.LastAppliedFrame ?? group.Animation.Sample(clock.Elapsed);
        group.AnimationTargets = frame.Panels.Select(x => x with { Bounds = x.Bounds with { Y = x.Bounds.Y + 8 }, Opacity = 0 }).ToArray();
        group.Animation.Retarget(group.AnimationTargets,
            clock.Elapsed, ReduceMotion(), group.Animation.Generation, presentedFrame: group.LastAppliedFrame);
        Advance(group);
        return group.Error is null ? CoreResult<bool>.Success(true) : Failure<bool>("FlyoutCleanupPending", group.Error);
    }
    private CoreResult<bool> FinishClose(Group group)
    {
        group.Timer.Stop();
        foreach (var pair in group.Windows.ToArray())
        {
            var closed = pair.Value.TryClose();
            if (!closed.IsSuccess) { group.Error = closed.Error!.Message; return closed; }
            group.Windows.Remove(pair.Key);
        }
        foreach (var pair in group.Renderers.ToArray())
        {
            try { pair.Value.Dispose(); group.Renderers.Remove(pair.Key); }
            catch (Exception error) { group.Error = error.Message; return Failure<bool>("FlyoutCleanupPending", error.Message); }
        }
        group.Animation.Clear(); group.Associated.Clear();
        groups.Remove(group.Request.ScreenId);
        record("flyout-closed", new { group.Request.ScreenId, group.Generation });
        if (!stopping && group.Pending is { } next) { var opened = Toggle(next); if (!opened.IsSuccess) Error = opened.Error!.Message; }
        if (groups.Count == 0) return input.TryStop();
        return CoreResult<bool>.Success(true);
    }
    private void WindowClosed(Group group, string key)
    {
        if (!group.Closing && !group.VisibleKeys.Contains(key)) return;
        if (!Current(group) || group.FinishQueued) return;
        record("flyout-window-closed", new { group.Generation, key, group.Closing, elapsedMs = clock.Elapsed.TotalMilliseconds });
        group.FinishQueued = true;
        dispatcher.TryEnqueue(() =>
        {
            group.FinishQueued = false;
            if (!Current(group)) return;
            if (group.Closing) FinishClose(group); else BeginClose(group);
        });
    }
    private void ObserveInput(FlyoutObservedInput observed)
    {
        if (observed.Generation != input.Generation) return;
        foreach (var group in groups.Values.ToArray())
        {
            if (group.Closing || !FlyoutNative.IsAfter(observed.MessageTime, group.OpenedAt)) continue;
            var previousForeground = group.LastForeground;
            var previousFocus = group.LastFocus;
            bool inside = FlyoutNative.BelongsTo(observed.Window, group.Windows.Values.Select(x => x.Handle).Concat(group.Associated));
            void CloseFromInput()
            {
                if (group.Closing) return;
                record("flyout-input-close", new { kind = observed.Kind.ToString(), time = observed.MessageTime,
                    openedAt = group.OpenedAt, observedHWND = observed.Window.ToInt64(),
                    foregroundBaseline = previousForeground.ToInt64(), focusBaseline = previousFocus.ToInt64(), inside,
                    observedRoot = FlyoutNative.Root(observed.Window).ToInt64(),
                    currentForeground = FlyoutNative.GetForegroundWindow().ToInt64(),
                    ownedWindows = group.Windows.Values.Select(x => x.Handle.ToInt64()).ToArray(),
                    triggerWindow = group.Request.TriggerWindow.ToInt64(),
                    triggerStamp = group.Request.TriggerMessageTime, group.Generation, elapsedMs = clock.Elapsed.TotalMilliseconds });
                BeginClose(group);
            }
            if (observed.Kind == FlyoutInputKind.MouseDown)
            {
                if (group.Request.TriggerMessageTime is { } trigger && !FlyoutNative.IsAfter(observed.MessageTime, trigger)) continue;
                if (FlyoutNative.Contains(group.Request.Anchor, observed.Point) && group.Request.TriggerWindow != 0 &&
                    FlyoutNative.Root(observed.Window) == FlyoutNative.Root(group.Request.TriggerWindow)) continue;
                if (!inside) CloseFromInput();
            }
            else if (observed.Kind == FlyoutInputKind.Foreground)
            {
                if (observed.Window == group.LastForeground) continue;
                group.LastForeground = observed.Window; if (!inside) CloseFromInput();
            }
            else
            {
                if (observed.Window == group.LastFocus) continue;
                group.LastFocus = observed.Window; if (!inside) CloseFromInput();
            }
        }
    }
    private void ObserverFailed(string message)
    {
        Error = message;
        record("flyout-input-failed", message);
        // Deliver failure after the current native callback has unwound; one close attempt per group.
        var affected = groups.Values.Select(x => (x.Request.ScreenId, x.Generation)).ToArray();
        dispatcher.TryEnqueue(() => { foreach (var item in affected) CloseScreen(item.ScreenId, item.Generation); });
    }
    private bool Current(Group group) => groups.TryGetValue(group.Request.ScreenId, out var current) && ReferenceEquals(current, group);
    private static CoreResult<T> Failure<T>(string code, string message) => CoreResult<T>.Failure(new(code,
        string.IsNullOrWhiteSpace(message) ? code : message));
    private sealed record Navigation(string Id, string? SourceNode);
    private sealed class Group(FlyoutOpenRequest request, long generation, EntryTemplateDeclaration declaration)
    {
        internal FlyoutOpenRequest Request = request;
        internal readonly long Generation = generation;
        internal readonly EntryTemplateDeclaration Declaration = declaration;
        internal readonly uint OpenedAt = unchecked((uint)Environment.TickCount);
        internal nint LastForeground = FlyoutNative.GetForegroundWindow(), LastFocus = FlyoutNative.CurrentFocus();
        internal readonly List<Navigation> Path = [];
        internal readonly List<string> Parallel = [];
        internal readonly Dictionary<string, TemplateRenderer> Renderers = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, TaskbarFlyoutWindow> Windows = new(StringComparer.Ordinal);
        internal readonly HashSet<nint> Associated = [];
        internal readonly FlyoutRectangleAnimation Animation = new();
        internal FlyoutRectangleFrame? LastAppliedFrame;
        internal readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        internal FlyoutGroupLayoutResult? Layout;
        internal double HintReservation;
        internal HashSet<string> VisibleKeys = [];
        internal IReadOnlyList<FlyoutRectangleTarget>? AnimationTargets;
        internal FlyoutOpenRequest? Pending;
        internal string? Error, FocusNode;
        internal bool Closing, FinishQueued, Keyboard = request.Invocation == FlyoutInvocationKind.Keyboard;
    }
}

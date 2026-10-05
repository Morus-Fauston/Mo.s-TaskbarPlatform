using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Mtp.Contracts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

internal sealed record EventGroupOpenRequest(FlyoutEntryKey Entry, string SessionId, string ScreenId,
    PixelRect WorkArea, uint Dpi, FlyoutPosition Position = FlyoutPosition.Default);
internal sealed record EventGroupHostObservation(EventGroupIdentity Identity, FlyoutEntryKey Entry, bool Closing,
    bool CleanupPending, bool Protected, string? Error, FlyoutGroupMode Mode, IReadOnlyList<FlyoutWindowObservation> Windows);

/// <summary>UI-thread owner of event resources; schedule slots end only after verified native destruction.</summary>
internal sealed class EventGroupManager : IDisposable
{
    private readonly BrokerStateStore states;
    private readonly RegisteredImageCache images;
    private readonly TemplateActionSender sender;
    private readonly Action<string, object?> record;
    private readonly EventGroupSchedule schedule;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly Dictionary<EventGroupIdentity, Group> groups = [];
    private readonly DispatcherTimer timer = new();
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Windows.UI.ViewManagement.UISettings ui = new();
    private HostSettingsPreferences preferences = new(new(), []);
    private HostAppearancePreferences appearance = new();
    private bool stopping, ticking, reflowing, reflowQueued;
    private int diagnosticCount;
    internal bool? ReducedMotionOverride { get; set; }
    internal Func<string, IReadOnlyList<PixelRect>> HigherPriorityBounds { get; set; } = _ => [];
    internal event Action? FrameApplied;
    internal event Action<HintOwner>? PresentedFrameApplied;
    internal bool HasResources => groups.Count > 0;
    private TimeSpan Now => clock.GetElapsedTime(started, clock.GetTimestamp());
    internal EventGroupManager(BrokerStateStore states, RegisteredImageCache images, TemplateActionSender sender,
        Action<string, object?> record, TimeProvider? clock = null)
    {
        this.states = states; this.images = images; this.sender = sender;
        this.clock = clock ?? TimeProvider.System; started = this.clock.GetTimestamp();
        schedule = new(clock: this.clock);
        this.record = (kind, value) => { if (diagnosticCount >= 4096) return; diagnosticCount++; try { record(kind, value); } catch (Exception) { } };
        timer.Interval = TimeSpan.FromMilliseconds(16); timer.Tick += Tick;
    }
    internal IReadOnlyList<EventGroupHostObservation> Inspect()
    {
        var observations = schedule.Snapshot().ToDictionary(value => value.Identity);
        return groups.Values.Select(group => new EventGroupHostObservation(group.Identity, group.Request.Entry,
            group.Closing, group.CleanupPending, observations.GetValueOrDefault(group.Identity)?.Protected == true,
            group.Error, group.Layout?.Mode ?? FlyoutGroupMode.Hierarchical,
            group.Windows.Values.Select(window => new FlyoutWindowObservation(window.Handle, window.TemplateId,
                window.LastBounds, window.IsClosing)).ToArray())).ToArray();
    }
    internal TaskbarFlyoutWindow? WindowForTesting(EventGroupIdentity identity, string key = "main") =>
        groups.GetValueOrDefault(identity)?.Windows.GetValueOrDefault(key);

    private Group? HintGroup(HintOwner owner)
    {
        if (owner?.Entry?.Entry is not { Kind: TemplateEntryKind.EventChannel }) return null;
        var identity = new EventGroupIdentity(Key(owner.Entry), owner.ScreenId, owner.SessionId, owner.Generation);
        return groups.TryGetValue(identity, out var group) && group.Committed && !group.Closing && !group.CleanupPending &&
            group.Request.Entry == owner.Entry ? group : null;
    }
    internal TaskbarDipRect? BoundsForHint(HintOwner owner) => HintGroup(owner)?.TargetBounds;
    internal (PixelRect WorkArea, uint Dpi)? EnvironmentForHint(HintOwner owner) =>
        HintGroup(owner) is { } group ? (group.Request.WorkArea, group.Request.Dpi) : null;
    internal TaskbarDipRect? PresentedBoundsForHint(HintOwner owner)
    {
        if (HintGroup(owner) is not { } group) return null;
        var bounds = group.Windows.Where(value => group.Visible.Contains(value.Key) && value.Value.LastBounds.IsValid)
            .Select(value => FlyoutNative.ToDip(value.Value.LastBounds, group.Request.Dpi)).ToArray();
        if (bounds.Length == 0) return null;
        double left = bounds.Min(value => value.X), top = bounds.Min(value => value.Y);
        return new(left, top, bounds.Max(value => value.Right) - left, bounds.Max(value => value.Bottom) - top);
    }
    internal bool ReserveHintSpace(HintOwner owner, double heightDip, string source = "ordinary")
    {
        if (!double.IsFinite(heightDip) || heightDip < 0 || heightDip > 512 ||
            string.IsNullOrWhiteSpace(source) || source.Length > 256 || HintGroup(owner) is not { } group ||
            group.BaseBounds is null || group.BaseLayout is null ||
            !group.HintReservations.ContainsKey(source) && group.HintReservations.Count >= 65) return false;
        double previousSource = group.HintReservations.GetValueOrDefault(source);
        double previous = group.HintReservation;
        if (heightDip == 0) group.HintReservations.Remove(source); else group.HintReservations[source] = heightDip;
        group.HintReservation = group.HintReservations.Values.DefaultIfEmpty().Max();
        if (group.HintReservation == previous) return true;
        bool applying = false;
        try
        {
            // Keep the scheduler's occupied rectangle unchanged. Only this owner's content
            // yields height; an associated hint must not rearrange unrelated event groups.
            var layout = ReservedLayout(group, group.BaseBounds.Value);
            applying = true;
            group.Layout = layout;
            PrepareWindows(group, group.HintReservation > 0 ? layout.Bounds : group.BaseBounds.Value, false);
            Advance(group); Drive();
            return HintGroup(owner) is not null;
        }
        catch (Exception error)
        {
            if (previousSource == 0) group.HintReservations.Remove(source); else group.HintReservations[source] = previousSource;
            group.HintReservation = previous;
            record("event-hint-reflow-failed", new { group.Identity, error.Message });
            if (applying) Fail(group, error);
            return false;
        }
    }

    private static FlyoutGroupLayoutResult ReservedLayout(Group group, TaskbarDipRect occupied)
    {
        if (group.HintReservation == 0) return group.BaseLayout!;
        double height = occupied.Height - group.HintReservation;
        if (height < FlyoutGroupLayout.MinimumHeightDip)
            throw new InvalidOperationException("预留提示后无法保留事件组必要操作区域");
        double margin = FlyoutGroupLayout.SafetyMarginDip;
        var localWork = new TaskbarDipRect(occupied.X - margin, occupied.Y + group.HintReservation - margin,
            occupied.Width + margin * 2, height + margin * 2);
        var layout = FlyoutGroupLayout.Calculate(localWork,
            new(occupied.Right - 1, occupied.Bottom + FlyoutGroupLayout.GapDip, 1, 1), group.Measures, group.Path[^1].Id);
        if (!layout.IsSuccess) throw new InvalidOperationException(layout.Error!.Message);
        return layout.Value!;
    }

    internal ProtocolResult Show(EventGroupOpenRequest request)
    {
        if (stopping) return ProtocolResult.Reject("EventManagerClosed", "事件窗口调度已停止");
        EventGroupProposal? proposal = null;
        Group? created = null;
        try
        {
            var resolved = Resolve(request);
            if (!resolved.IsSuccess) return Reject(resolved.Error!);
            string key = Key(request.Entry);
            var existing = groups.Values.FirstOrDefault(value => value.Identity.Key == key);
            if (existing is not null && (existing.Identity.SessionId != request.SessionId ||
                !ReferenceEquals(existing.Declaration, resolved.Value!.Template)))
            {
                var closed = FinishClose(existing);
                if (!closed.IsSuccess) return Reject(closed.Error!);
                existing = null;
            }
            string screen = existing?.Identity.ScreenId ?? request.ScreenId;
            PollInteractions(screen);
            if (groups.Values.Any(value => value.Identity.ScreenId == screen && value.CleanupPending))
                return ProtocolResult.Reject("EventCleanupPending", "本屏尚有原生资源等待清理");
            var prepared = schedule.Prepare(new(key, screen, request.SessionId,
                resolved.Value!.Entry.ClosePolicy == EventClosePolicy.Persistent));
            if (!prepared.IsSuccess) return Reject(prepared.Error!);
            proposal = prepared.Value!;
            if (proposal.Kind == EventGroupProposalKind.Refresh)
            {
                if (existing is null || existing.Closing) return ProtocolResult.Reject("StaleEvent", "事件窗口已经结束");
                // Trigger movement cannot move a live channel to another screen or change its first order.
                existing.Request = existing.Request with { Position = request.Position };
                ReflowScreen(screen);
                if (!Current(existing) || existing.CleanupPending) return ProtocolResult.Reject("EventRefreshFailed", "事件内容刷新失败");
                var committed = schedule.Commit(proposal);
                if (!committed.IsSuccess) return Reject(committed.Error!);
                record("event-refreshed", existing.Identity);
                Drive(); return ProtocolResult.Success("Displayed");
            }
            if (proposal.Victim is { } victim)
            {
                var eviction = schedule.BeginEviction(proposal);
                if (!eviction.IsSuccess) return Reject(eviction.Error!);
                if (!groups.TryGetValue(victim, out var owner)) return ProtocolResult.Reject("EventOwnerMissing", "待替换事件资源所有者缺失");
                var closed = FinishClose(owner);
                if (!closed.IsSuccess) return Reject(closed.Error!);
            }
            if (groups.Count >= EventGroupSchedule.MaximumScreens * EventGroupSchedule.MaximumGroupsPerScreen)
                return ProtocolResult.Reject("EventCapacityExceeded", "事件资源预算已满");
            created = new(request, proposal.Identity, resolved.Value!.Template);
            created.Path.Add(new(created.Declaration.MainTemplateId, null));
            created.Parallel.AddRange((created.Declaration.Panels ?? []).Where(value => value.Presentation == PanelPresentation.Parallel).Select(value => value.TemplateId));
            created.Interaction = new((source, active) => Current(created) && !created.Closing && schedule.SetInteraction(created.Identity, source, active));
            groups.Add(created.Identity, created); // Own before constructing any native window.
            ReflowScreen(screen, created, apply: false);
            if (created.CleanupPending || !Current(created)) throw new InvalidOperationException("事件窗口创建失败");
            var commit = schedule.Commit(proposal);
            if (!commit.IsSuccess) throw new InvalidOperationException(commit.Error!.Message);
            created.Committed = true;
            Advance(created);
            if (created.CleanupPending || !Current(created)) return ProtocolResult.Reject("EventPresentationFailed", "事件窗口未能呈现");
            record("event-created", created.Identity); Drive(); return ProtocolResult.Success("Displayed");
        }
        catch (Exception error)
        {
            if (created is not null && Current(created)) Fail(created, error);
            record("event-show-failed", new { type = error.GetType().Name, error.Message });
            return ProtocolResult.Reject("EventPresentationFailed", "事件窗口未能完成显示");
        }
        finally { if (proposal is not null) schedule.Cancel(proposal); Drive(); }
    }

    internal void ApplySettings(HostSettingsPreferences value)
    {
        preferences = value;
        var limit = schedule.SetLimit((value.Events ?? new()).MaximumGroupsPerScreen);
        if (!limit.IsSuccess) { record("event-settings-rejected", limit.Error); return; }
        foreach (var group in groups.Values.ToArray())
            if (!group.CleanupPending && !Enabled(group.Request)) FinishClose(group);
        Converge(); ReflowAll(); Drive();
    }
    internal void ApplyAppearance(HostAppearancePreferences value)
    {
        appearance = value;
        foreach (var group in groups.Values.ToArray())
        {
            if (group.CleanupPending) continue;
            try { foreach (var window in group.Windows.Values) window.ApplyAppearance(value); }
            catch (Exception error) { Fail(group, error); }
        }
    }
    internal void Refresh(Func<string, TaskbarDockDisplay?> display)
    {
        foreach (var group in groups.Values.ToArray())
        {
            if (group.Closing || group.CleanupPending) continue;
            try
            {
                var resolved = Resolve(group.Request);
                var screen = display(group.Identity.ScreenId);
                if (!resolved.IsSuccess || !ReferenceEquals(resolved.Value!.Template, group.Declaration) || screen is null)
                { FinishClose(group); continue; }
                group.Request = group.Request with { WorkArea = screen.WorkArea, Dpi = screen.Dpi };
            }
            catch (Exception error) { Fail(group, error); }
        }
        ReflowAll(); Pulse();
    }
    internal CoreResult<bool> EnterKeyboard(EventGroupIdentity identity)
    {
        if (!groups.TryGetValue(identity, out var group) || group.Closing)
            return Failure("StaleEvent", "事件组已结束");
        try
        {
            group.Windows.Values.FirstOrDefault(value => value.TemplateId == group.Path[^1].Id)?.EnterKeyboard();
            group.Interaction.Poll(); return CoreResult<bool>.Success(true);
        }
        catch (Exception error) { Fail(group, error); return Failure("EventFocusFailed", "事件组无法进入键盘操作"); }
    }
    internal CoreResult<bool> Close(EventGroupIdentity identity)
    {
        if (!groups.TryGetValue(identity, out var group)) return CoreResult<bool>.Success(true);
        if (group.CleanupPending) return FinishClose(group);
        BeginClose(group); return group.CleanupPending ? Failure("EventCleanupPending", group.Error!) : CoreResult<bool>.Success(true);
    }
    internal CoreResult<bool> CloseEntry(FlyoutEntryKey entry, string session)
    {
        CoreResult<bool> result = CoreResult<bool>.Success(true);
        foreach (var group in groups.Values.Where(value => value.Request.Entry == entry && value.Request.SessionId == session).ToArray())
        { var closed = FinishClose(group); if (!closed.IsSuccess) result = closed; }
        Drive(); return result;
    }
    internal CoreResult<bool> TryClose()
    {
        stopping = true; timer.Stop();
        CoreResult<bool> result = CoreResult<bool>.Success(true);
        foreach (var group in groups.Values.ToArray())
        { var closed = FinishClose(group); if (!closed.IsSuccess) result = closed; }
        if (groups.Count == 0) timer.Tick -= Tick;
        return result;
    }
    public void Dispose()
    {
        var result = TryClose();
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
    }
    private CoreResult<Resolved> Resolve(EventGroupOpenRequest request)
    {
        if (request?.Entry?.Entry is not { Kind: TemplateEntryKind.EventChannel } entry ||
            string.IsNullOrWhiteSpace(request.Entry.ApplicationId) || request.Entry.ApplicationId.Length > 256 ||
            string.IsNullOrWhiteSpace(entry.FeatureGroupId) || entry.FeatureGroupId.Length > 256 ||
            string.IsNullOrWhiteSpace(entry.EntryId) || entry.EntryId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.ScreenId) || request.ScreenId.Length > 256 ||
            request.Dpi is < 48 or > 960 || !request.WorkArea.IsValid || !Enum.IsDefined(request.Position))
            return CoreResult<Resolved>.Failure(new("InvalidEventRequest", "事件身份、位置或显示环境无效"));
        var snapshot = states.GetSnapshot(request.Entry.ApplicationId);
        if (snapshot?.SessionId != request.SessionId || !snapshot.IsConnected || !snapshot.IsInteractive)
            return CoreResult<Resolved>.Failure(new("StaleSession", "事件会话不可用"));
        if (!Enabled(request)) return CoreResult<Resolved>.Failure(new("EntryDisabled", "事件入口已关闭显示"));
        var definition = snapshot.Declaration?.FlyoutEntries.FirstOrDefault(value => value.Kind == FlyoutKind.EventGroup &&
            value.Identity.Segments[1].Value == entry.FeatureGroupId && value.Identity.LocalId.Value == entry.EntryId);
        var template = snapshot.Declaration?.Templates.FirstOrDefault(value => value.Entry == entry)?.Declaration;
        if (definition is null || template is null || snapshot.State?.TemplateEntries?.Any(value => value.Entry == entry) != true)
            return CoreResult<Resolved>.Failure(new("EventTemplateUnavailable", "事件声明或完整模板状态不可用"));
        return CoreResult<Resolved>.Success(new(definition, template));
    }
    private bool Enabled(EventGroupOpenRequest request) =>
        preferences.EventVisibility?.GetValueOrDefault(Key(request.Entry), true) != false &&
        states.FlyoutRequests.IsEntryEnabled(request.Entry.ApplicationId, request.SessionId,
            request.Entry.Entry.FeatureGroupId, request.Entry.Entry.EntryId);
    private static string Key(FlyoutEntryKey entry) => JsonSerializer.Serialize(new[]
        { entry.ApplicationId, entry.Entry.FeatureGroupId, entry.Entry.EntryId });
    private TemplateRenderer Renderer(Group group, string id)
    {
        if (group.Renderers.TryGetValue(id, out var renderer)) return renderer;
        if (group.Renderers.Count >= TemplateLimits.TemplatesPerEntry) throw new InvalidOperationException("事件组模板预算已满");
        var controller = new TemplateInteractionController(states, group.Request.Entry.ApplicationId, group.Request.Entry.Entry,
            sender, id, intent => Navigate(group, intent));
        try { renderer = new(controller, images); }
        catch { controller.Dispose(); throw; }
        group.Renderers.Add(id, renderer); return renderer;
    }
    private ProtocolResult Navigate(Group group, TemplateNavigationIntent intent)
    {
        if (!Current(group) || group.Closing || intent.ApplicationId != group.Request.Entry.ApplicationId ||
            intent.Entry != group.Request.Entry.Entry || intent.SessionId != group.Request.SessionId ||
            group.Layout?.VisiblePanels.Any(value => value.TemplateId == intent.SourceTemplateId) != true ||
            !group.Renderers.TryGetValue(intent.SourceTemplateId, out var source) || !source.IsCurrentNavigation(intent) ||
            intent.Action.Kind is not (TemplateActionKind.OpenPanel or TemplateActionKind.Back))
            return ProtocolResult.Reject("StaleEvent", "事件导航来源已失效");
        return Navigate(group, intent.Action.Kind == TemplateActionKind.Back ? null : intent.Action.TargetId, intent.SourceNodeId);
    }
    private ProtocolResult Navigate(Group group, string? template, string? sourceNode = null)
    {
        if (!Current(group) || group.Closing) return ProtocolResult.Reject("StaleEvent", "事件组已结束");
        group.Keyboard = FlyoutNative.BelongsTo(FlyoutNative.CurrentFocus(), group.Windows.Values.Select(value => value.Handle));
        if (template is null)
        {
            if (group.Path.Count <= 1) return ProtocolResult.Reject("NoPreviousPanel", "已处于事件主面板");
            group.FocusNode = group.Path[^1].SourceNode; group.Path.RemoveAt(group.Path.Count - 1);
        }
        else
        {
            if (group.Declaration.Panels?.Any(value => value.TemplateId == template) != true)
                return ProtocolResult.Reject("UnknownPanel", "事件关联面板未声明");
            int existing = group.Path.FindIndex(value => value.Id == template);
            if (existing >= 0) group.Path.RemoveRange(existing + 1, group.Path.Count - existing - 1);
            else if (group.Path.Count < TemplateLimits.TemplatesPerEntry) group.Path.Add(new(template, sourceNode));
            else return ProtocolResult.Reject("PanelDepthExceeded", "事件导航预算已满");
            group.FocusNode = null;
        }
        try
        {
            ReflowScreen(group.Identity.ScreenId, group);
            return Current(group) && !group.Closing ? ProtocolResult.Success() : ProtocolResult.Reject("EventLayoutFailed", "事件导航布局失败");
        }
        catch (Exception error) { Fail(group, error); return ProtocolResult.Reject("EventLayoutFailed", "事件导航布局失败"); }
    }
    private void ReflowAll()
    {
        foreach (string screen in groups.Keys.Select(value => value.ScreenId).Distinct().ToArray())
        {
            try { ReflowScreen(screen); }
            catch (Exception error)
            {
                foreach (var group in groups.Values.Where(value => value.Identity.ScreenId == screen && !value.Closing).ToArray()) Fail(group, error);
            }
        }
    }
    private void ReflowScreen(string screen, Group? transition = null, bool apply = true)
    {
        if (reflowing) return;
        reflowing = true;
        try
        {
            var order = schedule.Snapshot().ToDictionary(value => value.Identity, value => value.FirstShownOrder ?? long.MaxValue);
            var active = groups.Values.Where(value => value.Identity.ScreenId == screen && !value.Closing)
                .OrderBy(value => order.GetValueOrDefault(value.Identity, long.MaxValue)).ThenBy(value => value.Identity.Generation).ToList();
            if (active.Count == 0) return;
            var work = FlyoutNative.ToDip(active[0].Request.WorkArea, active[0].Request.Dpi);
            foreach (var group in active.ToArray())
            {
                try
                {
                var requested = group.Path.Select(value => value.Id).Concat(group.Parallel).Distinct(StringComparer.Ordinal).ToArray();
                var measures = requested.Select(id => new FlyoutPanelMeasure(id, TaskbarFlyoutWindow.Measure(Renderer(group, id), 320),
                    group.Declaration.Panels?.FirstOrDefault(value => value.TemplateId == id)?.Presentation == PanelPresentation.Parallel)).ToArray();
                var layout = FlyoutGroupLayout.Calculate(work, new(work.X + work.Width / 2, work.Bottom - 1, 1, 1), measures, group.Path[^1].Id);
                if (!layout.IsSuccess) throw new InvalidOperationException(layout.Error!.Message);
                group.Measures = measures;
                group.BaseLayout = group.Layout = layout.Value!;
                }
                catch (Exception error) { Fail(group, error); active.Remove(group); }
            }
            if (active.Count == 0) return;
            IReadOnlyList<PixelRect> higher;
            try
            {
                higher = HigherPriorityBounds(screen);
                if (higher is null || higher.Count > EventGroupLayout.MaximumHigherPriorityBounds || higher.Any(value => !value.IsValid))
                    throw new InvalidOperationException("高优先级窗口矩形无效或超出预算");
            }
            catch (Exception error) { higher = []; record("event-obstacle-query-failed", error.GetType().Name); }
            var configured = preferences.Events ?? new();
            var placements = EventGroupLayout.Calculate(work,
                active.Select(value => new EventGroupMeasure(value.Identity.Key, value.Layout!.Bounds.Width, value.Layout.Bounds.Height,
                    Position(configured.AllowApplicationPosition && value.Request.Position != FlyoutPosition.Default
                        ? value.Request.Position : configured.DefaultPosition))).ToArray(), Position(configured.DefaultPosition),
                higher.Select(value => FlyoutNative.ToDip(value, active[0].Request.Dpi)).ToArray());
            if (!placements.IsSuccess) throw new InvalidOperationException(placements.Error!.Message);
            foreach (var group in active)
            {
                try
                {
                var placement = placements.Value!.Placements.Single(value => value.Key == group.Identity.Key);
                group.BaseBounds = placement.Bounds;
                try { group.Layout = ReservedLayout(group, placement.Bounds); }
                catch (InvalidOperationException error) when (group.HintReservation > 0)
                {
                    // A smaller environment can make the associated hint impossible. Preserve
                    // the event's operation area; its hint owner will reject/release on this frame.
                    group.HintReservations.Clear(); group.HintReservation = 0;
                    group.Layout = group.BaseLayout!;
                    record("event-hint-space-unavailable", new { group.Identity, error.Message });
                }
                PrepareWindows(group, group.HintReservation > 0 ? group.Layout.Bounds : placement.Bounds, ReferenceEquals(group, transition));
                if (apply || group.Committed) Advance(group);
                }
                catch (Exception error) { Fail(group, error); }
            }
        }
        finally { reflowing = false; Drive(); }
    }
    private void PrepareWindows(Group group, TaskbarDipRect destination, bool transition)
    {
        var layout = group.Layout!;
        double dx = destination.X - layout.Bounds.X, dy = destination.Y - layout.Bounds.Y;
        var targets = new List<FlyoutRectangleTarget>();
        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in layout.VisiblePanels)
        {
            string key = layout.Mode == FlyoutGroupMode.Parallel && panel.TemplateId != group.Declaration.MainTemplateId
                ? "panel:" + Array.FindIndex(group.Declaration.Templates.ToArray(), value => value.TemplateId == panel.TemplateId) : "main";
            visible.Add(key);
            if (!group.Windows.TryGetValue(key, out var window))
            {
                if (group.Windows.Count >= TemplateLimits.TemplatesPerEntry) throw new InvalidOperationException("事件窗口预算已满");
                window = new(() => BeginClose(group), () => Navigate(group, (string?)null), id => Navigate(group, id),
                    () => WindowClosed(group, key), record);
                group.Windows.Add(key, window);
                window.Initialize("MTP 事件面板", "MtpEventGroup"); window.ApplyAppearance(appearance);
                var captured = window;
                group.Interaction.Attach(window.Root, () => captured.Handle);
            }
            window.RefreshAppearance();
            var renderer = Renderer(group, panel.TemplateId);
            foreach (var other in group.Windows.Values.Where(value => !ReferenceEquals(value, window))) other.DetachRenderer(renderer);
            transition |= window.SetRenderer(panel.TemplateId, renderer, group.Path.Count > 1, group.Declaration.Panels?.Select(value => value.TemplateId) ?? []);
            targets.Add(new(key, panel.Bounds with { X = panel.Bounds.X + dx, Y = panel.Bounds.Y + dy }));
        }
        group.Interaction.SynchronizeMenus();
        foreach (var old in group.Windows.Where(value => !visible.Contains(value.Key)))
        {
            old.Value.DisableInput();
            var prior = group.LastApplied?.Panels.FirstOrDefault(value => value.Key == old.Key);
            if (prior is not null) targets.Add(prior with { Opacity = 0 });
        }
        group.Visible = visible; group.Targets = targets;
        group.TargetBounds = new(destination.X, destination.Y, layout.Bounds.Width, layout.Bounds.Height);
        long previous = group.Animation.Revision;
        group.Animation.Retarget(targets, Now, Reduced(), group.Animation.Generation, transition, group.LastApplied);
        if (previous != group.Animation.Revision) foreach (var window in group.Windows.Values) window.RetargetContent();
    }
    private void Advance(Group group)
    {
        if (!Current(group) || group.CleanupPending || !group.Committed) return;
        try
        {
            var frame = group.Animation.Sample(Now);
            if (Reduced() && !frame.IsComplete)
            { group.Animation.Retarget(group.Targets, Now, true, group.Animation.Generation); frame = group.Animation.Sample(Now); }
            bool changed = group.LastApplied is null || group.LastAppliedDpi != group.Request.Dpi ||
                !group.LastApplied.Panels.SequenceEqual(frame.Panels) || group.LastApplied.Progress != frame.Progress;
            if (changed)
            {
                foreach (var panel in frame.Panels)
                    group.Windows.GetValueOrDefault(panel.Key)?.Apply(panel, frame.Progress, group.Request.Dpi, group.Closing || !group.Visible.Contains(panel.Key));
                group.LastApplied = frame;
                group.LastAppliedDpi = group.Request.Dpi;
                EmitFrame(group);
            }
            if (!group.Closing && !group.NativeVisible && frame.Panels.Any(panel => panel.Opacity * 255 >= 1 &&
                group.Windows.GetValueOrDefault(panel.Key) is { Handle: not 0 } window && IsWindowVisible(window.Handle) && FlyoutNative.ReadOpacity(window.Handle) > 0))
                group.NativeVisible = schedule.MarkVisible(group.Identity);
            if (frame.IsComplete)
            {
                if (group.Closing) { FinishClose(group); return; }
                foreach (string key in group.Windows.Keys.Where(value => !group.Visible.Contains(value)).ToArray())
                {
                    var window = group.Windows[key]; group.Interaction.Detach(window.Root);
                    var result = window.TryClose();
                    if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
                    group.Windows.Remove(key);
                }
                if (group.Keyboard)
                {
                    group.Windows.Values.FirstOrDefault(value => value.TemplateId == group.Path[^1].Id)?.EnterKeyboard(group.FocusNode);
                    group.Keyboard = false; group.FocusNode = null;
                }
            }
            if (!group.Closing) group.Interaction.Poll();
        }
        catch (Exception error) { Fail(group, error); }
    }
    private void Tick(object? sender, object args) => Pulse();
    private void Pulse()
    {
        if (ticking || stopping) return;
        ticking = true;
        try
        {
            foreach (var group in groups.Values.ToArray()) Advance(group);
            foreach (var identity in schedule.Expired()) if (groups.TryGetValue(identity, out var group)) BeginClose(group);
            Converge();
        }
        catch (Exception error) { record("event-tick-failed", new { type = error.GetType().Name, error.Message }); }
        finally { ticking = false; Drive(); }
    }
    private void Converge()
    {
        PollInteractions();
        foreach (string screen in groups.Keys.Select(value => value.ScreenId).Distinct().ToArray())
            foreach (var identity in schedule.ConvergenceCandidates(screen))
                if (groups.TryGetValue(identity, out var group)) BeginClose(group);
    }
    private void PollInteractions(string? screen = null)
    {
        // Admission and limit changes may run between timer ticks. Protection follows
        // the current native input target, never the last cached idle-frame observation.
        foreach (var group in groups.Values.Where(value => !value.Closing && !value.CleanupPending &&
            (screen is null || value.Identity.ScreenId == screen)).ToArray())
        {
            try { group.Interaction.Poll(); }
            catch (Exception error) { Fail(group, error); }
        }
    }
    private void BeginClose(Group group)
    {
        if (!Current(group) || group.Closing) return;
        try
        {
            group.Interaction.Dispose(); group.Closing = true; schedule.BeginClose(group.Identity);
            foreach (var renderer in group.Renderers.Values) renderer.StopInteraction();
            foreach (var window in group.Windows.Values) window.DisableInput();
            var current = group.LastApplied ?? group.Animation.Sample(Now);
            group.Targets = current.Panels.Select(value => value with { Opacity = 0, Bounds = value.Bounds with { Y = value.Bounds.Y + 8 } }).ToArray();
            group.Animation.Retarget(group.Targets, Now, Reduced(), group.Animation.Generation, presentedFrame: group.LastApplied);
            Advance(group); Drive();
        }
        catch (Exception error) { Fail(group, error); }
    }
    private CoreResult<bool> FinishClose(Group group)
    {
        if (!Current(group)) return CoreResult<bool>.Success(true);
        group.Closing = true; schedule.BeginClose(group.Identity);
        try
        {
            group.Interaction?.Dispose();
            foreach (var renderer in group.Renderers.Values) renderer.StopInteraction();
            foreach (var pair in group.Windows.ToArray())
            {
                pair.Value.DisableInput();
                var result = pair.Value.TryClose();
                if (!result.IsSuccess) { group.CleanupPending = true; group.Error = result.Error!.Message; return result; }
                group.Windows.Remove(pair.Key);
            }
            foreach (var renderer in group.Renderers.ToArray()) { renderer.Value.Dispose(); group.Renderers.Remove(renderer.Key); }
            group.Animation.Clear(); groups.Remove(group.Identity);
            if (group.Committed) schedule.ConfirmDestroyed(group.Identity);
            record("event-closed", group.Identity); EmitFrame(group); QueueReflow(); Drive(); return CoreResult<bool>.Success(true);
        }
        catch (Exception error)
        { group.CleanupPending = true; group.Error = error.Message; return Failure("EventCleanupPending", "事件原生资源尚未确认清理"); }
    }
    private void Fail(Group group, Exception error)
    {
        if (!Current(group)) return;
        group.Error = error.Message;
        record("event-owner-failed", new { group.Identity, type = error.GetType().Name, error.Message });
        FinishClose(group);
    }
    private void WindowClosed(Group group, string key)
    {
        if (!Current(group) || group.FinishQueued || !group.Closing && !group.Visible.Contains(key)) return;
        group.FinishQueued = true;
        dispatcher.TryEnqueue(() =>
        {
            group.FinishQueued = false;
            if (!Current(group) || group.CleanupPending) return;
            if (group.Closing) FinishClose(group); else BeginClose(group);
        });
    }
    private void EmitFrame(Group group)
    {
        if (PresentedFrameApplied is { } associated)
        {
            var owner = new HintOwner(group.Identity.ScreenId, group.Identity.Generation, group.Request.Entry, group.Identity.SessionId);
            foreach (Action<HintOwner> listener in associated.GetInvocationList())
                try { listener(owner); } catch (Exception error) { record("event-associated-frame-failed", error.GetType().Name); }
        }
        if (FrameApplied is not { } listeners) return;
        foreach (Action listener in listeners.GetInvocationList())
            try { listener(); } catch (Exception error) { record("event-frame-observer-failed", error.GetType().Name); }
    }
    private void QueueReflow()
    {
        if (stopping || reflowQueued || groups.Count == 0) return;
        reflowQueued = true;
        if (!dispatcher.TryEnqueue(() => { reflowQueued = false; if (!stopping) ReflowAll(); })) reflowQueued = false;
    }
    private bool Current(Group group) => groups.GetValueOrDefault(group.Identity) == group;
    private bool Reduced()
    { if (ReducedMotionOverride is bool value) return value; try { return !ui.AnimationsEnabled; } catch { return true; } }
    private void Drive()
    {
        var active = groups.Values.Where(value => !value.CleanupPending).ToArray();
        if (stopping || active.Length == 0) { timer.Stop(); return; }
        timer.Interval = TimeSpan.FromMilliseconds(active.Any(value => value.LastApplied?.IsComplete != true) ? 16 : 100);
        timer.Start();
    }
    private static HintPosition Position(FlyoutPosition value) => value switch
    {
        FlyoutPosition.TopLeft => HintPosition.TopLeft, FlyoutPosition.TopCenter => HintPosition.TopCenter,
        FlyoutPosition.TopRight => HintPosition.TopRight, FlyoutPosition.BottomLeft => HintPosition.BottomLeft,
        FlyoutPosition.BottomCenter => HintPosition.BottomCenter, FlyoutPosition.BottomRight => HintPosition.BottomRight,
        FlyoutPosition.Center => HintPosition.Center, FlyoutPosition.LowerCenter => HintPosition.LowerCenter,
        _ => HintPosition.BottomLeft
    };
    private static ProtocolResult Reject(StructuredError error) => ProtocolResult.Reject(error.Code, error.Message);
    private static CoreResult<bool> Failure(string code, string message) => CoreResult<bool>.Failure(new(code, message));
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    private sealed record Resolved(ValidatedFlyoutEntry Entry, EntryTemplateDeclaration Template);
    private sealed record Navigation(string Id, string? SourceNode);
    private sealed class Group(EventGroupOpenRequest request, EventGroupIdentity identity, EntryTemplateDeclaration declaration)
    {
        internal EventGroupOpenRequest Request = request;
        internal readonly EventGroupIdentity Identity = identity;
        internal readonly EntryTemplateDeclaration Declaration = declaration;
        internal readonly List<Navigation> Path = [];
        internal readonly List<string> Parallel = [];
        internal readonly Dictionary<string, TemplateRenderer> Renderers = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, TaskbarFlyoutWindow> Windows = new(StringComparer.Ordinal);
        internal EventGroupInteraction Interaction = null!;
        internal readonly FlyoutRectangleAnimation Animation = new();
        internal FlyoutRectangleFrame? LastApplied;
        internal uint LastAppliedDpi;
        internal IReadOnlyList<FlyoutRectangleTarget> Targets = [];
        internal FlyoutGroupLayoutResult? Layout;
        internal FlyoutGroupLayoutResult? BaseLayout;
        internal TaskbarDipRect? BaseBounds, TargetBounds;
        internal IReadOnlyList<FlyoutPanelMeasure> Measures = [];
        internal double HintReservation;
        internal readonly Dictionary<string, double> HintReservations = new(StringComparer.Ordinal);
        internal HashSet<string> Visible = [];
        internal bool Committed, NativeVisible, Closing, CleanupPending, FinishQueued, Keyboard;
        internal string? Error, FocusNode;
    }
}

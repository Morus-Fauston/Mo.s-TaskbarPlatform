using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mtp.Contracts;
using Mtp.Host.Islands;
using Mtp.Host.Templates;
using Mtp.Platform.Core;
using Windows.Foundation;

namespace Mtp.Host.Flyouts;

/// <summary>Production UI composition: resolves only current Host-owned triggers and consumes admitted requests.</summary>
internal sealed class TaskbarFlyoutCoordinator : IDisposable
{
    private readonly HostBrokerSession session;
    private readonly IslandDisplayAdapter island;
    private readonly Func<IReadOnlyList<TaskbarDockDisplay>> displays;
    private readonly Action<string, object?> record;
    private readonly Dictionary<string, AnchorOwner> anchors = new(StringComparer.Ordinal);
    private readonly HostFlyoutReplacementTracker replacingRequests;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    internal TaskbarFlyoutManager Manager { get; }
    internal ShortHintManager Hints { get; }
    internal InteractiveHintManager InteractiveHints { get; }
    internal EventGroupManager Events { get; }
    private bool stacking, stopping;
    private string? stackError;

    internal TaskbarFlyoutCoordinator(HostBrokerSession session, IslandDisplayAdapter island,
        Func<IReadOnlyList<TaskbarDockDisplay>> displays, RegisteredImageCache images, Action<string, object?> record)
    {
        this.session = session; this.island = island; this.displays = displays;
        this.record = (kind, value) => { try { record(kind, value); } catch (Exception) { } };
        replacingRequests = new(session.States.FlyoutRequests);
        Manager = new(session.States, (key, templateId, navigate) => new TemplateRenderer(
            new TemplateInteractionController(session.States, key.ApplicationId, key.Entry,
                SendActionAsync, templateId, navigate), images), record);
        Events = new(session.States, images, SendActionAsync, record);
        Hints = new(session.States, Manager, images, record, events: Events);
        InteractiveHints = new(session.States, Manager, images,
            (origin, slot, parameter, expected, token) => SendActionCoreAsync(slot, parameter, expected, token, origin), ExpandHint, record);
        Hints.AdditionalInstanceCount = () => InteractiveHints.Inspect().Count;
        InteractiveHints.AdditionalInstanceCount = () => Hints.Inspect().Count;
        Events.HigherPriorityBounds = screen => Hints.Inspect().Concat(InteractiveHints.Inspect())
            .Where(x => x.Request.ScreenId == screen && x.Request.Owner is null && x.Bounds.IsValid)
            .Select(x => x.Bounds).Take(64).ToArray();
        Manager.PresentedFrameApplied += GroupFrameApplied;
        Events.FrameApplied += EnforceStack;
        Hints.FrameApplied += EnforceStack;
        InteractiveHints.FrameApplied += EnforceStack;
    }

    private void GroupFrameApplied(string screen, long generation) => EnforceStack();
    private void EnforceStack()
    {
        if (stacking || stopping) return;
        stacking = true;
        try
        {
            var taskbar = Manager.Inspect().SelectMany(x => x.Windows).Select(x => x.Handle).Where(FlyoutNative.IsWindow);
            var events = Events.Inspect().SelectMany(x => x.Windows).Select(x => x.Handle).Where(FlyoutNative.IsWindow);
            var hints = Hints.Inspect().Select(x => x.Handle).Concat(InteractiveHints.Inspect().SelectMany(x =>
                InteractiveHints.WindowForTesting(x.Generation) is { } window ? new[] { window.BackgroundHandle, window.Handle } : []))
                .Where(FlyoutNative.IsWindow);
            var result = HostOwnedFlyoutStack.Apply(taskbar, events, hints);
            var error = result.Error?.Code;
            if (error != stackError && error is not null) record("flyout-stack-failed", result.Error);
            stackError = error;
        }
        catch (Exception error) { if (stackError != error.GetType().Name) record("flyout-stack-failed", error.Message); stackError = error.GetType().Name; }
        finally { stacking = false; }
    }

    internal void ApplySettings(HostSettingsPreferences preferences)
    {
        foreach (var snapshot in session.States.Snapshots)
            foreach (var entry in snapshot.Declaration?.FlyoutEntries ?? [])
                if (entry.Kind is FlyoutKind.ShortHint or FlyoutKind.InteractiveHint or FlyoutKind.EventGroup)
                    session.States.FlyoutRequests.SetEntryEnabled(entry,
                        (entry.Kind == FlyoutKind.EventGroup ? preferences.EventVisibility : preferences.HintVisibility)
                            ?.GetValueOrDefault(HostSettingsController.IdentityKey(entry.Identity), true) != false);
        Events.ApplySettings(preferences);
        Hints.ApplySettings(preferences);
        InteractiveHints.ApplySettings(preferences);
    }

    internal Task<ProtocolResult> SendActionAsync(ActionSlotReference slot, ActionParameter parameter, string expected, CancellationToken token) =>
        SendActionCoreAsync(slot, parameter, expected, token);

    private async Task<ProtocolResult> SendActionCoreAsync(ActionSlotReference slot, ActionParameter parameter, string expected,
        CancellationToken token, ShortHintRequest? hintOrigin = null)
    {
        // A result belongs to the group present at dispatch, never to a later same-name group.
        var kind = slot.EntryKind switch { ActionEntryKind.TaskbarFlyout => TemplateEntryKind.TaskbarFlyout,
            ActionEntryKind.Hint => TemplateEntryKind.Hint, ActionEntryKind.EventChannel => TemplateEntryKind.EventChannel,
            _ => TemplateEntryKind.Component };
        var entry = new FlyoutEntryKey(slot.ApplicationId, new(slot.FeatureGroupId, kind, slot.EntryId));
        var origin = await OnUi(() =>
        {
            if (hintOrigin is not null) return (Owner: hintOrigin.Owner, ScreenId: (string?)hintOrigin.ScreenId);
            if (kind == TemplateEntryKind.EventChannel)
            {
                var live = Events.Inspect().FirstOrDefault(x => x.Entry == entry && x.Identity.SessionId == expected && !x.Closing);
                var eventOwner = live is null ? null : new HintOwner(live.Identity.ScreenId, live.Identity.Generation, live.Entry, live.Identity.SessionId);
                return (Owner: eventOwner, ScreenId: eventOwner?.ScreenId);
            }
            var group = Manager.Inspect().FirstOrDefault(x => x.Entry == entry && x.SessionId == expected && !x.Closing);
            var owned = group is null ? null : new HintOwner(group.ScreenId, group.Generation, group.Entry, group.SessionId);
            return (Owner: owned, ScreenId: owned?.ScreenId ?? TriggerDisplay()?.Id);
        });
        var owner = origin.Owner;
        var result = await session.SendActionAsync(slot, parameter, token, expected);
        await OnUi(() =>
        {
          if (!token.IsCancellationRequested && session.States.GetSnapshot(slot.ApplicationId)?.SessionId == expected &&
            (!result.Accepted || result.ActivityRejections is { Count: > 0 }))
          {
            var screen = displays().FirstOrDefault(x => x.Id == origin.ScreenId);
            if (screen is not null)
            {
                string text = result.ActivityRejections is { Count: > 0 } denied
                    ? $"{denied.Count} 项活动未显示：对应实况岛入口已关闭。可在设置中开启。" : result.Message;
                if (text.Length > 1024) text = text[..1024];
                var shown = Hints.Show(new(entry, expected, screen.Id, screen.WorkArea, screen.Dpi, Owner: owner), text);
                record("action-hint-result", new { slot, expected, result.Code, shown = shown.Code, owner });
            }
          }
          return true;
        });
        return result;
    }

    private Task<T> OnUi<T>(Func<T> action)
    {
        if (dispatcher.HasThreadAccess) return Task.FromResult(action());
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() => { try { result.TrySetResult(action()); } catch (Exception error) { result.TrySetException(error); } }))
            result.TrySetException(new ObjectDisposedException(nameof(TaskbarFlyoutCoordinator)));
        return result.Task;
    }

    private TaskbarDockDisplay? TriggerDisplay(HostFlyoutTrigger? trigger = null)
    {
        var available = displays();
        if (trigger is not null)
            return available.FirstOrDefault(x => FlyoutNative.Contains(x.Bounds, new() { X = trigger.X, Y = trigger.Y })) ??
                available.FirstOrDefault(x => x.IsPrimary) ?? available.FirstOrDefault();
        if (FlyoutNative.GetCursorPos(out var cursor))
        {
            var hit = available.FirstOrDefault(x => FlyoutNative.Contains(x.Bounds, cursor));
            if (hit is not null) return hit;
        }
        return available.FirstOrDefault(x => x.IsPrimary) ?? available.FirstOrDefault();
    }

    internal ProtocolResult Navigate(TemplateRenderer source, TemplateNavigationIntent intent)
    {
        if (!source.IsCurrentNavigation(intent) || intent.Action.Kind != TemplateActionKind.OpenPanel)
            return ProtocolResult.Reject("StaleTemplate", "浮窗入口导航已失效");
        var node = source.GetNodeElement(intent.SourceNodeId);
        if (node is null) return ProtocolResult.Reject("AnchorUnavailable", "触发控件不可见");
        PixelRect? ReadAnchor()
        {
            if (!source.IsCurrentNavigation(intent) || source.GetNodeElement(intent.SourceNodeId) is not { } current) return null;
            return ToScreen(current.TransformToVisual(null).TransformBounds(new(0, 0, current.ActualWidth, current.ActualHeight)));
        }
        var invocation = node is Control { FocusState: FocusState.Keyboard } ? FlyoutInvocationKind.Keyboard : FlyoutInvocationKind.Pointer;
        return Open(new(intent.ApplicationId, intent.Entry), intent.SessionId, ReadAnchor, invocation, intent.Action.TargetId);
    }

    internal ProtocolResult OpenItem(HostItemActivationResult routed, string? control)
    {
        if (routed.Origin is not { } origin || routed.SessionId is null || routed.TaskbarFlyoutId is null)
            return ProtocolResult.Reject("StaleItem", "项入口已失效");
        var trigger = island.GetItemTrigger(origin, control);
        PixelRect? ReadAnchor() => island.GetItemTrigger(origin, control) is { } current ? ToScreen(current.Bounds) : null;
        return Open(new(origin.Item.ApplicationId, new(origin.Item.FeatureGroupId, TemplateEntryKind.TaskbarFlyout, routed.TaskbarFlyoutId)),
            routed.SessionId, ReadAnchor, trigger?.Keyboard == true ? FlyoutInvocationKind.Keyboard : FlyoutInvocationKind.Pointer);
    }

    internal ProtocolResult Present(QueuedHostFlyout queued)
    {
        var message = queued.Message;
        var request = message.Flyout!;
        if (queued.Expired) return ProtocolResult.Reject("FlyoutExpired", "显示请求已过期");
        var state = session.States.GetSnapshot(message.ApplicationId);
        if (state?.SessionId != message.SessionId || !state.IsInteractive || !state.IsConnected)
            return ProtocolResult.Reject("StaleSession", "显示请求会话已失效");
        if (state.State?.Revision != request.StateRevision) return ProtocolResult.Reject("StaleState", "显示请求状态已更换");
        if (!session.States.FlyoutRequests.IsEntryEnabled(message.ApplicationId, message.SessionId, request.FeatureGroupId, request.EntryId))
            return ProtocolResult.Reject("EntryDisabled", "入口显示已关闭");
        if (request.Kind is FlyoutKind.ShortHint or FlyoutKind.InteractiveHint)
        {
            var target = request.Screen == FlyoutScreen.Primary ? displays().FirstOrDefault(x => x.IsPrimary) ?? TriggerDisplay(queued.Trigger) : TriggerDisplay(queued.Trigger);
            if (target is null) return ProtocolResult.Reject("ScreenUnavailable", "没有可用提示屏幕");
            var entry = new FlyoutEntryKey(message.ApplicationId, new(request.FeatureGroupId, TemplateEntryKind.Hint, request.EntryId));
            HintOwner? owner = null;
            if (request.Kind == FlyoutKind.InteractiveHint)
            {
                var expansion = state.Declaration?.FlyoutEntries.FirstOrDefault(x => x.Identity.Segments[1].Value == request.FeatureGroupId &&
                    x.Identity.Segments[2].Value == request.EntryId)?.Expansion;
                var group = Manager.Inspect().FirstOrDefault(x => x.ScreenId == target.Id && !x.Closing && x.SessionId == message.SessionId &&
                    x.Entry.ApplicationId == message.ApplicationId && x.Entry.Entry.FeatureGroupId == request.FeatureGroupId &&
                    x.Entry.Entry.Kind == TemplateEntryKind.TaskbarFlyout && x.Entry.Entry.EntryId == expansion?.TaskbarFlyoutId);
                if (group is not null) owner = new(group.ScreenId, group.Generation, group.Entry, group.SessionId);
            }
            var hintRequest = new ShortHintRequest(entry, message.SessionId, target.Id, target.WorkArea, target.Dpi, request.Position, owner);
            return request.Kind == FlyoutKind.ShortHint ? Hints.Show(hintRequest) : InteractiveHints.Show(hintRequest);
        }
        if (request.Kind == FlyoutKind.EventGroup)
        {
            var target = request.Screen == FlyoutScreen.Primary ? displays().FirstOrDefault(x => x.IsPrimary) ?? TriggerDisplay(queued.Trigger) : TriggerDisplay(queued.Trigger);
            if (target is null) return ProtocolResult.Reject("ScreenUnavailable", "没有可用事件屏幕");
            return Events.Show(new(new(message.ApplicationId, new(request.FeatureGroupId, TemplateEntryKind.EventChannel, request.EntryId)),
                message.SessionId, target.Id, target.WorkArea, target.Dpi, request.Position));
        }
        if (request.Kind != FlyoutKind.TaskbarGroup) return ProtocolResult.Reject("FlyoutUnavailable", "浮窗类型无效");
        if (request.Screen == FlyoutScreen.Primary && displays().FirstOrDefault(x => x.IsPrimary)?.Id != island.Geometry?.DisplayId)
            return ProtocolResult.Reject("ScreenUnavailable", "主屏幕没有可用任务栏入口");
        var source = island.GetGroupFrame()?.Components.FirstOrDefault(x => x.Key.ApplicationId == message.ApplicationId &&
            x.Key.FeatureGroupId == request.FeatureGroupId)?.Key;
        PixelRect? ReadAnchor()
        {
            var frame = island.GetGroupFrame();
            var component = frame?.Components.FirstOrDefault(x => x.Key == source);
            if (source is null || island.GroupSnapshot?.ComponentsByKey.ContainsKey(source) != true) return null;
            return component is null || frame is null ? null : ToScreen(new(component.Bounds.X + frame.WidthDip,
                component.Bounds.Y, component.Bounds.Width, component.Bounds.Height));
        }
        var result = Open(new(message.ApplicationId, new(request.FeatureGroupId, TemplateEntryKind.TaskbarFlyout, request.EntryId)),
            message.SessionId, ReadAnchor, FlyoutInvocationKind.Automatic);
        if (result.Code == "Replacing" && island.Geometry is { } geometry) replacingRequests.Track(geometry.DisplayId, message);
        return result;
    }

    private ProtocolResult ExpandHint(ShortHintRequest source, HintExpansionTarget target)
    {
        var entry = new FlyoutEntryKey(source.Entry.ApplicationId,
            new(source.Entry.Entry.FeatureGroupId, TemplateEntryKind.TaskbarFlyout, target.TaskbarFlyoutId));
        if (source.Owner is { } owner)
        {
            if (owner.Entry != entry) return ProtocolResult.Reject("InvalidHintOwner", "展开目标不属于当前组");
            return Manager.ExpandFromHint(owner, target.PanelTemplateId);
        }
        var component = island.GetGroupFrame()?.Components.FirstOrDefault(x => x.Key.ApplicationId == source.Entry.ApplicationId &&
            x.Key.FeatureGroupId == source.Entry.Entry.FeatureGroupId)?.Key;
        PixelRect? ReadAnchor()
        {
            var frame = island.GetGroupFrame();
            var current = frame?.Components.FirstOrDefault(x => x.Key == component);
            if (component is null || island.GroupSnapshot?.ComponentsByKey.ContainsKey(component) != true || current is null || frame is null) return null;
            return ToScreen(new(current.Bounds.X + frame.WidthDip, current.Bounds.Y, current.Bounds.Width, current.Bounds.Height));
        }
        if (island.Geometry?.DisplayId != source.ScreenId) return ProtocolResult.Reject("AnchorUnavailable", "提示屏幕没有可用任务栏入口");
        var existing = Manager.Inspect().FirstOrDefault(x => x.ScreenId == source.ScreenId && x.Entry == entry && x.SessionId == source.SessionId && !x.Closing);
        if (existing is not null)
            return Manager.ExpandFromHint(new(existing.ScreenId, existing.Generation, existing.Entry, existing.SessionId), target.PanelTemplateId);
        return Open(entry, source.SessionId, ReadAnchor, FlyoutInvocationKind.Pointer, target.PanelTemplateId);
    }

    private ProtocolResult Open(FlyoutEntryKey entry, string sessionId, Func<PixelRect?> readAnchor,
        FlyoutInvocationKind invocation, string? initialTemplate = null)
    {
        try
        {
            if (entry.Entry.Kind == TemplateEntryKind.TaskbarFlyout && !session.States.FlyoutRequests.IsEntryEnabled(
                entry.ApplicationId, sessionId, entry.Entry.FeatureGroupId, entry.Entry.EntryId))
                return ProtocolResult.Reject("EntryDisabled", "入口显示已关闭");
            var geometry = island.Geometry;
            var anchor = readAnchor();
            if (!island.IsAlive || geometry is null || anchor is null)
                return ProtocolResult.Reject("AnchorUnavailable", "任务栏入口没有可用定位");
            var work = WorkArea(geometry);
            bool OriginAvailable() => island.IsAlive && island.Geometry?.DisplayId == geometry.DisplayId && readAnchor() is not null;
            var result = Manager.Toggle(new(entry, sessionId, geometry.DisplayId, anchor.Value, work, geometry.Dpi,
                invocation, invocation == FlyoutInvocationKind.Pointer ? unchecked((uint)FlyoutNative.GetMessageTime()) : null,
                island.Handle, initialTemplate, OriginAvailable));
            if (!result.IsSuccess) return ProtocolResult.Reject(result.Error!.Code, result.Error.Message);
            if (result.Value != FlyoutToggleResult.Closed) anchors[geometry.DisplayId] = new(entry, sessionId, readAnchor);
            return ProtocolResult.Success(result.Value switch { FlyoutToggleResult.Opened => "Displayed", FlyoutToggleResult.Closed => "Closed", _ => "Replacing" });
        }
        catch (Exception error) { record("flyout-compose-failed", error.Message); return ProtocolResult.Reject("FlyoutUnavailable", "Host无法呈现该浮窗"); }
    }

    internal void Refresh()
    {
        foreach (var group in Manager.Inspect())
        {
            if (group.Closing) continue;
            if (!anchors.TryGetValue(group.ScreenId, out var owner) || owner.Entry != group.Entry || owner.SessionId != group.SessionId) continue;
            var geometry = island.Geometry;
            PixelRect? anchor = null;
            try { if (island.IsAlive) anchor = owner.Read(); } catch (Exception) { }
            if (geometry is null || geometry.DisplayId != group.ScreenId || anchor is null)
                Manager.CloseScreen(group.ScreenId, group.Generation);
            else Manager.UpdateEnvironment(group.ScreenId, anchor.Value, WorkArea(geometry), geometry.Dpi);
        }
        Manager.Refresh();
        Events.Refresh(id => displays().FirstOrDefault(x => x.Id == id));
        Hints.Refresh(id => displays().FirstOrDefault(x => x.Id == id));
        InteractiveHints.Refresh(id => displays().FirstOrDefault(x => x.Id == id));
        var observed = Manager.Inspect();
        foreach (var pair in replacingRequests.Snapshot())
        {
            var message = pair.Value;
            var request = message.Flyout!;
            var current = observed.FirstOrDefault(x => x.ScreenId == pair.Key);
            if (current is { Closing: true }) continue;
            bool displayed = current is not null && current.SessionId == message.SessionId && current.Entry.ApplicationId == message.ApplicationId &&
                current.Entry.Entry == new TemplateEntryReference(request.FeatureGroupId, TemplateEntryKind.TaskbarFlyout, request.EntryId);
            var result = displayed ? ProtocolResult.Success("Displayed") : ProtocolResult.Reject("FlyoutReplacementFailed", "替换窗口未能完成，原请求不会重放");
            replacingRequests.Complete(pair.Key, message, result);
            record("flyout-replacement-result", new { message.ApplicationId, message.SessionId, request.RequestSequence, result.Code, result.Accepted });
        }
        var live = observed.Select(x => x.ScreenId).ToHashSet(StringComparer.Ordinal);
        foreach (var key in anchors.Keys.Where(x => !live.Contains(x)).ToArray()) anchors.Remove(key);
        EnforceStack();
    }

    private PixelRect WorkArea(TaskbarDockGeometry geometry) => displays().FirstOrDefault(x => x.Id == geometry.DisplayId)?.WorkArea ??
        new(geometry.DisplayBounds.X, geometry.DisplayBounds.Y, geometry.DisplayBounds.Width,
            checked(geometry.TaskbarBounds.Y - geometry.DisplayBounds.Y));
    private PixelRect? ToScreen(Rect value)
    {
        if (!island.IsAlive || island.Geometry is not { } geometry || value.Width <= 0 || value.Height <= 0) return null;
        var bounds = FlyoutNative.Bounds(island.Handle);
        double scale = 96d / geometry.Dpi;
        return FlyoutNative.ToPixels(new(bounds.X * scale + value.X, bounds.Y * scale + value.Y, value.Width, value.Height), geometry.Dpi);
    }
    internal CoreResult<bool> TryClose()
    {
        stopping = true;
        var hintsClosed = Hints.TryClose();
        var interactiveClosed = InteractiveHints.TryClose();
        var eventsClosed = Events.TryClose();
        var result = Manager.TryClose();
        if (result.IsSuccess && hintsClosed.IsSuccess && interactiveClosed.IsSuccess && eventsClosed.IsSuccess)
        {
            anchors.Clear(); replacingRequests.Clear();
            Manager.PresentedFrameApplied -= GroupFrameApplied;
            Events.FrameApplied -= EnforceStack; Hints.FrameApplied -= EnforceStack; InteractiveHints.FrameApplied -= EnforceStack;
        }
        return !hintsClosed.IsSuccess ? hintsClosed : !interactiveClosed.IsSuccess ? interactiveClosed : !eventsClosed.IsSuccess ? eventsClosed : result;
    }
    public void Dispose() { TryClose(); Hints.Dispose(); InteractiveHints.Dispose(); Events.Dispose(); Manager.Dispose(); }
    private sealed record AnchorOwner(FlyoutEntryKey Entry, string SessionId, Func<PixelRect?> Read);
}

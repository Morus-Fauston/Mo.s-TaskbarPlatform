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
    internal TaskbarFlyoutManager Manager { get; }

    internal TaskbarFlyoutCoordinator(HostBrokerSession session, IslandDisplayAdapter island,
        Func<IReadOnlyList<TaskbarDockDisplay>> displays, RegisteredImageCache images, Action<string, object?> record)
    {
        this.session = session; this.island = island; this.displays = displays; this.record = record;
        replacingRequests = new(session.States.FlyoutRequests);
        Manager = new(session.States, (key, templateId, navigate) => new TemplateRenderer(
            new TemplateInteractionController(session.States, key.ApplicationId, key.Entry,
                (slot, parameter, expected, token) => session.SendActionAsync(slot, parameter, token, expected), templateId, navigate), images), record);
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
        if (request.Kind != FlyoutKind.TaskbarGroup) return ProtocolResult.Reject("FlyoutUnavailable", "该浮窗类型尚未接入窗口");
        if (!session.States.FlyoutRequests.IsEntryEnabled(message.ApplicationId, message.SessionId, request.FeatureGroupId, request.EntryId))
            return ProtocolResult.Reject("EntryDisabled", "入口显示已关闭");
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
        var result = Manager.TryClose();
        if (result.IsSuccess) { anchors.Clear(); replacingRequests.Clear(); }
        return result;
    }
    public void Dispose() { Manager.Dispose(); anchors.Clear(); replacingRequests.Clear(); }
    private sealed record AnchorOwner(FlyoutEntryKey Entry, string SessionId, Func<PixelRect?> Read);
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mtp.Contracts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

internal delegate Task<ProtocolResult> InteractiveHintActionSender(ShortHintRequest origin,
    ActionSlotReference slot, ActionParameter parameter, string expectedSession, CancellationToken token);

/// <summary>UI-thread owner of bounded, interactive hints. No input observer or business replay.</summary>
internal sealed class InteractiveHintManager : IDisposable
{
    internal const int MaximumHints = 64;
    private readonly BrokerStateStore states;
    private readonly TaskbarFlyoutManager groups;
    private readonly RegisteredImageCache images;
    private readonly InteractiveHintActionSender sender;
    private readonly Func<ShortHintRequest, HintExpansionTarget, ProtocolResult> expand;
    private readonly Action<string, object?> record;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly Dictionary<long, Instance> hints = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Windows.UI.ViewManagement.UISettings ui = new();
    private HostSettingsPreferences preferences = new(new(), []);
    private HostAppearancePreferences appearance = new();
    private long generation;
    private bool stopping, ticking;
    private int diagnosticCount;
    internal bool? ReducedMotionOverride { get; set; }
    internal Func<int> AdditionalInstanceCount { get; set; } = () => 0;
    internal bool HasResources => hints.Count > 0;
    internal InteractiveHintWindow? WindowForTesting(long generation) => hints.GetValueOrDefault(generation)?.Window;
    internal IReadOnlyList<ShortHintObservation> Inspect() => hints.Values.Select(x => new ShortHintObservation(
        x.Request, x.Generation, x.Window?.Handle ?? 0, x.Window?.LastBounds ?? default, x.Closing, null, x.Mode)).ToArray();
    private TimeSpan Now => clock.GetElapsedTime(started, clock.GetTimestamp());

    internal InteractiveHintManager(BrokerStateStore states, TaskbarFlyoutManager groups, RegisteredImageCache images,
        InteractiveHintActionSender sender, Func<ShortHintRequest, HintExpansionTarget, ProtocolResult> expand,
        Action<string, object?> record, TimeProvider? clock = null)
    {
        this.states = states; this.groups = groups; this.images = images; this.sender = sender; this.expand = expand;
        this.record = (name, value) => { if (diagnosticCount >= 4096) return; diagnosticCount++; try { record(name, value); } catch (Exception) { } };
        this.clock = clock ?? TimeProvider.System; started = this.clock.GetTimestamp();
        timer.Tick += Tick;
        groups.PresentedFrameApplied += GroupFrameApplied;
    }

    private void GroupFrameApplied(string screenId, long ownerGeneration)
    {
        foreach (var instance in hints.Values.Where(x => x.Request.Owner is { } owner && owner.ScreenId == screenId && owner.Generation == ownerGeneration).ToArray())
        {
            if (instance.CleanupPending || instance.Reflowing) continue;
            if (groups.EnvironmentForHint(screenId, ownerGeneration) is { } environment)
                instance.Request = instance.Request with { WorkArea = environment.WorkArea, Dpi = environment.Dpi };
            if (!Available(instance.Request)) FinishClose(instance);
            else if (!instance.Closing && groups.BoundsForHint(screenId, ownerGeneration) != instance.OwnerLayoutBounds) SafeReflow(instance);
            else Advance(instance);
        }
    }

    internal void ApplySettings(HostSettingsPreferences value)
    {
        preferences = value;
        foreach (var instance in hints.Values.ToArray())
        {
            if (instance.CleanupPending) continue;
            if (!Enabled(instance.Request)) { FinishClose(instance); continue; }
            if (!instance.Closing && !instance.CleanupPending) SafeReflow(instance);
        }
    }
    internal void ApplyAppearance(HostAppearancePreferences value)
    {
        appearance = value;
        foreach (var instance in hints.Values) instance.Window?.ApplyAppearance(value);
    }
    private bool Enabled(ShortHintRequest request)
    {
        // Host action failures have no app-declared hint switch. Their reason also remains in settings.
        if (request.Entry.Entry.Kind != TemplateEntryKind.Hint) return true;
        var id = new StableIdentity(new StableId(request.Entry.ApplicationId)).CreateChild(new(request.Entry.Entry.FeatureGroupId)).CreateChild(new(request.Entry.Entry.EntryId));
        return preferences.HintVisibility?.GetValueOrDefault(HostSettingsController.IdentityKey(id), true) != false &&
            states.FlyoutRequests.IsEntryEnabled(request.Entry.ApplicationId, request.SessionId,
                request.Entry.Entry.FeatureGroupId, request.Entry.Entry.EntryId);
    }
    private bool Current(Instance instance) => hints.GetValueOrDefault(instance.Generation) == instance;
    private bool Available(ShortHintRequest request)
    {
        var snapshot = states.GetSnapshot(request.Entry.ApplicationId);
        if (snapshot?.SessionId != request.SessionId || !snapshot.IsConnected || !snapshot.IsInteractive ||
            request.Entry.Entry.Kind != TemplateEntryKind.Hint ||
            snapshot.Declaration?.FlyoutEntries.Any(x => x.Identity.Segments[1].Value == request.Entry.Entry.FeatureGroupId &&
                x.Identity.Segments[2].Value == request.Entry.Entry.EntryId && x.Kind == FlyoutKind.InteractiveHint) != true) return false;
        if (request.Owner is not { } owner) return true;
        if (owner.SessionId != request.SessionId || owner.Entry.ApplicationId != request.Entry.ApplicationId ||
            owner.Entry.Entry.FeatureGroupId != request.Entry.Entry.FeatureGroupId || owner.ScreenId != request.ScreenId) return false;
        return groups.Inspect().Any(x => x.ScreenId == owner.ScreenId && x.Generation == owner.Generation &&
            x.Entry == owner.Entry && x.SessionId == owner.SessionId && !x.Closing);
    }

    internal ProtocolResult Show(ShortHintRequest request)
    {
        if (stopping) return ProtocolResult.Reject("HintClosed", "短提示正在关闭");
        if (request is null || request.Entry is null || request.Entry.Entry is null ||
            !request.WorkArea.IsValid || request.Dpi is < 48 or > 960 || !Enum.IsDefined(request.Position) ||
            string.IsNullOrWhiteSpace(request.ScreenId) || request.ScreenId.Length > 256)
            return ProtocolResult.Reject("InvalidHint", "短提示参数无效");
        if (!Available(request)) return ProtocolResult.Reject("StaleHint", "短提示所属会话或浮窗组已结束");
        if (!Enabled(request)) return ProtocolResult.Reject("EntryDisabled", "短提示显示已关闭，详情可在设置查看");
        var existing = hints.Values.FirstOrDefault(x => x.Request.SessionId == request.SessionId &&
            x.Request.Owner == request.Owner && x.Request.Entry == request.Entry && x.Request.ScreenId == request.ScreenId);
        if (existing is { Closing: false })
        {
            existing.Request = request;
            existing.Lifetime.Refresh(existing.Generation);
            SafeReflow(existing); record("interactive-hint-refreshed", new { existing.Generation, request.Entry });
            return Current(existing) && !existing.Closing ? ProtocolResult.Success("Refreshed") : ProtocolResult.Reject("HintLayoutFailed", "提示刷新未完成");
        }
        if (existing is not null && !FinishClose(existing).IsSuccess) return ProtocolResult.Reject("HintCleanupPending", "旧提示关闭尚未完成");
        if (hints.Count + AdditionalInstanceCount() >= MaximumHints) return ProtocolResult.Reject("HintCapacityExceeded", "短提示数量已达预算");
        var instance = new Instance(request, checked(++generation), clock);
        hints.Add(instance.Generation, instance);
        try
        {
            var declared = states.GetSnapshot(request.Entry.ApplicationId)?.Declaration?.Templates.FirstOrDefault(x => x.Entry == request.Entry.Entry)?.Declaration;
            if (declared is null) throw new InvalidOperationException("可交互提示缺少已确认模板");
            instance.Renderer = new(new TemplateInteractionController(states, request.Entry.ApplicationId, request.Entry.Entry,
                (slot, parameter, expected, token) => Current(instance) && !instance.Closing && Available(instance.Request)
                    ? sender(instance.Request, slot, parameter, expected, token)
                    : Task.FromResult(ProtocolResult.Reject("StaleHint", "提示来源已失效")),
                navigate: intent => Navigate(instance, intent)), images, TemplateRenderSurface.HintForeground);
            instance.Background = new(new TemplateInteractionController(states, request.Entry.ApplicationId, request.Entry.Entry,
                (_, _, _, _) => Task.FromResult(ProtocolResult.Reject("HintReadOnly", "背景不接收动作"))),
                images, TemplateRenderSurface.HintBackground);
            instance.Content = instance.Renderer;
            instance.Window = new(instance.Renderer, instance.Background, () => OnClosed(instance), record,
                (source, active) =>
                {
                    if (!Current(instance) || instance.Closing) return;
                    if (instance.Window?.IsVisible == true) instance.Lifetime.MarkVisible(instance.Generation);
                    instance.Lifetime.SetInteraction(instance.Generation, source, active);
                });
            if (request.Owner is { } owner)
            {
                var linked = groups.AssociateInteractionWindow(owner.ScreenId, owner.Generation, instance.Window.Handle, true);
                if (!linked.IsSuccess) throw new InvalidOperationException(linked.Error!.Message);
                instance.AssociatedHandle = instance.Window.Handle;
            }
            instance.Window.ApplyAppearance(appearance);
            Reflow(instance);
            if (!Current(instance) || instance.Closing || instance.CleanupPending || instance.Window?.IsAlive != true)
                return ProtocolResult.Reject("HintCreateFailed", "短提示未能完成原生呈现");
            timer.Start();
            record("interactive-hint-opened", new { instance.Generation, request.Entry, request.SessionId, request.ScreenId, request.Owner });
            return ProtocolResult.Success("Displayed");
        }
        catch (InteractiveHintWindowCreationException error)
        {
            instance.Window = error.Owner; FinishClose(instance);
            return ProtocolResult.Reject("HintCreateFailed", "短提示窗口创建失败，保留清理所有权");
        }
        catch (Exception error)
        {
            record("interactive-hint-create-failed", error.Message); FinishClose(instance);
            return ProtocolResult.Reject("HintCreateFailed", "短提示创建或定位失败");
        }
    }

    private ProtocolResult Navigate(Instance instance, TemplateNavigationIntent intent)
    {
        if (!Current(instance) || instance.Closing || !Available(instance.Request) ||
            instance.Renderer?.IsCurrentNavigation(intent) != true || intent.Action.Kind != TemplateActionKind.ExpandHint)
            return ProtocolResult.Reject("StaleHint", "提示展开来源已失效");
        var target = states.GetSnapshot(intent.ApplicationId)?.Declaration?.FlyoutEntries.FirstOrDefault(x =>
            x.Identity.Segments[1].Value == intent.Entry.FeatureGroupId && x.Identity.Segments[2].Value == intent.Entry.EntryId)?.Expansion;
        if (target is null) return ProtocolResult.Reject("UnknownPanel", "提示未声明展开目标");
        return expand(instance.Request, target);
    }

    internal bool Focus(long expectedGeneration) => hints.TryGetValue(expectedGeneration, out var instance) &&
        !instance.Closing && Available(instance.Request) && instance.Window?.FocusFirst() == true;

    internal void Refresh(Func<string, TaskbarDockDisplay?>? display = null)
    {
        foreach (var instance in hints.Values.ToArray())
        {
            if (instance.CleanupPending) continue;
            if (!Available(instance.Request) || !Enabled(instance.Request)) { FinishClose(instance); continue; }
            if (instance.Closing) continue;
            if (display is not null)
            {
                if (display(instance.Request.ScreenId) is not { } current) { FinishClose(instance); continue; }
                instance.Request = instance.Request with { WorkArea = current.WorkArea, Dpi = current.Dpi };
            }
            SafeReflow(instance);
        }
    }
    private void SafeReflow(Instance instance)
    {
        try { Reflow(instance); }
        catch (Exception error) { record("interactive-hint-layout-failed", new { instance.Generation, error.Message }); FinishClose(instance); }
    }
    private void Reflow(Instance instance)
    {
        if (instance.Reflowing) return;
        instance.Reflowing = true;
        try { ReflowCore(instance); } finally { instance.Reflowing = false; }
    }
    private void ReflowCore(Instance instance)
    {
        instance.Renderer?.Refresh(); instance.Window?.RefreshAppearance();
        var request = instance.Request;
        var work = FlyoutNative.ToDip(request.WorkArea, request.Dpi);
        var owner = request.Owner is { } owned ? groups.BoundsForHint(owned.ScreenId, owned.Generation) : null;
        if (request.Owner is not null && owner is null) throw new InvalidOperationException("所属浮窗组已结束");
        double width = owner?.Width ?? Math.Min(320, work.Width - 32);
        instance.Content!.Measure(new(Math.Max(1, width), double.PositiveInfinity));
        double height = Math.Max(48, instance.Content.DesiredSize.Height);
        if (height > 160 || height > work.Height - 32)
            throw new InvalidOperationException("可交互提示无法在当前工作区完整呈现必要控件");
        var hintsPreference = preferences.Hints ?? new();
        var position = hintsPreference.AllowApplicationPosition && request.Position != FlyoutPosition.Default ? request.Position : hintsPreference.DefaultPosition;
        var result = HintLayout.Calculate(work, width, height, Position(position), owner);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        if (result.Value!.Mode == HintPlacementMode.GroupReflow && request.Owner is { } group)
        {
            if (!groups.ReserveHintSpace(group.ScreenId, group.Generation, height + HintLayout.GapDip, Reservation(instance))) throw new InvalidOperationException("所属组无法为提示保留空间");
            owner = groups.BoundsForHint(group.ScreenId, group.Generation);
            result = HintLayout.Calculate(work, width, height, Position(position), owner);
            if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        }
        instance.Mode = result.Value!.Mode;
        instance.OwnerLayoutBounds = owner;
        // Associated motion is local to its group. The group's actual native frame supplies
        // translation and width; the hint independently animates its relative position/opacity.
        var targetBounds = owner is { } ownerTarget ? result.Value.Bounds with
            { X = 0, Y = result.Value.Bounds.Y - ownerTarget.Y } : result.Value.Bounds;
        instance.Target = new("hint", targetBounds);
        instance.Animation.Retarget([instance.Target], Now, ReduceMotion(), instance.Animation.Generation, presentedFrame: instance.LastFrame);
        Advance(instance);
    }
    private static HintPosition Position(FlyoutPosition value) => value switch
    {
        FlyoutPosition.TopLeft => HintPosition.TopLeft, FlyoutPosition.TopCenter => HintPosition.TopCenter,
        FlyoutPosition.TopRight => HintPosition.TopRight, FlyoutPosition.BottomLeft => HintPosition.BottomLeft,
        FlyoutPosition.BottomCenter => HintPosition.BottomCenter, FlyoutPosition.BottomRight => HintPosition.BottomRight,
        FlyoutPosition.Center => HintPosition.Center, _ => HintPosition.LowerCenter
    };
    private bool ReduceMotion()
    {
        if (ReducedMotionOverride is { } value) return value;
        try { return !ui.AnimationsEnabled; } catch (System.Runtime.InteropServices.COMException) { return true; }
    }
    private void Tick(object? sender, object args)
    {
        if (ticking) return; ticking = true;
        try
        {
            foreach (var instance in hints.Values.ToArray())
            {
                if (instance.CleanupPending) continue;
                if (instance.NativeEnded) { FinishClose(instance); continue; }
                if (!Available(instance.Request) || !Enabled(instance.Request)) { FinishClose(instance); continue; }
                if (!instance.Closing) instance.Window?.RefreshInteraction();
                if (!instance.Closing && instance.Lifetime.IsExpired(instance.Generation)) BeginClose(instance);
                else Advance(instance);
            }
        }
        finally { ticking = false; if (!hints.Values.Any(x => !x.CleanupPending)) timer.Stop(); }
    }
    private void Advance(Instance instance)
    {
        if (!Current(instance) || instance.Window is null || instance.CleanupPending) return;
        if (instance.NativeEnded) { FinishClose(instance); return; }
        try
        {
            if (ReduceMotion() && instance.Target is { } target)
                instance.Animation.Retarget([target], Now, true, instance.Animation.Generation);
            var frame = instance.Animation.Sample(Now);
            if (frame.Panels.FirstOrDefault() is not { } panel) return;
            var bounds = panel.Bounds;
            if (instance.Request.Owner is { } owner && groups.PresentedBoundsForHint(owner.ScreenId, owner.Generation) is { } ownerBounds)
            {
                bounds = bounds with { X = ownerBounds.X, Width = ownerBounds.Width, Y = ownerBounds.Y + bounds.Y };
            }
            var pixels = FlyoutNative.ToPixels(bounds, instance.Request.Dpi);
            byte alpha = (byte)Math.Round(panel.Opacity * 255);
            if (instance.AppliedBounds != pixels || instance.AppliedAlpha != alpha)
            {
                instance.Window.Apply(pixels, panel.Opacity); instance.Window.Show();
                instance.AppliedBounds = pixels; instance.AppliedAlpha = alpha;
            }
            instance.LastFrame = frame;
            if (panel.Opacity > 0 && instance.Window.IsVisible && !instance.Closing) instance.Lifetime.MarkVisible(instance.Generation);
            if (frame.IsComplete && instance.Closing) FinishClose(instance);
        }
        catch (Exception error) { record("interactive-hint-frame-failed", new { instance.Generation, error.Message }); FinishClose(instance); }
    }
    private void BeginClose(Instance instance)
    {
        if (!Current(instance) || instance.Closing) return;
        instance.Closing = true; instance.Window?.StopInteraction(); instance.Renderer?.StopInteraction();
        var panel = instance.LastFrame?.Panels.FirstOrDefault();
        if (panel is null) { FinishClose(instance); return; }
        instance.Target = panel with { Bounds = panel.Bounds with { Y = panel.Bounds.Y + 8 }, Opacity = 0 };
        instance.Animation.Retarget([instance.Target], Now, ReduceMotion(), instance.Animation.Generation, presentedFrame: instance.LastFrame);
        Advance(instance);
    }
    private void OnClosed(Instance instance)
    {
        instance.NativeEnded = true;
        instance.Closing = true;
        // WinUI Closed is synchronous during TryClose. The retained owner completes after return.
        timer.Start();
    }
    private CoreResult<bool> FinishClose(Instance instance)
    {
        if (!Current(instance)) return CoreResult<bool>.Success(true);
        instance.Closing = true; instance.Window?.StopInteraction(); instance.Renderer?.StopInteraction();
        var result = instance.Window?.TryClose() ?? CoreResult<bool>.Success(true);
        if (!result.IsSuccess) { instance.CleanupPending = true; record("interactive-hint-cleanup-pending", new { instance.Generation, result.Error }); return result; }
        instance.Renderer?.Dispose(); instance.Background?.Dispose(); instance.Animation.Clear(); hints.Remove(instance.Generation);
        if (instance.Request.Owner is { } owner)
        {
            groups.AssociateInteractionWindow(owner.ScreenId, owner.Generation, instance.AssociatedHandle, false);
            groups.ReserveHintSpace(owner.ScreenId, owner.Generation, 0, Reservation(instance));
        }
        record("interactive-hint-closed", new { instance.Generation });
        if (hints.Count == 0) timer.Stop();
        return CoreResult<bool>.Success(true);
    }
    internal CoreResult<bool> TryClose()
    {
        stopping = true; timer.Stop();
        CoreResult<bool> result = CoreResult<bool>.Success(true);
        foreach (var instance in hints.Values.ToArray()) { var closed = FinishClose(instance); if (!closed.IsSuccess) result = closed; }
        if (result.IsSuccess) groups.PresentedFrameApplied -= GroupFrameApplied;
        return result;
    }
    public void Dispose() { TryClose(); if (hints.Count == 0) timer.Tick -= Tick; }
    private static string Reservation(Instance instance) => "interactive:" + instance.Generation;
    private sealed class Instance(ShortHintRequest request, long generation, TimeProvider clock)
    {
        internal bool NativeEnded;
        internal ShortHintRequest Request = request;
        internal readonly long Generation = generation;
        internal readonly FlyoutLifetime Lifetime = new(generation, FlyoutLifetimeKind.InteractiveHint, clock: clock);
        internal readonly FlyoutRectangleAnimation Animation = new();
        internal InteractiveHintWindow? Window;
        internal FrameworkElement? Content;
        internal TemplateRenderer? Renderer;
        internal TemplateRenderer? Background;
        internal nint AssociatedHandle;
        internal FlyoutRectangleFrame? LastFrame;
        internal FlyoutRectangleTarget? Target;
        internal HintPlacementMode Mode;
        internal TaskbarDipRect? OwnerLayoutBounds;
        internal PixelRect? AppliedBounds;
        internal byte? AppliedAlpha;
        internal bool Closing, CleanupPending, Reflowing;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mtp.Contracts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

internal sealed record HintOwner(string ScreenId, long Generation, FlyoutEntryKey Entry, string SessionId);
internal sealed record ShortHintRequest(FlyoutEntryKey Entry, string SessionId, string ScreenId, PixelRect WorkArea,
    uint Dpi, FlyoutPosition Position = FlyoutPosition.Default, HintOwner? Owner = null);
internal sealed record ShortHintObservation(ShortHintRequest Request, long Generation, nint Handle, PixelRect Bounds,
    bool Closing, string? Text, HintPlacementMode Mode);

/// <summary>UI-thread owner of bounded, ordinary hints. No input observer or business replay.</summary>
internal sealed class ShortHintManager : IDisposable
{
    internal const int MaximumHints = 64;
    private readonly BrokerStateStore states;
    private readonly TaskbarFlyoutManager groups;
    private readonly EventGroupManager? events;
    private readonly RegisteredImageCache images;
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
    internal event Action? FrameApplied;
    internal bool HasResources => hints.Count > 0;
    internal ShortHintWindow? WindowForTesting(long generation) => hints.GetValueOrDefault(generation)?.Window;
    internal IReadOnlyList<ShortHintObservation> Inspect() => hints.Values.Select(x => new ShortHintObservation(
        x.Request, x.Generation, x.Window?.Handle ?? 0, x.Window?.LastBounds ?? default, x.Closing, x.Text?.Text, x.Mode)).ToArray();
    private TimeSpan Now => clock.GetElapsedTime(started, clock.GetTimestamp());

    internal ShortHintManager(BrokerStateStore states, TaskbarFlyoutManager groups, RegisteredImageCache images,
        Action<string, object?> record, TimeProvider? clock = null, EventGroupManager? events = null)
    {
        this.states = states; this.groups = groups; this.images = images;
        this.events = events;
        this.record = (name, value) => { if (diagnosticCount >= 4096) return; diagnosticCount++; try { record(name, value); } catch (Exception) { } };
        this.clock = clock ?? TimeProvider.System; started = this.clock.GetTimestamp();
        timer.Tick += Tick;
        groups.PresentedFrameApplied += GroupFrameApplied;
        if (events is not null) events.PresentedFrameApplied += EventFrameApplied;
    }

    private void GroupFrameApplied(string screenId, long ownerGeneration)
    {
        foreach (var owner in hints.Values.Select(x => x.Request.Owner).OfType<HintOwner>().Where(owner =>
            owner.Entry.Entry.Kind == TemplateEntryKind.TaskbarFlyout && owner.ScreenId == screenId && owner.Generation == ownerGeneration).Distinct().ToArray())
            OwnerFrameApplied(owner);
    }
    private void EventFrameApplied(HintOwner owner) => OwnerFrameApplied(owner);
    private void OwnerFrameApplied(HintOwner owner)
    {
        foreach (var instance in hints.Values.Where(x => x.Request.Owner == owner).ToArray())
        {
            if (instance.CleanupPending || instance.Reflowing) continue;
            if (OwnerEnvironment(owner) is { } environment)
                instance.Request = instance.Request with { WorkArea = environment.WorkArea, Dpi = environment.Dpi };
            if (!Available(instance.Request)) FinishClose(instance);
            else if (!instance.Closing && OwnerBounds(owner) != instance.OwnerLayoutBounds) SafeReflow(instance);
            else Advance(instance);
        }
    }
    private bool OwnerAvailable(HintOwner owner) => owner.Entry.Entry.Kind switch
    {
        TemplateEntryKind.EventChannel => events?.BoundsForHint(owner) is not null,
        TemplateEntryKind.TaskbarFlyout => groups.Inspect().Any(x => x.ScreenId == owner.ScreenId && x.Generation == owner.Generation &&
            x.Entry == owner.Entry && x.SessionId == owner.SessionId && !x.Closing),
        _ => false
    };
    private TaskbarDipRect? OwnerBounds(HintOwner owner) => !OwnerAvailable(owner) ? null :
        owner.Entry.Entry.Kind == TemplateEntryKind.EventChannel ? events!.BoundsForHint(owner) : groups.BoundsForHint(owner.ScreenId, owner.Generation);
    private TaskbarDipRect? OwnerPresentedBounds(HintOwner owner) => !OwnerAvailable(owner) ? null :
        owner.Entry.Entry.Kind == TemplateEntryKind.EventChannel ? events!.PresentedBoundsForHint(owner) : groups.PresentedBoundsForHint(owner.ScreenId, owner.Generation);
    private (PixelRect WorkArea, uint Dpi)? OwnerEnvironment(HintOwner owner) => !OwnerAvailable(owner) ? null :
        owner.Entry.Entry.Kind == TemplateEntryKind.EventChannel ? events!.EnvironmentForHint(owner) : groups.EnvironmentForHint(owner.ScreenId, owner.Generation);
    private bool ReserveOwnerSpace(HintOwner owner, double height) => OwnerAvailable(owner) &&
        (owner.Entry.Entry.Kind == TemplateEntryKind.EventChannel ? events!.ReserveHintSpace(owner, height) :
            groups.ReserveHintSpace(owner.ScreenId, owner.Generation, height));

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
        if (snapshot?.SessionId != request.SessionId || !snapshot.IsConnected || !snapshot.IsInteractive) return false;
        if (request.Owner is not { } owner) return true;
        return owner.Entry.ApplicationId == request.Entry.ApplicationId && owner.SessionId == request.SessionId &&
            owner.ScreenId == request.ScreenId && OwnerAvailable(owner);
    }

    internal ProtocolResult Show(ShortHintRequest request, string? errorText = null)
    {
        if (stopping) return ProtocolResult.Reject("HintClosed", "短提示正在关闭");
        if (request is null || request.Entry is null || request.Entry.Entry is null ||
            !request.WorkArea.IsValid || request.Dpi is < 48 or > 960 || !Enum.IsDefined(request.Position) ||
            string.IsNullOrWhiteSpace(request.ScreenId) || request.ScreenId.Length > 256 ||
            errorText is { Length: > 1024 }) return ProtocolResult.Reject("InvalidHint", "短提示参数无效");
        if (!Available(request)) return ProtocolResult.Reject("StaleHint", "短提示所属会话或浮窗组已结束");
        if (!Enabled(request)) return ProtocolResult.Reject("EntryDisabled", "短提示显示已关闭，详情可在设置查看");
        var existing = hints.Values.FirstOrDefault(x => x.Request.SessionId == request.SessionId &&
            (request.Owner is not null ? x.Request.Owner == request.Owner : x.Request.Owner is null && x.Request.Entry == request.Entry && x.Request.ScreenId == request.ScreenId));
        if (existing is { Closing: false })
        {
            existing.Request = request;
            if (existing.Text is not null && errorText is not null)
            {
                existing.Text.Text = errorText;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(existing.Text, errorText);
            }
            existing.Lifetime.Refresh(existing.Generation);
            SafeReflow(existing); record("hint-refreshed", new { existing.Generation, request.Entry });
            return Current(existing) && !existing.Closing ? ProtocolResult.Success("Refreshed") : ProtocolResult.Reject("HintLayoutFailed", "提示刷新未完成");
        }
        if (existing is not null && !FinishClose(existing).IsSuccess) return ProtocolResult.Reject("HintCleanupPending", "旧提示关闭尚未完成");
        if (hints.Count + AdditionalInstanceCount() >= MaximumHints) return ProtocolResult.Reject("HintCapacityExceeded", "短提示数量已达预算");
        var instance = new Instance(request, checked(++generation), clock);
        hints.Add(instance.Generation, instance);
        try
        {
            FrameworkElement content;
            var declared = states.GetSnapshot(request.Entry.ApplicationId)?.Declaration?.Templates.FirstOrDefault(x => x.Entry == request.Entry.Entry)?.Declaration;
            if (errorText is null && declared is not null)
            {
                instance.Renderer = new(new TemplateInteractionController(states, request.Entry.ApplicationId, request.Entry.Entry,
                    (_, _, _, _) => Task.FromResult(ProtocolResult.Reject("HintReadOnly", "普通提示不接收动作"))), images);
                instance.Renderer.IsEnabled = false;
                content = instance.Renderer;
            }
            else
            {
                instance.Text = new TextBlock { Text = errorText ?? request.Entry.Entry.EntryId, TextWrapping = TextWrapping.Wrap,
                    MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(instance.Text, instance.Text.Text);
                content = instance.Text;
            }
            instance.Content = content;
            instance.Window = new(content, () => OnClosed(instance), record);
            instance.Window.ApplyAppearance(appearance);
            Reflow(instance);
            if (!Current(instance) || instance.Closing || instance.CleanupPending || instance.Window?.IsAlive != true)
                return ProtocolResult.Reject("HintCreateFailed", "短提示未能完成原生呈现");
            timer.Start();
            record("hint-opened", new { instance.Generation, request.Entry, request.SessionId, request.ScreenId, request.Owner });
            return ProtocolResult.Success("Displayed");
        }
        catch (ShortHintWindowCreationException error)
        {
            instance.Window = error.Owner; FinishClose(instance);
            return ProtocolResult.Reject("HintCreateFailed", "短提示窗口创建失败，保留清理所有权");
        }
        catch (Exception error)
        {
            record("hint-create-failed", error.Message); FinishClose(instance);
            return ProtocolResult.Reject("HintCreateFailed", "短提示创建或定位失败");
        }
    }

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
        catch (Exception error) { record("hint-layout-failed", new { instance.Generation, error.Message }); FinishClose(instance); }
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
        var owner = request.Owner is { } owned ? OwnerBounds(owned) : null;
        if (request.Owner is not null && owner is null) throw new InvalidOperationException("所属浮窗组已结束");
        double width = owner?.Width ?? Math.Min(320, work.Width - 32);
        instance.Content!.Measure(new(Math.Max(1, width), Math.Max(1, Math.Min(160, work.Height - 32))));
        double height = Math.Clamp(instance.Content.DesiredSize.Height, 48, 160);
        var hintsPreference = preferences.Hints ?? new();
        var position = hintsPreference.AllowApplicationPosition && request.Position != FlyoutPosition.Default ? request.Position : hintsPreference.DefaultPosition;
        var result = HintLayout.Calculate(work, width, height, Position(position), owner);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        if (result.Value!.Mode == HintPlacementMode.GroupReflow && request.Owner is { } group)
        {
            if (!ReserveOwnerSpace(group, height + HintLayout.GapDip)) throw new InvalidOperationException("所属组无法为提示保留空间");
            owner = OwnerBounds(group);
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
                if (!Available(instance.Request) || !Enabled(instance.Request)) { FinishClose(instance); continue; }
                if (!instance.Closing && instance.Lifetime.IsExpired(instance.Generation)) BeginClose(instance);
                else Advance(instance);
            }
        }
        finally { ticking = false; if (!hints.Values.Any(x => !x.CleanupPending)) timer.Stop(); }
    }
    private void Advance(Instance instance)
    {
        if (!Current(instance) || instance.Window is null || instance.CleanupPending) return;
        try
        {
            if (ReduceMotion() && instance.Target is { } target)
                instance.Animation.Retarget([target], Now, true, instance.Animation.Generation);
            var frame = instance.Animation.Sample(Now);
            if (frame.Panels.FirstOrDefault() is not { } panel) return;
            var bounds = panel.Bounds;
            if (instance.Request.Owner is { } owner && OwnerPresentedBounds(owner) is { } ownerBounds)
            {
                bounds = bounds with { X = ownerBounds.X, Width = ownerBounds.Width, Y = ownerBounds.Y + bounds.Y };
            }
            var pixels = FlyoutNative.ToPixels(bounds, instance.Request.Dpi);
            byte alpha = (byte)Math.Round(panel.Opacity * 255);
            if (instance.AppliedBounds != pixels || instance.AppliedAlpha != alpha)
            {
                instance.Window.Apply(pixels, panel.Opacity); instance.Window.Show();
                instance.AppliedBounds = pixels; instance.AppliedAlpha = alpha;
                EmitFrame();
            }
            instance.LastFrame = frame;
            if (panel.Opacity > 0 && instance.Window.IsVisible && !instance.Closing) instance.Lifetime.MarkVisible(instance.Generation);
            if (frame.IsComplete && instance.Closing) FinishClose(instance);
        }
        catch (Exception error) { record("hint-frame-failed", new { instance.Generation, error.Message }); FinishClose(instance); }
    }
    private void BeginClose(Instance instance)
    {
        if (!Current(instance) || instance.Closing) return;
        instance.Closing = true; instance.Renderer?.StopInteraction();
        var panel = instance.LastFrame?.Panels.FirstOrDefault();
        if (panel is null) { FinishClose(instance); return; }
        instance.Target = panel with { Bounds = panel.Bounds with { Y = panel.Bounds.Y + 8 }, Opacity = 0 };
        instance.Animation.Retarget([instance.Target], Now, ReduceMotion(), instance.Animation.Generation, presentedFrame: instance.LastFrame);
        Advance(instance);
    }
    private void OnClosed(Instance instance)
    {
        instance.Closing = true;
        // WinUI Closed is synchronous during TryClose. The retained owner completes after return.
        timer.Start();
    }
    private CoreResult<bool> FinishClose(Instance instance)
    {
        if (!Current(instance)) return CoreResult<bool>.Success(true);
        instance.Closing = true; instance.Renderer?.StopInteraction();
        var result = instance.Window?.TryClose() ?? CoreResult<bool>.Success(true);
        if (!result.IsSuccess) { instance.CleanupPending = true; record("hint-cleanup-pending", new { instance.Generation, result.Error }); return result; }
        instance.Renderer?.Dispose(); instance.Animation.Clear(); hints.Remove(instance.Generation);
        if (instance.Request.Owner is { } owner) ReserveOwnerSpace(owner, 0);
        record("hint-closed", new { instance.Generation });
        EmitFrame();
        if (hints.Count == 0) timer.Stop();
        return CoreResult<bool>.Success(true);
    }
    private void EmitFrame()
    {
        if (FrameApplied is not { } listeners) return;
        foreach (Action listener in listeners.GetInvocationList())
            try { listener(); } catch (Exception error) { record("hint-frame-observer-failed", error.GetType().Name); }
    }
    internal CoreResult<bool> TryClose()
    {
        stopping = true; timer.Stop();
        CoreResult<bool> result = CoreResult<bool>.Success(true);
        foreach (var instance in hints.Values.ToArray()) { var closed = FinishClose(instance); if (!closed.IsSuccess) result = closed; }
        if (result.IsSuccess)
        {
            groups.PresentedFrameApplied -= GroupFrameApplied;
            if (events is not null) events.PresentedFrameApplied -= EventFrameApplied;
        }
        return result;
    }
    public void Dispose() { TryClose(); if (hints.Count == 0) timer.Tick -= Tick; }
    private sealed class Instance(ShortHintRequest request, long generation, TimeProvider clock)
    {
        internal ShortHintRequest Request = request;
        internal readonly long Generation = generation;
        internal readonly FlyoutLifetime Lifetime = new(generation, FlyoutLifetimeKind.ShortHint, clock: clock);
        internal readonly FlyoutRectangleAnimation Animation = new();
        internal ShortHintWindow? Window;
        internal FrameworkElement? Content;
        internal TemplateRenderer? Renderer;
        internal TextBlock? Text;
        internal FlyoutRectangleFrame? LastFrame;
        internal FlyoutRectangleTarget? Target;
        internal HintPlacementMode Mode;
        internal TaskbarDipRect? OwnerLayoutBounds;
        internal PixelRect? AppliedBounds;
        internal byte? AppliedAlpha;
        internal bool Closing, CleanupPending, Reflowing;
    }
}

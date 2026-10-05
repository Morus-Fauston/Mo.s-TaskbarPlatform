using Mtp.Platform.Core;

namespace Mtp.Host.Islands;

internal sealed record IslandTarget(nint Parent, string Identity, TaskbarDockGeometry Geometry, bool OwnedFixture = false, string? Warning = null);

internal sealed class IslandDisplayAdapter : IIslandSessionAdapter
{
    private readonly Func<TaskbarDockPreferences, CoreResult<IslandTarget>> capture;
    private readonly Action<string, object?> record;
    private readonly Func<Mtp.Contracts.ActionSlotReference, Task>? invokeAction;
    private readonly Func<HostComponentDisplayModel, Templates.TemplateRenderer?>? createTemplate;
    private readonly Func<ItemInteractionHandle, string?, Task>? activate;
    private readonly TaskbarGroupAnimation animation = new();
    private readonly TimerPresentationSampler timerReadings = new();
    private bool timersAdvancing;
    private IReadOnlyList<TimerDisplayReading> appliedTimerReadings = [];
    internal IReadOnlyList<TimerDisplayReading> GetTimerReadings() => appliedTimerReadings;
    internal bool TimerDriverRunning => frameTimer.IsEnabled;
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Microsoft.UI.Xaml.DispatcherTimer frameTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private bool reducedMotion;
    private bool groupMode;
    private bool animationFailed;
    private int rightGap;
    internal bool? ReducedMotionOverride { get; set; }
    internal HostGroupPresentationSnapshot? GroupSnapshot { get; private set; }
    internal TaskbarGroupAnimationFrame? GetGroupFrame() => lastFrame;
    private TaskbarGroupAnimationFrame? lastFrame;
    private ContentIslandHost? host;
    private IslandTarget? target;
    private PixelRect bounds;
    private bool placementCurrent;
    private HostComponentDisplayModel? component;
    private HostTestConfiguration config = new();
    private HostAppearancePreferences? appearance;
    public IslandDisplayAdapter(Func<TaskbarDockPreferences, CoreResult<IslandTarget>> capture, Action<string, object?> record,
        Func<Mtp.Contracts.ActionSlotReference, Task>? invokeAction = null,
        Func<HostComponentDisplayModel, Templates.TemplateRenderer?>? createTemplate = null,
        Func<ItemInteractionHandle, string?, Task>? activate = null)
    {
        this.capture = capture; this.record = record; this.invokeAction = invokeAction; this.createTemplate = createTemplate; this.activate = activate;
        frameTimer.Tick += (_, _) => AdvanceGroup();
    }
    public bool IsAlive => !animationFailed && host?.IsAlive == true;
    public string MaterialStatus => host?.MaterialStatus ?? "未创建内容岛";
    public bool PopupOpen => host?.PopupOpen == true;
    public nint Handle => host?.Handle ?? 0;
    public string TargetDescription { get; private set; } = "尚未采集";
    public string TargetSummary { get; private set; } = "尚未采集目标";
    public event Action? Lost;
    internal string? FailAfter { get; set; }

    public CoreResult<string> Prepare(TaskbarDockPreferences preferences, HostComponentDisplayModel? value, HostTestConfiguration configuration, bool unavailable)
    {
        groupMode = false;
        GroupSnapshot = null;
        frameTimer.Stop();
        return PrepareTarget(preferences, value, configuration, unavailable, null);
    }
    public CoreResult<string> PrepareGroup(TaskbarDockPreferences preferences, IReadOnlyList<HostComponentDisplayModel> components,
        HostTestConfiguration configuration, bool unavailable, BrokerStateStore? states, ItemPresentationController? presentations)
    {
        groupMode = true;
        return PrepareTarget(preferences, components.FirstOrDefault(value => value.IsVisible), configuration, unavailable,
            geometry =>
            {
                presentations?.UpdateScreens([geometry.DisplayId]);
                double available = Math.Max(0, ((long)(geometry.NotificationBounds?.X ?? geometry.TaskbarBounds.X) - geometry.TaskbarBounds.X) * 96d / Math.Max(1u, geometry.Dpi) - preferences.RightGapDip);
                return HostGroupPresentation.Build(components, states, presentations, geometry.DisplayId, availableWidthDip: available);
            });
    }
    private CoreResult<string> PrepareTarget(TaskbarDockPreferences preferences, HostComponentDisplayModel? value,
        HostTestConfiguration configuration, bool unavailable, Func<TaskbarDockGeometry, HostGroupPresentationSnapshot>? build)
    {
        component = value;
        config = configuration;
        rightGap = preferences.RightGapDip;
        placementCurrent = false;
        if (unavailable) { target = null; return CoreResult<string>.Failure(new("island_test_unavailable", "本会话模拟任务栏不可用；可清除模拟。")); }
        var result = capture(preferences);
        if (!result.IsSuccess) { target = null; return CoreResult<string>.Failure(result.Error!); }
        var next = result.Value!;
        TargetSummary = $"{next.Geometry.DisplayId} · DPI {next.Geometry.Dpi}";
        TargetDescription = $"{next.Geometry.DisplayId} · DPI {next.Geometry.Dpi} · 右侧间距 {preferences.RightGapDip} DIP · 通知区锚点 {next.Geometry.NotificationBounds?.X.ToString() ?? "不可用"} px · {next.Warning}";
        if (build is not null)
        {
            GroupSnapshot = build(next.Geometry);
            if (GroupSnapshot.Layout.Overflows) TargetDescription += " · 组宽超过可用区域，完整铺开可能重叠或越界。";
        }
        var placement = groupMode
            ? TaskbarGroupPlacement.Calculate(next.Geometry, Math.Max(96d / next.Geometry.Dpi, GroupSnapshot!.Layout.WidthDip), 32, preferences.RightGapDip)
            : TaskbarDockPlacement.Calculate(next.Geometry, new DipSize(configuration.Controls ? 320 : value?.HasTemplate == true ? 480 : 240, 32), preferences.RightGapDip);
        if (!placement.IsSuccess)
        {
            // During real auto-hide, keep an already healthy child attached to the same
            // parent. Windows moves/clips it; never replace that with synthetic visibility.
            var bar = next.Geometry.TaskbarBounds;
            var display = next.Geometry.DisplayBounds;
            var retracting = bar.Y >= display.Bottom - bar.Height && bar.Bottom > display.Bottom &&
                bar.X == display.X && bar.Width == display.Width;
            if (retracting && IsAlive && target?.Identity == next.Identity && target.Geometry.Dpi == next.Geometry.Dpi)
                return CoreResult<string>.Success(Key(next, preferences));
            target = null;
            return CoreResult<string>.Failure(placement.Error!);
        }
        target = next;
        bounds = placement.Value;
        placementCurrent = true;
        return CoreResult<string>.Success(Key(next, preferences));
    }
    private string Key(IslandTarget next, TaskbarDockPreferences preferences) => $"{next.Identity}|{next.Geometry.DisplayId}|{next.Geometry.Dpi}|{preferences.RightGapDip}|{groupMode}";
    public CoreResult<bool> Start()
    {
        animationFailed = false;
        if (target is null || component is null) return CoreResult<bool>.Failure(new("island_target_missing", "声明或任务栏定位条件不可用。"));
        if (host is not null) return CoreResult<bool>.Failure(new("island_cleanup_pending", "旧内容岛尚未释放。"));
        host = new ContentIslandHost(record, invokeAction, createTemplate, activate);
        host.Lost += OnLost;
        try
        {
            if (!target.OwnedFixture) NativeWindows.ValidateExplorerTarget(target.Parent, target.Geometry.DisplayId);
            host.Start(target.Parent, bounds, component, config, FailAfter);
            if (appearance is not null) host.ApplyAppearance(appearance);
            return CoreResult<bool>.Success(true);
        }
        catch (Exception error) { return CoreResult<bool>.Failure(new("island_embed_failed", "内容岛嵌入失败：" + error.Message)); }
    }
    private void OnLost() => Lost?.Invoke();
    public void RefreshPlacement()
    {
        if (!IsAlive) return;
        if (groupMode)
        {
            if (GroupSnapshot is not null && placementCurrent) RetargetGroup();
            else { frameTimer.Stop(); host!.SetPopup(false); }
            host!.CloseTransientIfParentHidden();
            return;
        }
        if (placementCurrent) host!.Move(bounds);
        else host!.SetPopup(false);
        host.CloseTransientIfParentHidden();
        if (!config.Controls && component is not null) host.UpdateConfirmed(component);
    }
    private bool ReduceMotion()
    {
        if (ReducedMotionOverride is bool forced) return forced;
        try { reducedMotion = !uiSettings.AnimationsEnabled; }
        catch (System.Runtime.InteropServices.COMException) { }
        return reducedMotion;
    }
    private void RetargetGroup()
    {
        var synchronized = timerReadings.Synchronize(GroupSnapshot!.Items, DateTimeOffset.UtcNow, clock.Elapsed);
        if (!synchronized.Accepted) throw new InvalidOperationException(synchronized.Message);
        animation.Retarget(GroupSnapshot.Layout, clock.Elapsed, ReduceMotion(), animation.Generation);
        AdvanceGroup();
        if (lastFrame?.IsComplete == false || timersAdvancing) frameTimer.Start();
    }
    private void AdvanceGroup()
    {
        if (!IsAlive || !groupMode || GroupSnapshot is null || target is null || !placementCurrent)
        { frameTimer.Stop(); return; }
        try
        {
            if (ReduceMotion()) animation.Retarget(GroupSnapshot.Layout, clock.Elapsed, true, animation.Generation);
            var frame = animation.Sample(clock.Elapsed);
            var placement = TaskbarGroupPlacement.Calculate(target.Geometry, Math.Max(96d / target.Geometry.Dpi, frame.WidthDip), frame.HeightDip, rightGap);
            if (!placement.IsSuccess) throw new InvalidOperationException(placement.Error!.Message);
            host!.Move(placement.Value);
            host.UpdateGroup(GroupSnapshot, frame);
            var readings = timerReadings.Sample(clock.Elapsed);
            host.UpdateTimerReadings(readings);
            appliedTimerReadings = readings;
            timersAdvancing = readings.Any(value => value.IsAdvancing);
            lastFrame = frame;
            frameTimer.Interval = frame.IsComplete ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(16);
            if (frame.IsComplete && !timersAdvancing) frameTimer.Stop();
        }
        catch (Exception error)
        {
            frameTimer.Stop();
            animationFailed = true;
            record("group-animation-failed", error.Message);
            // The owning display session performs bounded cleanup/recovery on the next refresh.
            Lost?.Invoke();
        }
    }
    public void Update(long value) => host?.Update(value);
    public void ApplyAppearance(HostAppearancePreferences value) { appearance = value; host?.ApplyAppearance(value); }
    public void Observe() => host?.Observe();
    public void SetPopup(bool open) => host?.SetPopup(open);
    public CoreResult<bool> Close()
    {
        frameTimer.Stop();
        animation.Clear();
        timerReadings.Clear();
        timersAdvancing = false;
        appliedTimerReadings = [];
        lastFrame = null;
        if (host is null) return CoreResult<bool>.Success(true);
        try { host.Close(); host.Lost -= OnLost; host = null; return CoreResult<bool>.Success(true); }
        catch (Exception error) { return CoreResult<bool>.Failure(new("island_cleanup_pending", "内容岛清理未完成，保留所有权供重试：" + error.Message)); }
    }

    public static CoreResult<IslandTarget> CaptureExplorer(Win32TaskbarDockEnvironment environment, TaskbarDockPreferences preferences)
    {
        var result = environment.CaptureIsland(preferences.TargetDisplayId);
        if (!result.IsSuccess) return CoreResult<IslandTarget>.Failure(result.Error!);
        var state = result.Value!;
        return CoreResult<IslandTarget>.Success(new(environment.ObservedTaskbar, state.TaskbarIdentity!, state.Geometry!, Warning: state.Error?.Message));
    }
}

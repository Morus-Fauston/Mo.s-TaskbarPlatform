using Mtp.Platform.Core;

namespace Mtp.Host.Islands;

internal sealed record IslandTarget(nint Parent, string Identity, TaskbarDockGeometry Geometry, bool OwnedFixture = false, string? Warning = null);

internal sealed class IslandDisplayAdapter : IIslandSessionAdapter
{
    private readonly Func<TaskbarDockPreferences, CoreResult<IslandTarget>> capture;
    private readonly Action<string, object?> record;
    private readonly Func<Mtp.Contracts.ActionSlotReference, Task>? invokeAction;
    private readonly Func<HostComponentDisplayModel, Templates.TemplateRenderer?>? createTemplate;
    private ContentIslandHost? host;
    private IslandTarget? target;
    private PixelRect bounds;
    private bool placementCurrent;
    private HostComponentDisplayModel? component;
    private HostTestConfiguration config = new();
    public IslandDisplayAdapter(Func<TaskbarDockPreferences, CoreResult<IslandTarget>> capture, Action<string, object?> record,
        Func<Mtp.Contracts.ActionSlotReference, Task>? invokeAction = null,
        Func<HostComponentDisplayModel, Templates.TemplateRenderer?>? createTemplate = null)
    { this.capture = capture; this.record = record; this.invokeAction = invokeAction; this.createTemplate = createTemplate; }
    public bool IsAlive => host?.IsAlive == true;
    public string MaterialStatus => host?.MaterialStatus ?? "未创建内容岛";
    public bool PopupOpen => host?.PopupOpen == true;
    public nint Handle => host?.Handle ?? 0;
    public string TargetDescription { get; private set; } = "尚未采集";
    public string TargetSummary { get; private set; } = "尚未采集目标";
    public event Action? Lost;
    internal string? FailAfter { get; set; }

    public CoreResult<string> Prepare(TaskbarDockPreferences preferences, HostComponentDisplayModel? value, HostTestConfiguration configuration, bool unavailable)
    {
        component = value;
        config = configuration;
        placementCurrent = false;
        if (unavailable) { target = null; return CoreResult<string>.Failure(new("island_test_unavailable", "本会话模拟任务栏不可用；可清除模拟。")); }
        var result = capture(preferences);
        if (!result.IsSuccess) { target = null; return CoreResult<string>.Failure(result.Error!); }
        var next = result.Value!;
        TargetSummary = $"{next.Geometry.DisplayId} · DPI {next.Geometry.Dpi}";
        TargetDescription = $"{next.Geometry.DisplayId} · DPI {next.Geometry.Dpi} · 右侧间距 {preferences.RightGapDip} DIP · 通知区锚点 {next.Geometry.NotificationBounds?.X.ToString() ?? "不可用"} px · {next.Warning}";
        var placement = TaskbarDockPlacement.Calculate(next.Geometry, new DipSize(configuration.Controls ? 320 : value?.HasTemplate == true ? 480 : 240, 32), preferences.RightGapDip);
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
    private static string Key(IslandTarget next, TaskbarDockPreferences preferences) => $"{next.Identity}|{next.Geometry.DisplayId}|{next.Geometry.Dpi}|{preferences.RightGapDip}";
    public CoreResult<bool> Start()
    {
        if (target is null || component is null) return CoreResult<bool>.Failure(new("island_target_missing", "声明或任务栏定位条件不可用。"));
        if (host is not null) return CoreResult<bool>.Failure(new("island_cleanup_pending", "旧内容岛尚未释放。"));
        host = new ContentIslandHost(record, invokeAction, createTemplate);
        host.Lost += OnLost;
        try
        {
            if (!target.OwnedFixture) NativeWindows.ValidateExplorerTarget(target.Parent, target.Geometry.DisplayId);
            host.Start(target.Parent, bounds, component, config, FailAfter);
            return CoreResult<bool>.Success(true);
        }
        catch (Exception error) { return CoreResult<bool>.Failure(new("island_embed_failed", "内容岛嵌入失败：" + error.Message)); }
    }
    private void OnLost() => Lost?.Invoke();
    public void RefreshPlacement()
    {
        if (!IsAlive) return;
        if (placementCurrent) host!.Move(bounds);
        else host!.SetPopup(false);
        host.CloseTransientIfParentHidden();
        if (!config.Controls && component is not null) host.UpdateConfirmed(component);
    }
    public void Update(long value) => host?.Update(value);
    public void Observe() => host?.Observe();
    public void SetPopup(bool open) => host?.SetPopup(open);
    public CoreResult<bool> Close()
    {
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

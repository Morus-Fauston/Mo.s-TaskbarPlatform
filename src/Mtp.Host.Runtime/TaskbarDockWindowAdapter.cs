using Mtp.Platform.Core;

namespace Mtp.Host;

internal sealed record TaskbarDockEnvironmentSnapshot(
    string DisplayId, PixelRect DisplayBounds, PixelRect WorkArea, uint Dpi,
    string? TaskbarIdentity, TaskbarDockGeometry? Geometry, StructuredError? Error, TaskbarVisibility Visibility);

internal interface ITaskbarDockEnvironment
{
    CoreResult<TaskbarDockEnvironmentSnapshot> Capture(string? displayId);
}

internal interface ITaskbarDockWindow
{
    event EventHandler? Closed;
    event EventHandler? VisualStateChanged;
    long Identity { get; }
    bool IsAlive { get; }
    StructuredError? VisualError { get; }
    void Show(HostComponentDisplayModel component, PixelRect bounds);
    void Hide();
    void Close();
}

internal sealed record TaskbarDockSession(long Generation, long WindowIdentity, string TaskbarIdentity, string DisplayId, TaskbarDockGeometry Geometry);

/// <summary>Owns the 05A top-level window, preferences and taskbar binding. All calls use the UI dispatcher.</summary>
internal sealed class TaskbarDockWindowAdapter : IIndependentDockWindowAdapter
{
    private static readonly DipSize contentSize = new(240, 32);
    private readonly ITaskbarDockPreferenceStore store;
    private readonly ITaskbarDockEnvironment environment;
    private readonly Func<ITaskbarDockWindow> createWindow;
    private ITaskbarDockWindow? window;
    private HostComponentDisplayModel? component;
    private long generation;
    private bool closePending;
    private StructuredError? placementError;
    private StructuredError? visualError;

    public TaskbarDockWindowAdapter(ITaskbarDockPreferenceStore store, ITaskbarDockEnvironment environment, Func<ITaskbarDockWindow> createWindow)
    {
        this.store = store;
        this.environment = environment;
        this.createWindow = createWindow;
        var loaded = store.Load();
        Preferences = loaded.Value ?? new();
        PreferenceError = loaded.Error;
    }

    public event EventHandler? Closed;
    public event EventHandler? StateChanged;
    public bool IsOpen => window is not null;
    public bool NeedsWindowRecovery { get; private set; }
    public TaskbarDockPreferences Preferences { get; private set; }
    public StructuredError? PreferenceError { get; private set; }
    public StructuredError? PresentationError => placementError;
    public StructuredError? VisualError => visualError;
    public TaskbarDockSession? Session { get; private set; }
    public TaskbarVisibility Visibility { get; private set; } = TaskbarVisibility.Allowed;
    public string Status => closePending ? "窗口关闭未确认，请重试关闭。"
        : !IsOpen ? "组件已关闭。"
        : Visibility == TaskbarVisibility.FullScreen ? "目标屏幕正在全屏使用，组件暂时隐藏。"
        : Visibility == TaskbarVisibility.TaskbarHidden ? "任务栏已收起，组件暂时隐藏。"
        : Visibility == TaskbarVisibility.Unknown ? "环境状态暂时无法确认，组件保持隐藏。"
        : placementError is not null ? $"任务栏嵌入当前不可用，已切换独立贴靠。{placementError.Message}"
        : "任务栏右贴靠已定位；透明效果待当前 Windows 环境确认。";

    public CoreResult<TaskbarDockPreferences> SetDisplay(string? displayId) => ApplyPreference(store.CommitDisplay(displayId));
    public CoreResult<TaskbarDockPreferences> SetGap(int gapDip) => ApplyPreference(store.CommitGap(gapDip));

    private CoreResult<TaskbarDockPreferences> ApplyPreference(CoreResult<TaskbarDockPreferences> result)
    {
        PreferenceError = result.Error;
        if (result.IsSuccess)
        {
            Preferences = result.Value!;
            Refresh();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public CoreResult<HostComponentDisplayModel> Show(HostComponentDisplayModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (closePending)
            return CoreResult<HostComponentDisplayModel>.Failure(new("dock_close_pending", "旧窗口尚未确认关闭，请先重试关闭。"));
        component = value;
        try
        {
            if (window is { IsAlive: false }) ReleaseWindow(window);
            var captured = environment.Capture(Preferences.TargetDisplayId);
            if (!captured.IsSuccess) return Suspend(value, TaskbarVisibility.Unknown, captured.Error);
            var snapshot = captured.Value!;
            if (snapshot.Visibility != TaskbarVisibility.Allowed)
                return Suspend(value, snapshot.Visibility, snapshot.Error);
            Visibility = TaskbarVisibility.Allowed;
            var placement = TaskbarDockPlacement.Calculate(snapshot.Geometry, contentSize, Preferences.RightGapDip);
            placementError = snapshot.Error ?? placement.Error;
            if (placementError is not null)
            {
                Session = null;
                placement = CalculateFallback(snapshot);
            }
            if (!placement.IsSuccess) return FailShow(placement.Error!);

            var current = EnsureWindow();
            try { current.Show(value, placement.Value); }
            catch (Exception) when (placementError is null && current.IsAlive)
            {
                Session = null;
                placementError = new("dock_position_failed", "任务栏区域定位失败。");
                var fallback = CalculateFallback(snapshot);
                if (!fallback.IsSuccess) return FailShow(fallback.Error!);
                current.Show(value, fallback.Value);
            }
            if (!ReferenceEquals(window, current) || !current.IsAlive) return FailShow(new("dock_window_lost", "组件窗口已失效。"));
            visualError = current.VisualError;
            NeedsWindowRecovery = false;
            if (placementError is null && snapshot.TaskbarIdentity is not null &&
                (Session is null || Session.WindowIdentity != window.Identity ||
                 Session.TaskbarIdentity != snapshot.TaskbarIdentity || Session.DisplayId != snapshot.DisplayId || Session.Geometry != snapshot.Geometry))
            {
                Session = new(++generation, window.Identity, snapshot.TaskbarIdentity, snapshot.DisplayId, snapshot.Geometry!);
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return CoreResult<HostComponentDisplayModel>.Success(value);
        }
        catch (Exception exception)
        {
            return FailShow(new("dock_window_show_failed", "组件窗口显示失败。", exception.GetType().Name));
        }
    }

    public void Refresh()
    {
        if (component is not null && !closePending) Show(component);
    }

    private ITaskbarDockWindow EnsureWindow()
    {
        if (window is null)
        {
            window = createWindow();
            window.Closed += WindowClosed;
            window.VisualStateChanged += WindowVisualStateChanged;
        }
        return window;
    }

    private CoreResult<HostComponentDisplayModel> Suspend(HostComponentDisplayModel value, TaskbarVisibility visibility, StructuredError? error)
    {
        var current = EnsureWindow();
        current.Hide();
        if (!ReferenceEquals(window, current) || !current.IsAlive)
            return FailShow(new("dock_window_lost", "组件窗口已失效。"));
        Visibility = visibility;
        Session = null;
        // A normal visibility transition must not erase an earlier placement or material failure.
        placementError = error ?? placementError;
        visualError = current.VisualError ?? visualError;
        NeedsWindowRecovery = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return CoreResult<HostComponentDisplayModel>.Success(value);
    }

    public CoreResult<bool> Close()
    {
        NeedsWindowRecovery = false;
        component = null;
        Session = null;
        var current = window;
        if (current is null) return CoreResult<bool>.Success(true);
        closePending = true;
        try
        {
            if (current.IsAlive) current.Close();
            if (ReferenceEquals(window, current) && !current.IsAlive) ReleaseWindow(current);
            if (window is null) return CoreResult<bool>.Success(true);
        }
        catch (Exception) { }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return CoreResult<bool>.Failure(new("dock_window_close_unconfirmed", "组件窗口未确认关闭，保留所有权供重试。"));
    }

    private CoreResult<HostComponentDisplayModel> FailShow(StructuredError error)
    {
        Close();
        placementError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return CoreResult<HostComponentDisplayModel>.Failure(error);
    }

    private void WindowClosed(object? sender, EventArgs args)
    {
        if (sender is ITaskbarDockWindow closed && ReferenceEquals(window, closed))
        {
            NeedsWindowRecovery = !closePending;
            component = null;
            ReleaseWindow(closed);
        }
    }

    private void ReleaseWindow(ITaskbarDockWindow current)
    {
        current.Closed -= WindowClosed;
        current.VisualStateChanged -= WindowVisualStateChanged;
        window = null;
        Session = null;
        closePending = false;
        Closed?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void WindowVisualStateChanged(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(window, sender)) return;
        visualError = window?.VisualError;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static CoreResult<PixelRect> CalculateFallback(TaskbarDockEnvironmentSnapshot snapshot)
    {
        var work = snapshot.WorkArea;
        var width = Math.Round(contentSize.Width * (snapshot.Dpi / 96d));
        var height = Math.Round(contentSize.Height * (snapshot.Dpi / 96d));
        var margin = Math.Round(8 * (snapshot.Dpi / 96d));
        if (!snapshot.DisplayBounds.Contains(work) || width < 1 || height < 1 ||
            width + 2 * margin > work.Width || height + 2 * margin > work.Height)
            return CoreResult<PixelRect>.Failure(new("dock_fallback_unavailable", "当前屏幕工作区无法容纳独立贴靠窗口。"));
        return CoreResult<PixelRect>.Success(new((int)(work.Right - margin - width), (int)(work.Bottom - margin - height), (int)width, (int)height));
    }
}

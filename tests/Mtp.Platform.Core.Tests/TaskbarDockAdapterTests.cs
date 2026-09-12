using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class TaskbarDockAdapterTests
{
    [Theory]
    [InlineData(TaskbarVisibility.FullScreen)]
    [InlineData(TaskbarVisibility.TaskbarHidden)]
    [InlineData(TaskbarVisibility.Unknown)]
    public void SuppressionPreventsFallbackAndRestoresSameWindowWithFreshSession(TaskbarVisibility visibility)
    {
        var environment = new EnvironmentStub();
        var original = environment.Snapshot;
        var window = new WindowStub(1);
        var adapter = new TaskbarDockWindowAdapter(new StoreStub(), environment, () => window);
        adapter.Show(Component());
        var session = adapter.Session;
        environment.Snapshot = original with { Visibility = visibility, Geometry = null, Error = new("anchor_failed", "missing") };
        adapter.Refresh();
        adapter.Refresh();
        Assert.False(window.IsVisible);
        Assert.True(window.IsAlive);
        Assert.True(adapter.IsOpen);
        Assert.Equal(1, window.ShowCount);
        Assert.Null(adapter.Session);
        environment.Snapshot = original;
        adapter.Refresh();
        Assert.True(window.IsVisible);
        Assert.NotEqual(session, adapter.Session);
        Assert.Equal(8, adapter.Preferences.RightGapDip);
    }

    [Fact]
    public void RepeatedLayoutKeepsSessionButExplorerAndWindowChangesStartNewSessions()
    {
        var environment = new EnvironmentStub();
        var windows = new List<WindowStub>();
        var adapter = new TaskbarDockWindowAdapter(new StoreStub(), environment, () =>
        {
            var window = new WindowStub(windows.Count + 1);
            windows.Add(window);
            return window;
        });
        Assert.True(adapter.Show(Component()).IsSuccess);
        Assert.Equal(new PixelRect(1452, 1040, 240, 32), windows[0].Bounds);
        var first = adapter.Session;
        adapter.Refresh();
        Assert.Equal(first, adapter.Session);
        environment.Snapshot = environment.Snapshot with { TaskbarIdentity = "explorer-2" };
        adapter.Refresh();
        Assert.NotEqual(first, adapter.Session);
        var second = adapter.Session;
        Assert.True(adapter.Close().IsSuccess);
        Assert.True(adapter.Show(Component()).IsSuccess);
        Assert.NotEqual(second, adapter.Session);
        Assert.Equal(2, windows.Count);
    }

    private static HostComponentDisplayModel Component() => HostComponentDisplayModel.From(
        new Component(new StableIdentity(new StableId("test")), CapabilityState.Available), true);

    [Fact]
    public void StartupSuppressionNeverShowsAndClosingPreventsLaterEnvironmentRecovery()
    {
        var environment = new EnvironmentStub();
        environment.Snapshot = environment.Snapshot with { Visibility = TaskbarVisibility.FullScreen };
        var window = new WindowStub(1);
        var store = new StoreStub();
        var adapter = new TaskbarDockWindowAdapter(store, environment, () => window);
        Assert.True(adapter.Show(Component()).IsSuccess);
        Assert.True(adapter.IsOpen);
        Assert.False(window.IsVisible);
        Assert.Equal(0, window.ShowCount);
        Assert.True(adapter.Close().IsSuccess);
        environment.Snapshot = environment.Snapshot with { Visibility = TaskbarVisibility.Allowed };
        adapter.Refresh();
        Assert.False(adapter.IsOpen);
        Assert.Equal(0, window.ShowCount);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public void FailedEnvironmentReadHidesExistingWindowAndKeepsErrorsUntilRecovery()
    {
        var environment = new EnvironmentStub();
        var window = new WindowStub(1) { VisualError = new("paint_failed", "paint") };
        var adapter = new TaskbarDockWindowAdapter(new StoreStub(), environment, () => window);
        adapter.Show(Component());
        environment.Failure = new("capture_failed", "read failed");
        adapter.Refresh();
        Assert.False(window.IsVisible);
        Assert.True(window.IsAlive);
        Assert.Equal(TaskbarVisibility.Unknown, adapter.Visibility);
        Assert.Equal("capture_failed", adapter.PresentationError?.Code);
        Assert.Equal("paint_failed", adapter.VisualError?.Code);
        environment.Failure = null;
        environment.Snapshot = environment.Snapshot with { Visibility = TaskbarVisibility.TaskbarHidden };
        adapter.Refresh();
        Assert.Equal("capture_failed", adapter.PresentationError?.Code);
        environment.Snapshot = environment.Snapshot with { Visibility = TaskbarVisibility.Allowed };
        adapter.Refresh();
        Assert.True(window.IsVisible);
        Assert.Null(adapter.PresentationError);
        Assert.Equal("paint_failed", adapter.VisualError?.Code);
    }

    [Fact]
    public void UnifiedDisplayActionsKeepPersistedIntentWhileEnvironmentSuppressesTheWindow()
    {
        var preferences = new DisplayPreferences();
        var display = new HostDisplayController(new Declaration(), preferences);
        var identity = display.Load().Components.Single().Identity;
        var environment = new EnvironmentStub();
        environment.Snapshot = environment.Snapshot with { Visibility = TaskbarVisibility.FullScreen };
        var windows = new List<WindowStub>();
        var adapter = new TaskbarDockWindowAdapter(new StoreStub(), environment, () =>
        {
            var window = new WindowStub(windows.Count + 1);
            windows.Add(window);
            return window;
        });
        var dock = new IndependentDockWindowController(display, adapter);
        var probe = new ExplorerTaskbarProbeController(display, new Win32ExplorerTaskbarEmbedAdapter(() => 1), dock);
        using var actions = new HostDisplayActionController(display, dock, probe);
        Assert.True(actions.SetVisibility(identity, true).IsSuccess);
        Assert.True(dock.State.IsOpen);
        Assert.True(dock.State.Component!.IsVisible);
        Assert.False(windows[0].IsVisible);
        Assert.True(actions.RestoreCurrent().IsSuccess);
        Assert.Equal(1, preferences.Writes);
        Assert.True(actions.SetVisibility(identity, false).IsSuccess);
        environment.Snapshot = environment.Snapshot with { Visibility = TaskbarVisibility.Allowed };
        actions.RestoreCurrent();
        Assert.False(adapter.IsOpen);
        Assert.Equal(2, preferences.Writes);
        environment.Snapshot = environment.Snapshot with { Visibility = TaskbarVisibility.TaskbarHidden };
        Assert.True(actions.SetVisibility(identity, true).IsSuccess);
        Assert.False(windows[1].IsVisible);
        environment.Snapshot = environment.Snapshot with { Visibility = TaskbarVisibility.Allowed };
        Assert.True(actions.RestoreCurrent().IsSuccess);
        Assert.True(windows[1].IsVisible);
        Assert.Equal(3, preferences.Writes);
        Assert.True(actions.Shutdown().IsSuccess);
    }

    [Fact]
    public void UnexpectedWindowClosureRequestsHostRecoveryButExplicitCloseDoesNot()
    {
        var window = new WindowStub(1);
        var adapter = new TaskbarDockWindowAdapter(new StoreStub(), new EnvironmentStub(), () => window);
        adapter.Show(Component());
        window.Close();
        Assert.True(adapter.NeedsWindowRecovery);
        Assert.False(adapter.IsOpen);
        adapter.Close();
        Assert.False(adapter.NeedsWindowRecovery);
    }

    [Fact]
    public void PositionFailureFallsBackAndRetainsPreferenceAndComponent()
    {
        var window = new WindowStub(1) { FailNextShow = true };
        var adapter = new TaskbarDockWindowAdapter(new StoreStub(), new EnvironmentStub(), () => window);
        var result = adapter.Show(Component());
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(adapter.Session);
        Assert.Equal(new PixelRect(1672, 992, 240, 32), window.Bounds);
        Assert.NotNull(adapter.PresentationError);
        Assert.Equal(new TaskbarDockPreferences(), adapter.Preferences);
        adapter.Refresh();
        Assert.NotNull(adapter.Session);
        Assert.Null(adapter.PresentationError);
    }

    [Fact]
    public void MissingAnchorUsesWorkAreaAndRecoveryDoesNotReuseOldSession()
    {
        var environment = new EnvironmentStub();
        var window = new WindowStub(1);
        var adapter = new TaskbarDockWindowAdapter(new StoreStub(), environment, () => window);
        adapter.Show(Component());
        var session = adapter.Session;
        var original = environment.Snapshot;
        environment.Snapshot = original with { Geometry = original.Geometry! with { NotificationBounds = null } };
        adapter.Refresh();
        Assert.Null(adapter.Session);
        Assert.Equal(new PixelRect(1672, 992, 240, 32), window.Bounds);
        Assert.Contains("独立贴靠", adapter.Status);
        environment.Snapshot = original;
        adapter.Refresh();
        Assert.NotNull(adapter.Session);
        Assert.NotEqual(session, adapter.Session);
        Assert.Null(adapter.PresentationError);
    }

    [Fact]
    public void FailedPreferenceWriteDoesNotMoveWindowOrReplaceCurrentValues()
    {
        var store = new StoreStub();
        var window = new WindowStub(1);
        var adapter = new TaskbarDockWindowAdapter(store, new EnvironmentStub(), () => window);
        adapter.Show(Component());
        var bounds = window.Bounds;
        var session = adapter.Session;
        store.FailWrites = true;
        Assert.False(adapter.SetGap(32).IsSuccess);
        Assert.Equal(8, adapter.Preferences.RightGapDip);
        Assert.Equal(bounds, window.Bounds);
        Assert.Equal(session, adapter.Session);
        Assert.NotNull(adapter.PreferenceError);
    }

    [Fact]
    public void DeadNativeWindowIsRecreatedAndCloseFailureRetainsOwnershipWithoutReopening()
    {
        var windows = new List<WindowStub>();
        var adapter = new TaskbarDockWindowAdapter(new StoreStub(), new EnvironmentStub(), () =>
        {
            var window = new WindowStub(windows.Count + 1);
            windows.Add(window);
            return window;
        });
        adapter.Show(Component());
        var original = adapter.Session;
        windows[0].IsAlive = false;
        adapter.Refresh();
        Assert.Equal(2, windows.Count);
        Assert.NotEqual(original, adapter.Session);
        windows[1].FailClose = true;
        Assert.False(adapter.Close().IsSuccess);
        adapter.Refresh();
        Assert.True(adapter.IsOpen);
        Assert.Equal(2, windows.Count);
        Assert.False(adapter.Show(Component()).IsSuccess);
        windows[1].FailClose = false;
        Assert.True(adapter.Close().IsSuccess);
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void VisualFailureIsReportedWithoutLosingSessionOrPreferencesAndSurvivesRefresh()
    {
        var window = new WindowStub(1);
        var adapter = new TaskbarDockWindowAdapter(new StoreStub(), new EnvironmentStub(), () => window);
        adapter.Show(Component());
        var session = adapter.Session;
        window.VisualError = new("dock_surface_paint_failed", "绘制失败");
        window.ReportVisualFailure();
        adapter.Refresh();
        Assert.Equal("dock_surface_paint_failed", adapter.VisualError?.Code);
        Assert.Equal(session, adapter.Session);
        Assert.True(adapter.IsOpen);
        Assert.Equal(8, adapter.Preferences.RightGapDip);
        Assert.True(adapter.Close().IsSuccess);
        Assert.Equal("dock_surface_paint_failed", adapter.VisualError?.Code);
    }

    [Fact]
    public void MissingPreferredDisplayKeepsStoredIdAndUsesPrimaryFallback()
    {
        var store = new StoreStub { Value = new("offline-display", 16) };
        var environment = new EnvironmentStub();
        environment.Snapshot = environment.Snapshot with { Error = new("dock_display_missing", "原屏幕不在") };
        var window = new WindowStub(1);
        var adapter = new TaskbarDockWindowAdapter(store, environment, () => window);
        Assert.True(adapter.Show(Component()).IsSuccess);
        Assert.Null(adapter.Session);
        Assert.Equal("offline-display", adapter.Preferences.TargetDisplayId);
        Assert.Equal(new PixelRect(1672, 992, 240, 32), window.Bounds);
    }

    private sealed class EnvironmentStub : ITaskbarDockEnvironment
    {
        public StructuredError? Failure { get; set; }
        public TaskbarDockEnvironmentSnapshot Snapshot { get; set; } = new(
            "display", new(0, 0, 1920, 1080), new(0, 0, 1920, 1032), 96,
            "explorer-1", new("display", new(0, 0, 1920, 1080), new(0, 1032, 1920, 48), new(1700, 1032, 220, 48), 96), null, TaskbarVisibility.Allowed);
        public CoreResult<TaskbarDockEnvironmentSnapshot> Capture(string? displayId) => Failure is null
            ? CoreResult<TaskbarDockEnvironmentSnapshot>.Success(Snapshot)
            : CoreResult<TaskbarDockEnvironmentSnapshot>.Failure(Failure);
    }

    private sealed class WindowStub(long identity) : ITaskbarDockWindow
    {
        public event EventHandler? Closed;
        public event EventHandler? VisualStateChanged;
        public long Identity => identity;
        public bool IsAlive { get; set; } = true;
        public StructuredError? VisualError { get; set; }
        public PixelRect Bounds { get; private set; }
        public bool FailNextShow { get; set; }
        public bool FailClose { get; set; }
        public bool IsVisible { get; private set; }
        public int ShowCount { get; private set; }
        public void Hide() => IsVisible = false;
        public void Show(HostComponentDisplayModel component, PixelRect bounds)
        {
            if (FailNextShow) { FailNextShow = false; throw new InvalidOperationException("Position failed"); }
            Bounds = bounds;
            IsVisible = true;
            ShowCount++;
        }
        public void Close()
        {
            if (FailClose) throw new InvalidOperationException("Close failed");
            IsAlive = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
        public void ReportVisualFailure() => VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class StoreStub : ITaskbarDockPreferenceStore
    {
        public int WriteCount { get; private set; }
        public TaskbarDockPreferences Value { get; set; } = new();
        public bool FailWrites { get; set; }
        public CoreResult<TaskbarDockPreferences> Load() => CoreResult<TaskbarDockPreferences>.Success(Value);
        public CoreResult<TaskbarDockPreferences> CommitDisplay(string? displayId)
        {
            WriteCount++;
            return CoreResult<TaskbarDockPreferences>.Success(Value = Value with { TargetDisplayId = displayId });
        }
        public CoreResult<TaskbarDockPreferences> CommitGap(int gapDip)
        {
            WriteCount++;
            return FailWrites
                ? CoreResult<TaskbarDockPreferences>.Failure(new("write_failed", "写入失败"))
                : CoreResult<TaskbarDockPreferences>.Success(Value = Value with { RightGapDip = gapDip });
        }
    }

    private sealed class DisplayPreferences : IComponentDisplayPreferenceStore
    {
        private ComponentDisplayPreferences value = new();
        public int Writes { get; private set; }
        public ComponentDisplayPreferenceLoadResult Load() => new(value, null, ComponentDisplayPreferenceLoadState.Loaded);
        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool isVisible)
        {
            Writes++;
            return CoreResult<ComponentDisplayPreferences>.Success(value = value.WithVisibility(identity, isVisible));
        }
    }

    private sealed class Declaration : IDeclarationSource
    {
        public CoreResult<string> Read() => CoreResult<string>.Success("""
            {"applicationId":"test","featureGroups":[{"featureGroupId":"controls",
              "components":[{"componentId":"widget","actionSlots":[{"actionSlotId":"go"}]}],
              "taskbarFlyouts":[{"taskbarFlyoutId":"panel","actionSlots":[{"actionSlotId":"go"}]}]}]}
            """);
    }
}

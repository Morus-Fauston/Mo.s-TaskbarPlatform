using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class HostDisplayActionTests
{
    [Fact]
    public void ShowingAComponentPersistsThePreferenceAndShowsTheDockWindowThroughOneAction()
    {
        using var context = CreateContext();

        var result = context.Actions.SetVisibility(context.Component.Identity, true);

        Assert.True(result.PreferenceChanged);
        Assert.True(result.IsSuccess);
        Assert.True(result.Component!.IsVisible);
        Assert.True(result.DockWindow.IsOpen);
        Assert.Equal(1, context.Preferences.SaveCount);
        Assert.Equal(1, context.DockAdapter.ShowCount);
    }

    [Fact]
    public void HidingAComponentPersistsThenDetachesTheProbeAndClosesTheDockWindow()
    {
        using var context = CreateContext();
        Assert.True(context.Actions.SetVisibility(context.Component.Identity, true).IsSuccess);
        Assert.True(context.Probe.Run(context.Display.CurrentComponents.Single(), new ExplorerTaskbarProbeRequest()).Succeeded);
        Assert.False(context.DockAdapter.IsOpen);
        var showsBeforeHide = context.DockAdapter.ShowCount;

        var result = context.Actions.SetVisibility(context.Component.Identity, false);

        Assert.True(result.IsSuccess);
        Assert.True(result.PreferenceChanged);
        Assert.False(result.Component!.IsVisible);
        Assert.False(result.DockWindow.IsOpen);
        Assert.Equal(2, context.Preferences.SaveCount);
        Assert.Equal(1, context.ProbeAdapter.DetachCount);
        Assert.Equal(showsBeforeHide, context.DockAdapter.ShowCount);
    }

    [Fact]
    public void PreferenceFailureDoesNotTouchPresentationAndReturnsThePreviousComponent()
    {
        using var context = CreateContext();
        context.Preferences.SaveError = new StructuredError("preference_write_failed", "Cannot save preferences.");

        var result = context.Actions.SetVisibility(context.Component.Identity, true);

        Assert.False(result.IsSuccess);
        Assert.False(result.PreferenceChanged);
        Assert.False(result.Component!.IsVisible);
        Assert.False(result.DockWindow.IsOpen);
        Assert.Equal("preference_write_failed", result.Errors.Single().Code);
        Assert.Equal(0, context.DockAdapter.ShowCount);
        Assert.Equal(0, context.DockAdapter.CloseCount);
        Assert.Equal(0, context.ProbeAdapter.DetachCount);
    }

    [Fact]
    public void WindowShowFailureKeepsTheVisiblePreferenceAndReportsTheRealWindowState()
    {
        using var context = CreateContext();
        context.DockAdapter.ShowError = new StructuredError("dock_window_show_failed", "Cannot show the window.");

        var result = context.Actions.SetVisibility(context.Component.Identity, true);

        Assert.False(result.IsSuccess);
        Assert.True(result.PreferenceChanged);
        Assert.True(result.Component!.IsVisible);
        Assert.False(result.DockWindow.IsOpen);
        Assert.Equal("dock_window_show_failed", result.Errors.Single().Code);
        Assert.True(context.Display.CurrentComponents.Single().IsVisible);
    }

    [Fact]
    public void WindowCloseFailureKeepsTheHiddenPreferenceAndReportsTheStillOpenWindow()
    {
        using var context = CreateContext();
        Assert.True(context.Actions.SetVisibility(context.Component.Identity, true).IsSuccess);
        context.DockAdapter.CloseError = new StructuredError("dock_window_close_failed", "Cannot close the window.");

        var result = context.Actions.SetVisibility(context.Component.Identity, false);

        Assert.False(result.IsSuccess);
        Assert.True(result.PreferenceChanged);
        Assert.False(result.Component!.IsVisible);
        Assert.True(result.DockWindow.IsOpen);
        Assert.Equal("dock_window_close_failed", result.Errors.Single().Code);
        Assert.False(context.Display.CurrentComponents.Single().IsVisible);
    }

    [Fact]
    public void RestoreCurrentUsesTheSamePresentationActionAsAUserChange()
    {
        using var context = CreateContext(initiallyVisible: true);

        var result = context.Actions.RestoreCurrent();

        Assert.True(result.IsSuccess);
        Assert.False(result.PreferenceChanged);
        Assert.True(result.Component!.IsVisible);
        Assert.True(result.DockWindow.IsOpen);
        Assert.Equal(0, context.Preferences.SaveCount);
        Assert.Equal(1, context.DockAdapter.ShowCount);
    }

    [Fact]
    public void RestoreCurrentShowsAVisibleComponentEvenWhenTheFirstComponentIsHidden()
    {
        using var context = CreateContext(ValidJsonWithTwoComponents());
        var second = context.Display.CurrentComponents[1];
        Assert.True(context.Display.SetVisibility(second.Identity, true).IsSuccess);
        context.Preferences.ResetSaveCount();

        var result = context.Actions.RestoreCurrent();

        Assert.True(result.IsSuccess);
        Assert.Equal(second.Identity, result.Component!.Identity);
        Assert.True(result.DockWindow.IsOpen);
        Assert.Equal(second.Identity, context.DockAdapter.LastShownComponent!.Identity);
    }

    [Fact]
    public void ShutdownAttemptsAllCleanupAndReturnsEveryError()
    {
        using var context = CreateContext();
        Assert.True(context.Actions.SetVisibility(context.Component.Identity, true).IsSuccess);
        context.ProbeAdapter.DetachError = new StructuredError("explorer_probe_detach_failed", "Cannot detach the probe.");
        context.DockAdapter.CloseError = new StructuredError("dock_window_close_failed", "Cannot close the window.");

        var result = context.Actions.Shutdown();

        Assert.False(result.IsSuccess);
        Assert.False(result.PreferenceChanged);
        Assert.True(result.DockWindow.IsOpen);
        Assert.Equal(
            new[] { "explorer_probe_detach_failed", "dock_window_close_failed" },
            result.Errors.Select(error => error.Code));
        Assert.Equal(1, context.ProbeAdapter.DetachCount);
        Assert.Equal(1, context.DockAdapter.CloseCount);
    }

    [Fact]
    public void ShutdownDetachesAnEmbeddedProbeWithoutReopeningTheDockWindow()
    {
        using var context = CreateContext();
        Assert.True(context.Actions.SetVisibility(context.Component.Identity, true).IsSuccess);
        Assert.True(context.Probe.Run(context.Display.CurrentComponents.Single(), new ExplorerTaskbarProbeRequest()).Succeeded);
        Assert.False(context.DockAdapter.IsOpen);
        var showsBeforeShutdown = context.DockAdapter.ShowCount;

        var result = context.Actions.Shutdown();

        Assert.True(result.IsSuccess);
        Assert.False(result.DockWindow.IsOpen);
        Assert.False(context.ProbeAdapter.IsEmbedded);
        Assert.Equal(showsBeforeShutdown, context.DockAdapter.ShowCount);
    }

    [Fact]
    public void ProbeRunAndDetachUseTheUnifiedDisplayActionEntryPoint()
    {
        using var context = CreateContext();
        Assert.True(context.Actions.SetVisibility(context.Component.Identity, true).IsSuccess);

        var run = context.Actions.RunProbe(
            context.Display.CurrentComponents.Single(),
            new ExplorerTaskbarProbeRequest());
        var detached = context.Actions.DetachProbe();

        Assert.True(run.Succeeded);
        Assert.True(detached.IsSuccess);
        Assert.False(context.Actions.ProbeState.IsEmbedded);
        Assert.True(context.DockAdapter.IsOpen);
    }

    [Fact]
    public void FailedShutdownCanBeRetriedBeforeTheActionModuleIsDisposed()
    {
        using var context = CreateContext();
        Assert.True(context.Actions.SetVisibility(context.Component.Identity, true).IsSuccess);
        Assert.True(context.Actions.RunProbe(
            context.Display.CurrentComponents.Single(),
            new ExplorerTaskbarProbeRequest()).Succeeded);
        context.ProbeAdapter.DetachError = new StructuredError("explorer_probe_detach_failed", "Cannot detach the probe.");
        context.DockAdapter.CloseError = new StructuredError("dock_window_close_failed", "Cannot close the window.");

        var failed = context.Actions.Shutdown();
        context.ProbeAdapter.DetachError = null;
        context.DockAdapter.CloseError = null;
        var retried = context.Actions.Shutdown();

        Assert.False(failed.IsSuccess);
        Assert.True(retried.IsSuccess);
        Assert.False(context.Actions.ProbeState.IsEmbedded);
        Assert.False(retried.DockWindow.IsOpen);
        Assert.Equal(2, context.ProbeAdapter.DetachCount);
        Assert.Equal(3, context.DockAdapter.CloseCount);
    }

    private static TestContext CreateContext(bool initiallyVisible = false) =>
        CreateContext(ValidJson(), initiallyVisible);

    private static TestContext CreateContext(string declaration, bool initiallyVisible = false)
    {
        var preferences = new MemoryPreferenceStore();
        var display = new HostDisplayController(new DeclarationSource(declaration), preferences);
        var component = display.Load().Components.First();
        if (initiallyVisible)
        {
            Assert.True(display.SetVisibility(component.Identity, true).IsSuccess);
            preferences.ResetSaveCount();
            component = display.CurrentComponents.First();
        }

        var dockAdapter = new RecordingDockAdapter();
        var dock = new IndependentDockWindowController(display, dockAdapter);
        var probeAdapter = new RecordingProbeAdapter();
        var probe = new ExplorerTaskbarProbeController(display, probeAdapter, dock);
        var actions = new HostDisplayActionController(display, dock, probe);
        return new TestContext(display, component, preferences, dockAdapter, dock, probeAdapter, probe, actions);
    }

    private static string ValidJson() => """
        {
          "applicationId": "music",
          "featureGroups": [{
            "featureGroupId": "controls",
            "components": [{ "componentId": "widget", "actionSlots": [{ "actionSlotId": "go" }] }],
            "taskbarFlyouts": [{ "taskbarFlyoutId": "panel", "actionSlots": [{ "actionSlotId": "go" }] }]
          }]
        }
        """;

    private static string ValidJsonWithTwoComponents() => """
        {
          "applicationId": "music",
          "featureGroups": [{
            "featureGroupId": "controls",
            "components": [
              { "componentId": "first", "actionSlots": [{ "actionSlotId": "go" }] },
              { "componentId": "second", "actionSlots": [{ "actionSlotId": "go" }] }
            ],
            "taskbarFlyouts": [{ "taskbarFlyoutId": "panel", "actionSlots": [{ "actionSlotId": "go" }] }]
          }]
        }
        """;

    private sealed record TestContext(
        HostDisplayController Display,
        HostComponentDisplayModel Component,
        MemoryPreferenceStore Preferences,
        RecordingDockAdapter DockAdapter,
        IndependentDockWindowController Dock,
        RecordingProbeAdapter ProbeAdapter,
        ExplorerTaskbarProbeController Probe,
        HostDisplayActionController Actions) : IDisposable
    {
        public void Dispose()
        {
            Probe.Dispose();
            Dock.Dispose();
        }
    }

    private sealed class DeclarationSource(string content) : IDeclarationSource
    {
        public CoreResult<string> Read() => CoreResult<string>.Success(content);
    }

    private sealed class MemoryPreferenceStore : IComponentDisplayPreferenceStore
    {
        private ComponentDisplayPreferences preferences = new();

        public int SaveCount { get; private set; }

        public StructuredError? SaveError { get; set; }

        public ComponentDisplayPreferenceLoadResult Load() =>
            new(preferences, null, ComponentDisplayPreferenceLoadState.Loaded);

        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool isVisible)
        {
            SaveCount++;
            if (SaveError is not null)
            {
                return CoreResult<ComponentDisplayPreferences>.Failure(SaveError);
            }

            preferences = preferences.WithVisibility(identity, isVisible);
            return CoreResult<ComponentDisplayPreferences>.Success(preferences);
        }

        public void ResetSaveCount() => SaveCount = 0;
    }

    private sealed class RecordingDockAdapter : IIndependentDockWindowAdapter
    {
        public event EventHandler? Closed;

        public bool IsOpen { get; private set; }

        public int ShowCount { get; private set; }

        public int CloseCount { get; private set; }

        public HostComponentDisplayModel? LastShownComponent { get; private set; }

        public StructuredError? ShowError { get; set; }

        public StructuredError? CloseError { get; set; }

        public CoreResult<HostComponentDisplayModel> Show(HostComponentDisplayModel component)
        {
            ShowCount++;
            if (ShowError is not null)
            {
                return CoreResult<HostComponentDisplayModel>.Failure(ShowError);
            }

            IsOpen = true;
            LastShownComponent = component;
            return CoreResult<HostComponentDisplayModel>.Success(component);
        }

        public CoreResult<bool> Close()
        {
            CloseCount++;
            if (CloseError is not null)
            {
                return CoreResult<bool>.Failure(CloseError);
            }

            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
            return CoreResult<bool>.Success(true);
        }
    }

    private sealed class RecordingProbeAdapter : IExplorerTaskbarEmbedAdapter
    {
        public event EventHandler? Lost
        {
            add { }
            remove { }
        }

        public event EventHandler? Detached
        {
            add { }
            remove { }
        }

        public bool IsEmbedded
        {
            get => Lifecycle == ExplorerTaskbarProbeLifecycle.Embedded;
            set => Lifecycle = value
                ? ExplorerTaskbarProbeLifecycle.Embedded
                : ExplorerTaskbarProbeLifecycle.Detached;
        }

        public ExplorerTaskbarProbeLifecycle Lifecycle { get; private set; }

        public int DetachCount { get; private set; }

        public StructuredError? DetachError { get; set; }

        public CoreResult<ExplorerTaskbarProbeReport> TryEmbed(
            HostComponentDisplayModel component,
            ExplorerTaskbarProbeRequest request)
        {
            IsEmbedded = true;
            return CoreResult<ExplorerTaskbarProbeReport>.Success(new ExplorerTaskbarProbeReport(
                DateTimeOffset.UnixEpoch,
                "Test Windows",
                "X64",
                1,
                96,
                new Windows.Graphics.SizeInt32(1920, 48),
                new Windows.Graphics.RectInt32(0, 1032, 1920, 48),
                1600,
                ExplorerTaskbarProbePlacement.TrayNotifyAnchor,
                new Windows.Graphics.RectInt32(1352, 4, 240, 40),
                new Windows.Graphics.RectInt32(1352, 1036, 240, 40),
                new[] { new ExplorerTaskbarProbeStep("find_taskbar", true, null) }));
        }

        public CoreResult<bool> Detach()
        {
            DetachCount++;
            if (DetachError is not null)
            {
                return CoreResult<bool>.Failure(DetachError);
            }

            IsEmbedded = false;
            return CoreResult<bool>.Success(true);
        }
    }
}

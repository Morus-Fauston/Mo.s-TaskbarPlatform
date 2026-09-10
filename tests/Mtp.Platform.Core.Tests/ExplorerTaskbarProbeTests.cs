using System;
using System.Linq;
using Mtp.Host;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Platform.Core.Tests;

public sealed class ExplorerTaskbarProbeTests
{
    [Fact]
    public void SuccessfulProbeClosesDockWindowAndLeavesPreferencesUntouched()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent();
        Assert.True(harness.DockController.ShowCurrent().IsSuccess);
        var savesBeforeProbe = harness.PreferenceStore.SaveCount;

        var outcome = harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest());

        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.Report);
        Assert.False(outcome.FallbackShown);
        Assert.True(harness.ProbeController.State.IsEmbedded);
        Assert.Same(outcome.Report, harness.ProbeController.State.Report);
        Assert.Equal(harness.Component.Identity, harness.ProbeController.State.Component!.Identity);
        Assert.Equal(1, harness.DockAdapter.CloseCount);
        Assert.False(harness.DockAdapter.IsOpen);
        Assert.Equal(savesBeforeProbe, harness.PreferenceStore.SaveCount);
        Assert.True(harness.DisplayController.CurrentComponents.Single().IsVisible);
    }

    [Fact]
    public void FailedProbeFallsBackToIndependentDockWindowWithExplainableError()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent(
            embedError: new StructuredError("explorer_probe_taskbar_not_found", "The Explorer taskbar window was not found.", "simulated"));

        var outcome = harness.ProbeController.Run(
            harness.Component,
            new ExplorerTaskbarProbeRequest(SimulateTaskbarUnavailable: true, TransparencyMode: ProbeTransparencyMode.TintDiagnostic));

        Assert.False(outcome.Succeeded);
        Assert.Equal("explorer_probe_taskbar_not_found", outcome.Error!.Code);
        Assert.True(outcome.FallbackShown);
        Assert.Null(outcome.FallbackError);
        Assert.Equal(1, harness.DockAdapter.ShowCount);
        Assert.True(harness.DockAdapter.IsOpen);
        Assert.False(harness.ProbeController.State.IsEmbedded);
        Assert.Equal("explorer_probe_taskbar_not_found", harness.ProbeController.State.Error!.Code);
        Assert.True(harness.EmbedAdapter.LastRequest!.SimulateTaskbarUnavailable);
        Assert.Equal(ProbeTransparencyMode.TintDiagnostic, harness.EmbedAdapter.LastRequest.TransparencyMode);
        Assert.Equal(ProbeTransparencyMode.AcrylicController, new ExplorerTaskbarProbeRequest().TransparencyMode);
        Assert.Equal("任务栏嵌入当前不可用，已切换独立贴靠", ExplorerTaskbarProbeOutcome.FallbackMessage);
    }

    [Fact]
    public void FailedProbeReportsFallbackFailureAlongsideProbeError()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent(
            embedError: new StructuredError("explorer_probe_set_parent_failed", "The probe window could not be attached to the taskbar."),
            dockShowError: new StructuredError("dock_window_show_failed", "The dock window could not be created."));

        var outcome = harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest());

        Assert.False(outcome.Succeeded);
        Assert.Equal("explorer_probe_set_parent_failed", outcome.Error!.Code);
        Assert.False(outcome.FallbackShown);
        Assert.Equal("dock_window_show_failed", outcome.FallbackError!.Code);
        Assert.False(harness.ProbeController.State.IsEmbedded);
        Assert.True(harness.DisplayController.CurrentComponents.Single().IsVisible);
    }

    [Fact]
    public void HiddenOrUndeclaredComponentIsRejectedBeforeTouchingAdapters()
    {
        var harness = ProbeHarness.CreateWithHiddenComponent();

        var hidden = harness.ProbeController.RunCurrent(new ExplorerTaskbarProbeRequest());
        var undeclared = harness.ProbeController.Run(
            HostComponentDisplayModel.From(new Component(new StableIdentity(new StableId("other")), CapabilityState.Available), true),
            new ExplorerTaskbarProbeRequest());

        Assert.False(hidden.Succeeded);
        Assert.Equal("explorer_probe_component_not_visible", hidden.Error!.Code);
        Assert.False(undeclared.Succeeded);
        Assert.Equal("explorer_probe_component_not_declared", undeclared.Error!.Code);
        Assert.False(hidden.FallbackShown);
        Assert.False(undeclared.FallbackShown);
        Assert.Equal(0, harness.EmbedAdapter.EmbedCount);
        Assert.Equal(0, harness.DockAdapter.ShowCount);
        Assert.False(harness.ProbeController.State.IsEmbedded);
    }

    [Fact]
    public void DetachRemovesProbeAndRestoresDockWindow()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent();
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);
        Assert.False(harness.DockAdapter.IsOpen);

        var detached = harness.ProbeController.Detach();

        Assert.True(detached.IsSuccess);
        Assert.Equal(1, harness.EmbedAdapter.DetachCount);
        Assert.False(harness.EmbedAdapter.IsEmbedded);
        Assert.True(harness.DockAdapter.IsOpen);
        Assert.False(harness.ProbeController.State.IsEmbedded);
        Assert.Null(harness.ProbeController.State.Error);
        Assert.Null(harness.ProbeController.State.Report);
    }

    [Fact]
    public void DetachFailureKeepsEmbeddedStateAndReportsError()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent(
            detachError: new StructuredError("explorer_probe_detach_failed", "The probe window could not be removed from the taskbar."));
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);

        var detached = harness.ProbeController.Detach();

        Assert.False(detached.IsSuccess);
        Assert.Equal("explorer_probe_detach_failed", detached.Error!.Code);
        Assert.True(harness.ProbeController.State.IsEmbedded);
        Assert.Equal("explorer_probe_detach_failed", harness.ProbeController.State.Error!.Code);
    }

    [Fact]
    public void DetachReportsFailureWhenTheIndependentWindowCannotBeRestored()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent(
            dockShowError: new StructuredError("dock_window_show_failed", "The dock window could not be restored."));
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);

        var detached = harness.ProbeController.Detach();

        Assert.False(detached.IsSuccess);
        Assert.Equal("dock_window_show_failed", detached.Error!.Code);
        Assert.False(harness.ProbeController.State.IsEmbedded);
        Assert.Equal("dock_window_show_failed", harness.ProbeController.State.FallbackError!.Code);
    }

    [Fact]
    public void DelayedDetachConfirmationRestoresTheIndependentWindow()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent(
            detachError: new StructuredError("explorer_probe_close_unconfirmed", "The probe window did not confirm that it closed."));
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);

        var detached = harness.ProbeController.Detach();
        harness.EmbedAdapter.RaiseDetached();

        Assert.False(detached.IsSuccess);
        Assert.False(harness.ProbeController.State.IsEmbedded);
        Assert.Null(harness.ProbeController.State.Error);
        Assert.True(harness.DockAdapter.IsOpen);
        Assert.Equal(1, harness.DockAdapter.ShowCount);
    }

    [Fact]
    public void DelayedDetachConfirmationDuringShutdownDoesNotReopenTheIndependentWindow()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent(
            detachError: new StructuredError("explorer_probe_close_unconfirmed", "The probe window did not confirm that it closed."));
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);

        var shutdown = harness.ProbeController.Shutdown();
        harness.EmbedAdapter.RaiseDetached();

        Assert.False(shutdown.IsSuccess);
        Assert.False(harness.ProbeController.State.IsEmbedded);
        Assert.False(harness.DockAdapter.IsOpen);
        Assert.Equal(0, harness.DockAdapter.ShowCount);
    }

    [Fact]
    public void FailedDockRestoreRetainsTheComponentAndCanBeRetried()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent();
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);
        harness.DockAdapter.ShowError = new StructuredError("dock_window_show_failed", "The dock window could not be restored.");

        var failed = harness.ProbeController.Detach();

        Assert.False(failed.IsSuccess);
        Assert.Equal(harness.Component.Identity, harness.ProbeController.State.Component!.Identity);
        Assert.Equal("dock_window_show_failed", harness.ProbeController.State.FallbackError!.Code);

        harness.DockAdapter.ShowError = null;
        var retried = harness.ProbeController.Detach();

        Assert.True(retried.IsSuccess);
        Assert.True(harness.DockAdapter.IsOpen);
        Assert.Null(harness.ProbeController.State.Component);
        Assert.Null(harness.ProbeController.State.FallbackError);
    }

    [Fact]
    public void CleanupPendingStateMatchesTheAdapterAndRemainsRecoverable()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent(
            detachError: new StructuredError("explorer_probe_restore_style_failed", "The probe window style could not be restored."));
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);
        harness.EmbedAdapter.LifecycleOnDetachFailure = ExplorerTaskbarProbeLifecycle.CleanupPending;

        var failed = harness.ProbeController.Detach();

        Assert.False(failed.IsSuccess);
        Assert.Equal(ExplorerTaskbarProbeLifecycle.CleanupPending, harness.ProbeController.State.Lifecycle);
        Assert.False(harness.ProbeController.State.IsEmbedded);
        Assert.True(harness.ProbeController.State.IsRecoveryPending);
        Assert.Equal(harness.Component.Identity, harness.ProbeController.State.Component!.Identity);
    }

    [Fact]
    public void DelayedDetachFromARerunRestoresFallbackAndAllowsAnotherRun()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent();
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);
        harness.EmbedAdapter.DetachError = new StructuredError(
            "explorer_probe_close_unconfirmed",
            "The old probe did not confirm that it closed.");

        var failedRerun = harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest());
        harness.EmbedAdapter.RaiseDetached();

        Assert.False(failedRerun.Succeeded);
        Assert.True(harness.DockAdapter.IsOpen);
        Assert.Equal(ExplorerTaskbarProbeLifecycle.Detached, harness.ProbeController.State.Lifecycle);

        harness.EmbedAdapter.DetachError = null;
        var retried = harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest());

        Assert.True(retried.Succeeded);
        Assert.False(harness.DockAdapter.IsOpen);
        Assert.Equal(ExplorerTaskbarProbeLifecycle.Embedded, harness.ProbeController.State.Lifecycle);
    }

    [Fact]
    public void LostEmbeddedWindowResetsStateAndRestoresDockWindow()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent();
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);
        var stateChanges = 0;
        harness.ProbeController.StateChanged += (_, _) => stateChanges++;

        harness.EmbedAdapter.RaiseLost();

        Assert.Equal(1, stateChanges);
        Assert.False(harness.ProbeController.State.IsEmbedded);
        Assert.Equal("explorer_probe_host_window_lost", harness.ProbeController.State.Error!.Code);
        Assert.True(harness.DockAdapter.IsOpen);
        Assert.True(harness.DisplayController.CurrentComponents.Single().IsVisible);
    }

    [Fact]
    public void LostEmbeddedWindowPreservesBothTheLossAndFallbackErrors()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent(
            dockShowError: new StructuredError("dock_window_show_failed", "The dock window could not be restored."));
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);

        harness.EmbedAdapter.RaiseLost();

        Assert.Equal("explorer_probe_host_window_lost", harness.ProbeController.State.Error!.Code);
        Assert.Equal("dock_window_show_failed", harness.ProbeController.State.FallbackError!.Code);
        Assert.Equal(harness.Component.Identity, harness.ProbeController.State.Component!.Identity);
        Assert.True(harness.ProbeController.State.IsRecoveryPending);
    }

    [Fact]
    public void RunningAgainWhileEmbeddedDetachesTheOldProbeFirst()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent();
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);

        var second = harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest());

        Assert.True(second.Succeeded);
        Assert.Equal(1, harness.EmbedAdapter.DetachCount);
        Assert.Equal(2, harness.EmbedAdapter.EmbedCount);
        Assert.True(harness.ProbeController.State.IsEmbedded);
    }

    [Fact]
    public void RunningAgainStopsWhenTheOldProbeCannotBeDetached()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent();
        Assert.True(harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest()).Succeeded);
        harness.EmbedAdapter.DetachError = new StructuredError(
            "explorer_probe_detach_failed",
            "The old probe could not be detached.");

        var second = harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest());

        Assert.False(second.Succeeded);
        Assert.Equal("explorer_probe_detach_failed", second.Error!.Code);
        Assert.Equal(1, harness.EmbedAdapter.EmbedCount);
        Assert.True(harness.ProbeController.State.IsEmbedded);
    }

    [Fact]
    public void SuccessfulEmbedReportsDockCloseFailureAsAnIncompleteTransition()
    {
        var harness = ProbeHarness.CreateWithVisibleComponent(
            dockCloseError: new StructuredError("dock_window_close_failed", "The old dock window could not close."));
        Assert.True(harness.DockController.ShowCurrent().IsSuccess);

        var outcome = harness.ProbeController.Run(harness.Component, new ExplorerTaskbarProbeRequest());

        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Report);
        Assert.Equal("dock_window_close_failed", outcome.Error!.Code);
        Assert.True(harness.ProbeController.State.IsEmbedded);
        Assert.Equal("dock_window_close_failed", harness.ProbeController.State.Error!.Code);
    }

    [Fact]
    public void ReportFormatterListsExperimentalScopeAndUnverifiedItems()
    {
        var text = ExplorerTaskbarProbeReportFormatter.Format(ProbeHarness.CreateReport());

        Assert.StartsWith(ExplorerTaskbarProbeReportFormatter.SuccessHeadline, text, StringComparison.Ordinal);
        Assert.Contains("不代表正式支持", text, StringComparison.Ordinal);
        Assert.Equal(
            new[] { "多屏", "DPI 变化", "任务栏自动隐藏", "Explorer 重启", "第三方任务栏工具" },
            ExplorerTaskbarProbeReport.UnverifiedScope);
        foreach (var item in ExplorerTaskbarProbeReport.UnverifiedScope)
        {
            Assert.Contains(item, text, StringComparison.Ordinal);
        }

        Assert.Contains("tray_notify_window", text, StringComparison.Ordinal);
        Assert.Contains("find_taskbar", text, StringComparison.Ordinal);
    }

    private sealed class ProbeHarness
    {
        private ProbeHarness(
            HostDisplayController displayController,
            CountingPreferenceStore preferenceStore,
            RecordingDockWindowAdapter dockAdapter,
            IndependentDockWindowController dockController,
            RecordingEmbedAdapter embedAdapter,
            ExplorerTaskbarProbeController probeController,
            HostComponentDisplayModel component)
        {
            DisplayController = displayController;
            PreferenceStore = preferenceStore;
            DockAdapter = dockAdapter;
            DockController = dockController;
            EmbedAdapter = embedAdapter;
            ProbeController = probeController;
            Component = component;
        }

        public HostDisplayController DisplayController { get; }

        public CountingPreferenceStore PreferenceStore { get; }

        public RecordingDockWindowAdapter DockAdapter { get; }

        public IndependentDockWindowController DockController { get; }

        public RecordingEmbedAdapter EmbedAdapter { get; }

        public ExplorerTaskbarProbeController ProbeController { get; }

        public HostComponentDisplayModel Component { get; }

        public static ProbeHarness CreateWithVisibleComponent(
            StructuredError? embedError = null,
            StructuredError? detachError = null,
            StructuredError? dockShowError = null,
            StructuredError? dockCloseError = null)
        {
            var harness = Create(embedError, detachError, dockShowError, dockCloseError);
            var visible = harness.DisplayController.SetVisibility(harness.Component.Identity, true);
            Assert.True(visible.IsSuccess);
            return new ProbeHarness(
                harness.DisplayController,
                harness.PreferenceStore,
                harness.DockAdapter,
                harness.DockController,
                harness.EmbedAdapter,
                harness.ProbeController,
                visible.Value!);
        }

        public static ProbeHarness CreateWithHiddenComponent() => Create(null, null, null, null);

        public static ExplorerTaskbarProbeReport CreateReport() => new(
            DateTimeOffset.UnixEpoch,
            "Test Windows",
            "X64",
            1,
            96,
            new SizeInt32(1920, 48),
            new RectInt32(0, 1032, 1920, 48),
            1600,
            ExplorerTaskbarProbePlacement.TrayNotifyAnchor,
            new RectInt32(1352, 4, 240, 40),
            new RectInt32(1352, 1036, 240, 40),
            new[] { new ExplorerTaskbarProbeStep("find_taskbar", true, null) });

        private static ProbeHarness Create(
            StructuredError? embedError,
            StructuredError? detachError,
            StructuredError? dockShowError,
            StructuredError? dockCloseError)
        {
            var preferenceStore = new CountingPreferenceStore();
            var displayController = new HostDisplayController(new StubDeclarationSource(ValidJson()), preferenceStore);
            var component = displayController.Load().Components.Single();
            var dockAdapter = new RecordingDockWindowAdapter
            {
                ShowError = dockShowError,
                CloseError = dockCloseError,
            };
            var dockController = new IndependentDockWindowController(displayController, dockAdapter);
            var embedAdapter = new RecordingEmbedAdapter { EmbedError = embedError, DetachError = detachError };
            var probeController = new ExplorerTaskbarProbeController(displayController, embedAdapter, dockController);
            return new ProbeHarness(displayController, preferenceStore, dockAdapter, dockController, embedAdapter, probeController, component);
        }

        private static string ValidJson() => """
            {
              "applicationId": "music",
              "featureGroups": [
                {
                  "featureGroupId": "controls",
                  "components": [
                    { "componentId": "widget", "actionSlots": [{ "actionSlotId": "go" }] }
                  ],
                  "taskbarFlyouts": [
                    { "taskbarFlyoutId": "panel", "actionSlots": [{ "actionSlotId": "go" }] }
                  ]
                }
              ]
            }
            """;
    }

    private sealed class StubDeclarationSource : IDeclarationSource
    {
        private readonly string content;

        public StubDeclarationSource(string content) => this.content = content;

        public CoreResult<string> Read() => CoreResult<string>.Success(content);
    }

    private sealed class CountingPreferenceStore : IComponentDisplayPreferenceStore
    {
        private ComponentDisplayPreferences preferences = new();

        public int SaveCount { get; private set; }

        public ComponentDisplayPreferenceLoadResult Load() =>
            new(preferences, null, ComponentDisplayPreferenceLoadState.Loaded);

        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool isVisible)
        {
            SaveCount++;
            preferences = preferences.WithVisibility(identity, isVisible);
            return CoreResult<ComponentDisplayPreferences>.Success(preferences);
        }
    }

    private sealed class RecordingDockWindowAdapter : IIndependentDockWindowAdapter
    {
        public event EventHandler? Closed;

        public bool IsOpen { get; private set; }

        public StructuredError? ShowError { get; set; }

        public StructuredError? CloseError { get; init; }

        public int ShowCount { get; private set; }

        public int CloseCount { get; private set; }

        public CoreResult<HostComponentDisplayModel> Show(HostComponentDisplayModel component)
        {
            ShowCount++;
            if (ShowError is not null)
            {
                return CoreResult<HostComponentDisplayModel>.Failure(ShowError);
            }

            IsOpen = true;
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

    private sealed class RecordingEmbedAdapter : IExplorerTaskbarEmbedAdapter
    {
        public event EventHandler? Lost;

        public event EventHandler? Detached;

        public ExplorerTaskbarProbeLifecycle Lifecycle { get; private set; } = ExplorerTaskbarProbeLifecycle.Detached;

        public bool IsEmbedded => Lifecycle == ExplorerTaskbarProbeLifecycle.Embedded;

        public ExplorerTaskbarProbeLifecycle LifecycleOnDetachFailure { get; set; } = ExplorerTaskbarProbeLifecycle.Embedded;

        public StructuredError? EmbedError { get; init; }

        public StructuredError? DetachError { get; set; }

        public int EmbedCount { get; private set; }

        public int DetachCount { get; private set; }

        public ExplorerTaskbarProbeRequest? LastRequest { get; private set; }

        public CoreResult<ExplorerTaskbarProbeReport> TryEmbed(HostComponentDisplayModel component, ExplorerTaskbarProbeRequest request)
        {
            EmbedCount++;
            LastRequest = request;
            if (EmbedError is not null)
            {
                return CoreResult<ExplorerTaskbarProbeReport>.Failure(EmbedError);
            }

            Lifecycle = ExplorerTaskbarProbeLifecycle.Embedded;
            return CoreResult<ExplorerTaskbarProbeReport>.Success(ProbeHarness.CreateReport());
        }

        public CoreResult<bool> Detach()
        {
            DetachCount++;
            if (DetachError is not null)
            {
                Lifecycle = LifecycleOnDetachFailure;
                return CoreResult<bool>.Failure(DetachError);
            }

            Lifecycle = ExplorerTaskbarProbeLifecycle.Detached;
            return CoreResult<bool>.Success(true);
        }

        public void RaiseLost()
        {
            Lifecycle = ExplorerTaskbarProbeLifecycle.Detached;
            Lost?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseDetached()
        {
            Lifecycle = ExplorerTaskbarProbeLifecycle.Detached;
            Detached?.Invoke(this, EventArgs.Empty);
        }
    }
}

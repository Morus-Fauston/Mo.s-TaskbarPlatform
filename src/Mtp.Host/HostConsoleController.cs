using Microsoft.UI.Xaml;
using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>Production composition and display actions, separate from the ordinary control window.</summary>
internal sealed class HostConsoleController
{
    private readonly HostDisplayController display;
    private readonly ITaskbarDockPreferenceStore store;
    private readonly IslandDisplayAdapter adapter;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TaskbarEnvironmentMonitor? monitor;
    private readonly List<string> errors = [];
    private bool closing;
    private bool refreshing;
    private HostTestConfiguration applied = new();
    public HostComponentDisplayModel? Component => display.CurrentComponents.FirstOrDefault();
    public TaskbarDockPreferences Preferences { get; private set; }
    public IslandDisplaySession Session { get; }
    public HostTestController Tests { get; }
    public IslandPreviewWindow Preview { get; } = new();
    public bool SimulateUnavailable { get; private set; }
    public IReadOnlyList<string> Errors => errors;
    public string Target => adapter.TargetDescription;
    public string TargetSummary => adapter.TargetSummary;
    public string MaterialStatus => adapter.MaterialStatus;
    public string Notice { get; private set; } = "";
    public StructuredError? PreferenceError { get; private set; }
    public bool PopupOpen => adapter.PopupOpen;
    public event Action? Changed;
    public Action<string> OpenDirectory { get; set; } = path => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    public HostConsoleController(HostDisplayController display, HostDisplayLoadResult loaded, ITaskbarDockPreferenceStore store,
        Func<TaskbarDockPreferences, CoreResult<IslandTarget>> capture, string evidenceRoot, Win32TaskbarDockEnvironment? environment = null)
    {
        this.display = display; this.store = store;
        var preferences = store.Load();
        Preferences = preferences.Value ?? new(); PreferenceError = preferences.Error;
        foreach (var error in loaded.Errors) AddError(error);
        if (PreferenceError is not null) AddError(PreferenceError);
        adapter = new IslandDisplayAdapter(capture, (kind, value) => Tests?.Observe(kind, value));
        Session = new IslandDisplaySession(adapter);
        Tests = new HostTestController(adapter, Reconfigure, () => new { Component, Preferences, State = Session.State.ToString(), Target }, evidenceRoot, () => !Preview.IsOpen);
        Tests.Changed += Notify;
        Preview.Changed += Notify;
        adapter.Lost += Refresh;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (environment is not null)
            monitor = new(action => dispatcher.TryEnqueue(() => action()), Refresh, () => environment.ObservedTaskbar);
        timer.Tick += (_, _) => Refresh();
        timer.Start();
    }
    public void Refresh()
    {
        if (closing || refreshing) return;
        refreshing = true;
        try
        {
            var prepared = adapter.Prepare(Preferences, Component, Tests.Configuration, SimulateUnavailable);
            Session.SetIntent(Component?.IsVisible == true);
            if (!prepared.IsSuccess && Component?.IsVisible == true) AddError(prepared.Error!);
            Session.Refresh(prepared.Value);
            if (Session.State == IslandDisplayState.Embedded) adapter.RefreshPlacement();
            if (Session.Error is not null) AddError(Session.Error);
            if (monitor?.Error is not null) AddError(monitor.Error);
        }
        catch (Exception error) { AddError(new("island_refresh_failed", error.Message)); }
        finally { refreshing = false; Notify(); }
    }
    private void Reconfigure(bool force)
    {
        if (closing) return;
        if (!Tests.IsRunning) SimulateUnavailable = false;
        if (force || applied != Tests.Configuration)
        {
            applied = Tests.Configuration;
            var stopped = adapter.Close();
            if (!stopped.IsSuccess) AddError(stopped.Error!);
            adapter.Prepare(Preferences, Component, applied, SimulateUnavailable);
            Session.Retry();
        }
        Refresh();
    }
    public void SetVisibility(bool visible)
    {
        if (Component is null) return;
        var result = display.SetVisibility(Component.Identity, visible);
        if (!result.IsSuccess) AddError(result.Error!);
        Tests.Observe("user-visibility", new { requested = visible, accepted = result.IsSuccess });
        Refresh();
    }
    public void SetDisplay(string? id) => ApplyPreference(store.CommitDisplay(id));
    public void SetGap(int gap) => ApplyPreference(store.CommitGap(gap));
    private void ApplyPreference(CoreResult<TaskbarDockPreferences> result)
    {
        PreferenceError = result.Error;
        if (result.IsSuccess) Preferences = result.Value!;
        else AddError(result.Error!);
        Tests.Observe("user-placement", new { Preferences, accepted = result.IsSuccess });
        Refresh();
    }
    public void SetSimulation(bool unavailable)
    {
        SimulateUnavailable = unavailable;
        Tests.Observe("failure-injection", unavailable);
        Refresh();
    }
    public void Retry() { Refresh(); Session.Retry(); Refresh(); }
    public void OpenPreview(nint owner, HostTestConfiguration configuration)
    {
        if (closing) return;
        if (Tests.IsRunning) throw new InvalidOperationException("先停止任务栏案例，再打开独立预览。");
        if (Component is null) throw new InvalidOperationException("没有可用于预览的有效组件。");
        Preview.Open(owner, Component, configuration);
    }
    public void Execute(Action action)
    {
        try { action(); }
        catch (Exception error) { AddError(new("host_action_failed", error.Message)); }
        Notify();
    }
    public void Export() { Notice = "报告已导出：" + Tests.Export(); Notify(); }
    public void OpenEvidence() { System.IO.Directory.CreateDirectory(Tests.EvidenceDirectory); OpenDirectory(Tests.EvidenceDirectory); Notice = "打开证据目录：" + Tests.EvidenceDirectory; Notify(); }
    public bool Shutdown()
    {
        closing = true;
        timer.Stop();
        var testStopped = true;
        try { Preview.Close(); }
        catch (Exception error) { testStopped = false; AddError(new("preview_shutdown_failed", error.Message)); }
        try { Tests.Shutdown(); }
        catch (Exception error) { testStopped = false; AddError(new("test_shutdown_failed", error.Message)); }
        if (monitor?.TryStop() == false) { AddError(monitor.Error!); return false; }
        var closed = Session.Shutdown();
        if (!closed && Session.Error is not null) AddError(Session.Error);
        return closed && testStopped;
    }
    private void AddError(StructuredError error)
    {
        var text = $"{error.Code}: {error.Message}";
        if (!errors.Contains(text)) { errors.Add(text); Tests?.Observe("host-error", text); }
    }
    private void Notify() => Changed?.Invoke();
}

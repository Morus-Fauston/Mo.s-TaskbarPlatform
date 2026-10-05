using Microsoft.UI.Xaml;
using Mtp.Contracts;
using Mtp.Host.Islands;
using Mtp.Host.Templates;
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
    private HostBrokerSession? communication;
    private readonly CancellationTokenSource communicationLifetime = new();
    private Task? communicationStartup;
    private readonly RegisteredImageCache images = new();
    public HostComponentDisplayModel? Component
    {
        get
        {
            var model = display.CurrentComponents.FirstOrDefault(value => value.Identity.Segments[0].Value == "counter")
                ?? display.CurrentComponents.FirstOrDefault();
            if (model is null || communication is null) return model;
            var id = model.Identity.Segments;
            var snapshot = communication.States.GetSnapshot(id[0].Value);
            var action = snapshot?.Declaration?.ActionSlots.FirstOrDefault(slot =>
                slot.Reference.EntryKind == ActionEntryKind.Component && slot.Reference.FeatureGroupId == id[1].Value &&
                slot.Reference.EntryId == id[2].Value && slot.ParameterKind == ActionParameterKind.None)?.Reference;
            var busy = action is not null && communication.Actions.GetPending(id[0].Value).Any(item => item.Slot == action && item.IsBusy);
            return model with { Action = action, CanInvokeAction = snapshot?.IsInteractive == true && action is not null && !busy, ActionBusy = busy };
        }
    }
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
    public bool CanRetryCommunication => !closing && communication is not null &&
        (communication.BrokerRecovery.CanRetry || communication.GetRecovery("counter")?.CanRetry == true);
    public string CommunicationStatus => communication is null ? "通信尚未启动" :
        $"平台通信：{RecoveryLabel(communication.BrokerRecovery)}；计数器：{RecoveryLabel(communication.GetRecovery("counter"))}";

    private static string RecoveryLabel(RecoverySnapshot? value) => value?.State switch
    {
        RecoveryState.Available => "已连接",
        RecoveryState.Recovering => "正在恢复",
        RecoveryState.Exhausted => "恢复已停止，可手动重试" + (value.Error is null ? "" : $"（{value.Error}）"),
        _ => "尚未连接"
    };

    public async Task RetryCommunicationAsync()
    {
        if (!CanRetryCommunication || communication is null) return;
        try
        {
            var pending = communication.BrokerRecovery.CanRetry
                ? communication.RetryBrokerAsync(communicationLifetime.Token)
                : communication.RetryApplicationAsync("counter", communicationLifetime.Token);
            Refresh();
            var result = await pending;
            Tests.Observe("communication-retry", new { result.Code, result.Accepted });
            if (!result.Accepted) AddError(new(result.Code, result.Message));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!closing) AddError(new("communication_retry_failed", error.Message)); }
        finally { Refresh(); }
    }
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
        adapter = new IslandDisplayAdapter(capture, (kind, value) => Tests?.Observe(kind, value), InvokeActionAsync, CreateTemplate);
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
    private TemplateRenderer? CreateTemplate(HostComponentDisplayModel component)
    {
        var session = communication;
        if (closing || session is null || !component.HasTemplate) return null;
        var ids = component.Identity.Segments;
        var controller = new TemplateInteractionController(session.States, ids[0].Value,
            new(ids[1].Value, TemplateEntryKind.Component, ids[2].Value),
            (slot, parameter, expected, token) => session.SendActionAsync(slot, parameter, token, expected));
        return new TemplateRenderer(controller, images);
    }
    private async Task InvokeActionAsync(ActionSlotReference slot)
    {
        if (closing || communication is null) return;
        try
        {
            var pending = communication.SendActionAsync(slot, new ActionParameter(), communicationLifetime.Token);
            Refresh();
            var result = await pending;
            if (!result.Accepted) AddError(new(result.Code, result.Message));
            Tests.Observe("declared-action-result", new { slot, result.Code, result.Accepted });
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!closing) AddError(new("ActionNotAvailable", "动作通道不可用，保留最后确认值。")); }
        finally { Refresh(); }
    }
    public void Refresh()
    {
        if (closing || refreshing) return;
        refreshing = true;
        try
        {
            if (communication is not null)
            {
                display.BindActivityPermissions(communication.States);
                display.ApplyBrokerSnapshots(communication.States.Snapshots);
            }
            if (communication?.LastError is { } connectionError) AddError(new(connectionError, "平台通信不可用，已保留最后确认读数。"));
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
    public Task StartCounterAsync(string brokerPath, string counterPath, bool templates = false)
    {
        if (communicationStartup is not null) return communicationStartup;
        if (templates)
        {
            var registration = images.Register("counter", "status", ImageResourceFormat.Png,
                Path.Combine(AppContext.BaseDirectory, "Assets", "template-status.png"));
            if (!registration.Accepted) AddError(new(registration.Code, registration.Message));
        }
        communicationStartup = StartCounterCoreAsync(brokerPath, counterPath, templates);
        return communicationStartup;
    }
    private async Task StartCounterCoreAsync(string brokerPath, string counterPath, bool templates)
    {
        HostBrokerSession? started = null;
        try
        {
            started = await HostBrokerSession.StartAsync(brokerPath, ["counter"], communicationLifetime.Token).ConfigureAwait(false);
            communication = started;
            await started.StartServiceAsync("counter", counterPath, communicationLifetime.Token,
                templates ? ["--templates"] : null).ConfigureAwait(false);
            if (communicationLifetime.IsCancellationRequested)
            {
                await started.DisposeAsync().ConfigureAwait(false);
                communication = null;
            }
        }
        catch (BrokerStartCleanupException error)
        {
            communication = error.CleanupOwner;
            throw;
        }
        catch
        {
            if (started is not null)
            {
                await started.DisposeAsync().ConfigureAwait(false);
                communication = null;
            }
            throw;
        }
    }
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
        display.Dispose();
        var testStopped = true;
        communicationLifetime.Cancel();
        try
        {
            communicationStartup?.GetAwaiter().GetResult();
        }
        catch (Exception error) { AddError(new("communication_start_failed", error.Message)); }
        try
        {
            communication?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            communication = null;
        }
        catch (Exception error) { testStopped = false; AddError(new("communication_shutdown_failed", error.Message)); }
        try { Preview.Close(); }
        catch (Exception error) { testStopped = false; AddError(new("preview_shutdown_failed", error.Message)); }
        try { Tests.Shutdown(); }
        catch (Exception error) { testStopped = false; AddError(new("test_shutdown_failed", error.Message)); }
        if (monitor?.TryStop() == false) { AddError(monitor.Error!); return false; }
        var closed = Session.Shutdown();
        if (closed) images.Dispose();
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

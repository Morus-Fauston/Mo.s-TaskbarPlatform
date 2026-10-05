using Microsoft.UI.Xaml;
using Mtp.Contracts;
using Mtp.Host.Islands;
using Mtp.Host.Flyouts;
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
    private readonly Microsoft.UI.Dispatching.DispatcherQueue dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private int refreshQueued;
    private readonly TaskbarEnvironmentMonitor? monitor;
    private readonly List<string> errors = [];
    private bool closing;
    private bool refreshing;
    private HostTestConfiguration applied = new();
    private HostBrokerSession? communication;
    private readonly CancellationTokenSource communicationLifetime = new();
    private Task? communicationStartup;
    private bool dynamicDemo;
    private readonly RegisteredImageCache images = new();
    private readonly HostFlyoutRequestQueue flyoutRequests = new();
    private readonly Func<IReadOnlyList<TaskbarDockDisplay>> getDisplays;
    private TaskbarFlyoutCoordinator? flyouts;
    internal Action<string, object?>? FlyoutDiagnostics { get; set; }
    private HostSettingsController? settings;
    public HostAppearancePreferences Appearance { get; private set; } = new();
    private HostTestConfiguration DisplayConfiguration => Tests.IsRunning ? Tests.Configuration : settings is null ? new() :
        new(Appearance.Material is MaterialKind.None or MaterialKind.Solid ? "none" : Appearance.Material.ToString().ToLowerInvariant(),
            Appearance.Material == MaterialKind.None ? 1 : Appearance.Opacity, Appearance.Theme.ToString().ToLowerInvariant());
    private bool UseGroupDisplay => (settings is not null || dynamicDemo) && !Tests.IsRunning;
    public IReadOnlyList<BrokerApplicationSnapshot> Applications => communication?.States.Snapshots ?? [];
    public RecoverySnapshot? GetRecovery(string? applicationId) => applicationId is null ? communication?.BrokerRecovery : communication?.GetRecovery(applicationId);
    public void AttachSettings(HostSettingsController value)
    {
        settings = value;
        ApplyAppearance(value.GetSnapshot().Preferences.Appearance);
    }
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        if (!dispatcher.TryEnqueue(() => { Interlocked.Exchange(ref refreshQueued, 0); if (!closing) Refresh(); }))
            Interlocked.Exchange(ref refreshQueued, 0);
    }
    public void ApplyAppearance(HostAppearancePreferences value)
    {
        if (closing) return;
        Appearance = value;
        if (!Tests.IsRunning) adapter.ApplyAppearance(value);
        flyouts?.Manager.ApplyAppearance(value);
        Refresh();
    }
    public async Task<ProtocolResult> RetryAsync(string? applicationId, CancellationToken token = default)
    {
        if (closing || communication is null) return ProtocolResult.Reject("ActionNotAvailable", "通信尚未启动");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, communicationLifetime.Token);
        var result = applicationId is null ? await communication.RetryBrokerAsync(linked.Token) :
            await communication.RetryApplicationAsync(applicationId, linked.Token);
        Refresh();
        return result;
    }
    public IReadOnlyList<HostComponentDisplayModel> Components
    {
        get
        {
            var models = settings?.GetSnapshot().Components ?? display.CurrentComponents;
            var session = communication;
            if (session is null) return models;
            var snapshots = session.States.Snapshots.ToDictionary(value => value.ApplicationId, StringComparer.Ordinal);
            var actions = snapshots.Values.Where(value => value.Declaration is not null)
                .SelectMany(value => value.Declaration!.ActionSlots)
                .Where(value => value.Reference.EntryKind == ActionEntryKind.Component && value.ParameterKind == ActionParameterKind.None)
                .GroupBy(value => (value.Reference.ApplicationId, value.Reference.FeatureGroupId, value.Reference.EntryId))
                .ToDictionary(group => group.Key, group => group.First().Reference);
            var pending = snapshots.Keys.SelectMany(id => session.Actions.GetPending(id)).Where(value => value.IsBusy)
                .Select(value => value.Slot).ToHashSet();
            return Array.AsReadOnly(models.Select(model =>
            {
                var id = model.Identity.Segments;
                snapshots.TryGetValue(id[0].Value, out var snapshot);
                actions.TryGetValue((id[0].Value, id[1].Value, id[2].Value), out var action);
                bool busy = action is not null && pending.Contains(action);
                return model with { Action = action, CanInvokeAction = snapshot?.IsInteractive == true && action is not null && !busy, ActionBusy = busy };
            }).ToArray());
        }
    }
    public HostComponentDisplayModel? Component
    {
        get
        {
            var components = Components;
            return settings is not null ? components.FirstOrDefault(value => value.IsVisible) ?? components.FirstOrDefault() :
                components.FirstOrDefault(value => value.Identity.Segments[0].Value == "counter") ?? components.FirstOrDefault();
        }
    }
    public TaskbarDockPreferences Preferences { get; private set; }
    public IslandDisplaySession Session { get; }
    public HostTestController Tests { get; }
    public IslandPreviewWindow Preview { get; } = new();
    public bool SimulateUnavailable { get; private set; }
    public IReadOnlyList<string> Errors => errors;
    public string? CurrentError => PreferenceError?.Message ?? Session.Error?.Message ?? communication?.LastError;
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
        getDisplays = environment is null ? () => [] : environment.GetDisplays;
        var preferences = store.Load();
        Preferences = preferences.Value ?? new(); PreferenceError = preferences.Error;
        foreach (var error in loaded.Errors) AddError(error);
        if (PreferenceError is not null) AddError(PreferenceError);
        adapter = new IslandDisplayAdapter(capture, (kind, value) => Tests?.Observe(kind, value), InvokeActionAsync, CreateTemplate, ActivateItem);
        Session = new IslandDisplaySession(adapter);
        Tests = new HostTestController(adapter, Reconfigure, () => new { Component, Preferences, State = Session.State.ToString(), Target }, evidenceRoot, () => !Preview.IsOpen);
        Tests.Changed += Notify;
        Preview.Changed += Notify;
        adapter.Lost += Refresh;
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
        TemplateRenderer? renderer = null;
        var controller = new TemplateInteractionController(session.States, ids[0].Value,
            new(ids[1].Value, TemplateEntryKind.Component, ids[2].Value),
            (slot, parameter, expected, token) => session.SendActionAsync(slot, parameter, token, expected),
            navigate: intent => renderer is not null && !closing && ReferenceEquals(session, communication)
                ? EnsureFlyouts().Navigate(renderer, intent) : ProtocolResult.Reject("StaleSession", "入口已失效"));
        return renderer = new TemplateRenderer(controller, images);
    }
    private TaskbarFlyoutCoordinator EnsureFlyouts()
    {
        if (flyouts is not null) return flyouts;
        flyouts = new(communication!, adapter, getDisplays, images, RecordFlyout);
        flyouts.Manager.ApplyAppearance(Appearance);
        return flyouts;
    }
    private void RecordFlyout(string kind, object? value)
    {
        Tests.Observe(kind, value);
        try { FlyoutDiagnostics?.Invoke(kind, value); } catch (Exception) { }
    }
    private void DrainFlyouts()
    {
        if (communication is not { } current) return;
        foreach (var queued in flyoutRequests.Drain())
        {
            ProtocolResult result;
            try { result = EnsureFlyouts().Present(queued); }
            catch (Exception) { result = ProtocolResult.Reject("FlyoutUnavailable", "该显示请求未能完成"); }
            var message = queued.Message;
            current.States.FlyoutRequests.RecordPresentationResult(message.ApplicationId, message.SessionId, message.Flyout!, result);
            RecordFlyout("flyout-presentation-result", new { message.ApplicationId, message.SessionId, message.Flyout!.RequestSequence, result.Code, result.Accepted });
        }
        flyouts?.Refresh();
        if (flyoutRequests.Count > 0) RequestRefresh();
    }
    private async Task InvokeActionAsync(ActionSlotReference slot)
    {
        var session = communication;
        if (closing || session is null) return;
        var expectedSession = session.States.GetSnapshot(slot.ApplicationId)?.SessionId;
        if (expectedSession is null) return;
        bool IsCurrent() => !closing && ReferenceEquals(communication, session) &&
            session.States.GetSnapshot(slot.ApplicationId)?.SessionId == expectedSession;
        try
        {
            var pending = session.SendActionAsync(slot, new ActionParameter(), communicationLifetime.Token, expectedSession);
            Refresh();
            var result = await pending;
            if (!IsCurrent()) return;
            if (!result.Accepted) AddError(new(result.Code, result.Message));
            Tests.Observe("declared-action-result", new { slot, result.Code, result.Accepted });
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { if (IsCurrent()) AddError(new("ActionNotAvailable", "动作通道不可用，保留最后确认值。")); }
        finally { if (IsCurrent()) Refresh(); }
    }
    private async Task ActivateItem(ItemInteractionHandle handle, string? control)
    {
        if (closing || communication is null || display.ItemActivations is null) return;
        ItemActivationSource? source = control switch
        {
            null => ItemActivationSource.BlankPrimary,
            "PrimaryButton" => ItemActivationSource.PrimaryButton,
            "SecondaryButton" => ItemActivationSource.SecondaryButton,
            _ => null
        };
        if (source is null) { AddError(new("InvalidActivationSource", "项控件来源无效。")); Refresh(); return; }
        var routed = display.ItemActivations.Activate(handle, source.Value);
        if (!routed.Result.Accepted) AddError(new(routed.Result.Code, routed.Result.Message));
        else if (routed.Action is not null) await InvokeItemActionAsync(communication, routed);
        else if (routed.TaskbarFlyoutId is not null)
        {
            var result = EnsureFlyouts().OpenItem(routed, control);
            if (!result.Accepted) AddError(new(result.Code, result.Message));
        }
        Refresh();
    }
    private async Task InvokeItemActionAsync(HostBrokerSession session, HostItemActivationResult routed)
    {
        if (closing || !ReferenceEquals(communication, session) || routed.Origin is null || routed.SessionId is null ||
            display.ItemPresentations?.Resolve(routed.Origin) is not { } current || current.SessionId != routed.SessionId)
            return;
        bool IsCurrent() => !closing && ReferenceEquals(communication, session) &&
            session.States.GetSnapshot(routed.Origin.Item.ApplicationId)?.SessionId == routed.SessionId &&
            display.ItemPresentations?.Resolve(routed.Origin)?.SessionId == routed.SessionId;
        try
        {
            var pending = session.SendActionAsync(routed.Action!, new(), communicationLifetime.Token, routed.SessionId);
            Refresh();
            var result = await pending;
            if (!IsCurrent()) return;
            if (!result.Accepted) AddError(new(result.Code, result.Message));
            Tests.Observe("item-action-result", new { routed.Action, result.Code, result.Accepted });
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { if (IsCurrent()) AddError(new("ActionNotAvailable", "项动作通道不可用，保留最后确认值。")); }
        finally { if (IsCurrent()) Refresh(); }
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
            var components = Components;
            bool visible = UseGroupDisplay ? components.Any(value => value.IsVisible) : Component?.IsVisible == true;
            var prepared = PrepareDisplay(components, DisplayConfiguration);
            Session.SetIntent(visible);
            if (!prepared.IsSuccess && visible) AddError(prepared.Error!);
            Session.Refresh(prepared.Value);
            if (Session.State == IslandDisplayState.Embedded) adapter.RefreshPlacement();
            DrainFlyouts();
            if (Session.Error is not null) AddError(Session.Error);
            if (monitor?.Error is not null) AddError(monitor.Error);
        }
        catch (Exception error) { AddError(new("island_refresh_failed", error.Message)); }
        finally { refreshing = false; Notify(); }
    }
    private CoreResult<string> PrepareDisplay(IReadOnlyList<HostComponentDisplayModel> components, HostTestConfiguration configuration) =>
        UseGroupDisplay ? adapter.PrepareGroup(Preferences, components, configuration, SimulateUnavailable, communication?.States, display.ItemPresentations, settings?.GetSnapshot().Preferences.IslandGrouping) :
        adapter.Prepare(Preferences, Component, configuration, SimulateUnavailable);
    private void Reconfigure(bool force)
    {
        if (closing) return;
        if (!Tests.IsRunning) SimulateUnavailable = false;
        if (force || applied != DisplayConfiguration)
        {
            applied = DisplayConfiguration;
            var stopped = adapter.Close();
            if (!stopped.IsSuccess) AddError(stopped.Error!);
            PrepareDisplay(Components, applied);
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
    public Task StartCounterAsync(string brokerPath, string counterPath, bool templates = false, bool dynamic = false, bool timers = false, bool presets = false, bool flyouts = false, bool organization = false)
    {
        if ((templates ? 1 : 0) + (dynamic ? 1 : 0) + (timers ? 1 : 0) + (presets ? 1 : 0) + (flyouts ? 1 : 0) + (organization ? 1 : 0) > 1) throw new ArgumentException("一次只能启动一种演示。");
        if (communicationStartup is not null) return communicationStartup;
        dynamicDemo = dynamic || timers || presets || flyouts || organization;
        if (templates)
        {
            var registration = images.Register("counter", "status", ImageResourceFormat.Png,
                Path.Combine(AppContext.BaseDirectory, "Assets", "template-status.png"));
            if (!registration.Accepted) AddError(new(registration.Code, registration.Message));
        }
        communicationStartup = StartCounterCoreAsync(brokerPath, counterPath, templates, dynamic, timers, presets, flyouts, organization);
        return communicationStartup;
    }
    private async Task StartCounterCoreAsync(string brokerPath, string counterPath, bool templates, bool dynamic, bool timers, bool presets, bool flyouts, bool organization)
    {
        HostBrokerSession? started = null;
        try
        {
            started = await HostBrokerSession.StartAsync(brokerPath, ["counter"], communicationLifetime.Token).ConfigureAwait(false);
            communication = started;
            started.QueueFlyoutPresentation = message =>
            {
                var queued = flyoutRequests.Enqueue(message);
                if (queued.Accepted) RequestRefresh();
                return queued;
            };
            await started.StartServiceAsync("counter", counterPath, communicationLifetime.Token,
                dynamic ? ["--dynamic"] : templates ? ["--templates"] : timers ? ["--timers"] : presets ? ["--presets"] : flyouts ? ["--flyouts"] : organization ? ["--organization"] : null).ConfigureAwait(false);
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
        flyoutRequests.Close();
        display.Dispose();
        var testStopped = true;
        if (flyouts is not null)
        {
            var result = flyouts.TryClose();
            if (result.IsSuccess) flyouts = null;
            else { testStopped = false; AddError(result.Error!); }
        }
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
        if (closed && flyouts is null) images.Dispose();
        if (!closed && Session.Error is not null) AddError(Session.Error);
        return closed && testStopped;
    }
    private void AddError(StructuredError error)
    {
        var text = $"{error.Code}: {error.Message}";
        if (text.Length > 2048) text = text[..2048];
        if (!errors.Contains(text))
        {
            if (errors.Count >= 64) errors.RemoveAt(0);
            errors.Add(text); Tests?.Observe("host-error", text);
        }
    }
    private void Notify() => Changed?.Invoke();
}

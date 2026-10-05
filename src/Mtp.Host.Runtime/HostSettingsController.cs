using System.Text.Json;
using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>Owns Host settings state. The changed callback must dispatch safely from any thread.</summary>
public sealed class HostSettingsController : IDisposable
{
    private readonly object gate = new();
    private readonly HostDisplayController display;
    private readonly IHostSettingsPreferenceStore store;
    private readonly Func<IReadOnlyList<BrokerApplicationSnapshot>> snapshots;
    private readonly Func<string?, CancellationToken, Task<ProtocolResult>> retry;
    private readonly Func<string> materialStatus;
    private readonly Action<HostAppearancePreferences> applyAppearance;
    private readonly Action changed;
    private readonly SettingsNavigation navigation = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, TaskCompletionSource<ProtocolResult>> recoveries = new(StringComparer.Ordinal);
    private HostSettingsPreferences preferences = new(new(), Array.Empty<string>());
    private StructuredError? error;
    private bool disposed;

    public HostSettingsController(HostDisplayController display, IHostSettingsPreferenceStore store,
        Func<IReadOnlyList<BrokerApplicationSnapshot>> snapshots, Func<string?, CancellationToken, Task<ProtocolResult>> retry,
        Func<string> materialStatus, Action<HostAppearancePreferences> applyAppearance, Action changed)
    {
        this.display = display ?? throw new ArgumentNullException(nameof(display));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        this.retry = retry ?? throw new ArgumentNullException(nameof(retry));
        this.materialStatus = materialStatus ?? throw new ArgumentNullException(nameof(materialStatus));
        this.applyAppearance = applyAppearance ?? throw new ArgumentNullException(nameof(applyAppearance));
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
        var loaded = store.Load();
        error = loaded.Error;
        if (loaded.IsSuccess)
        {
            preferences = loaded.Value!;
            try { applyAppearance(preferences.Appearance); }
            catch (Exception) { error = new("settings_apply_failed", "设置已读取，当前窗口外观未能更新。"); }
        }
    }

    public HostSettingsSnapshot GetSnapshot()
    {
        lock (gate)
        {
            if (disposed) return new(navigation.Snapshot, preferences, [], [], "设置已关闭。", new("settings_closed", "设置已关闭。"));
            var applications = Array.AsReadOnly(snapshots().ToArray());
            return new(navigation.Snapshot, preferences, OrderedComponents(),
                applications, materialStatus(), error)
                { Groupings = IslandGroupings(), HintEntries = HintEntries(applications), EventEntries = EventEntries(applications) };
        }
    }

    public HostSettingsSnapshot Navigate(SettingsPage page)
    {
        lock (gate)
        {
            if (!disposed) { navigation.Navigate(page); Notify(); }
            return GetSnapshot();
        }
    }

    public HostSettingsSnapshot Back()
    {
        lock (gate)
        {
            if (!disposed) { navigation.Back(); Notify(); }
            return GetSnapshot();
        }
    }

    public static string IdentityKey(StableIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var segments = identity.Segments.Select(value => value.Value).ToArray();
        if (segments.Length != 3 || segments.Any(value => value.Length > 256))
            throw new ArgumentException("Component identity must have three bounded segments.", nameof(identity));
        return JsonSerializer.Serialize(segments);
    }

    public CoreResult<HostSettingsSnapshot> SetVisibility(StableIdentity identity, bool visible)
    {
        lock (gate)
        {
            if (disposed) return Fail("settings_closed", "设置已关闭。");
            if (identity is null) return Fail("component_not_declared", "组件身份无效。");
            var result = display.SetVisibility(identity, visible);
            error = result.Error;
            Notify();
            return result.IsSuccess ? CoreResult<HostSettingsSnapshot>.Success(GetSnapshot()) : CoreResult<HostSettingsSnapshot>.Failure(error!);
        }
    }

    public CoreResult<HostSettingsSnapshot> MoveComponent(StableIdentity identity, int direction)
    {
        lock (gate)
        {
            if (disposed) return Fail("settings_closed", "设置已关闭。");
            if (direction is not (-1 or 1)) return Fail("settings_direction_invalid", "顺序调整仅支持上移或下移。");
            var rows = OrderedComponents();
            int index = rows.ToList().FindIndex(value => value.Identity == identity);
            if (index < 0) return Fail("component_not_declared", "当前没有该声明组件。");
            int neighbor = index + direction;
            if (neighbor < 0 || neighbor >= rows.Count) return CoreResult<HostSettingsSnapshot>.Success(GetSnapshot());
            var order = preferences.ComponentOrder.ToList();
            var existing = order.ToHashSet(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                string key = IdentityKey(row.Identity);
                if (existing.Add(key)) order.Add(key);
            }
            if (order.Count > LocalHostSettingsPreferenceStore.MaximumIdentities)
                return Fail("settings_order_full", "稳定身份顺序已达 4096 项，保留原值。");
            int from = order.IndexOf(IdentityKey(rows[index].Identity)), to = order.IndexOf(IdentityKey(rows[neighbor].Identity));
            (order[from], order[to]) = (order[to], order[from]);
            var committed = store.CommitOrder(order);
            error = committed.Error;
            if (committed.IsSuccess) preferences = committed.Value!;
            Notify();
            return committed.IsSuccess ? CoreResult<HostSettingsSnapshot>.Success(GetSnapshot()) : CoreResult<HostSettingsSnapshot>.Failure(error!);
        }
    }

    private IReadOnlyList<HostComponentDisplayModel> OrderedComponents()
    {
        var rank = preferences.ComponentOrder.Select((key, index) => (key, index)).ToDictionary(value => value.key, value => value.index, StringComparer.Ordinal);
        var islands = IslandGroupings().Select(value => value.Identity).ToHashSet();
        return Array.AsReadOnly(display.CurrentComponents.OrderBy(value => rank.GetValueOrDefault(IdentityKey(value.Identity), int.MaxValue))
            .ThenBy(value => islands.Contains(value.Identity) ? 0 : 1).ToArray());
    }

    private IReadOnlyList<HostIslandGrouping> IslandGroupings() => Array.AsReadOnly(snapshots()
        .Where(value => value.Declaration is not null).SelectMany(value => value.Declaration!.DynamicContents)
        .Where(value => value.Declaration.Kind == DynamicContentKind.LiveIsland)
        .Select(value => new HostIslandGrouping(value.ComponentIdentity, value.Declaration.Grouping,
            HostIslandOrganization.Effective(value.Declaration.Grouping,
                preferences.IslandGrouping?.GetValueOrDefault(IdentityKey(value.ComponentIdentity)))))
        .ToArray());

    public CoreResult<HostSettingsSnapshot> SetGrouping(StableIdentity identity, DynamicGrouping grouping)
    {
        lock (gate)
        {
            if (disposed) return Fail("settings_closed", "设置已关闭。");
            var current = IslandGroupings().FirstOrDefault(value => value.Identity == identity);
            if (current is null) return Fail("island_not_declared", "当前没有该实况岛入口。");
            if (grouping is not (DynamicGrouping.Together or DynamicGrouping.Separate) ||
                current.Capability != DynamicGrouping.UserChoice && grouping != current.Capability)
                return Fail("grouping_not_supported", "提供方不支持该组织方式。");
            var committed = store.CommitGrouping(identity, grouping);
            error = committed.Error;
            if (committed.IsSuccess) preferences = committed.Value!;
            Notify();
            return committed.IsSuccess ? CoreResult<HostSettingsSnapshot>.Success(GetSnapshot()) : CoreResult<HostSettingsSnapshot>.Failure(error!);
        }
    }

    public CoreResult<HostSettingsSnapshot> SetAppearance(HostAppearancePreferences appearance)
    {
        lock (gate)
        {
            if (disposed) return Fail("settings_closed", "设置已关闭。");
            var valid = LocalHostSettingsPreferenceStore.Validate(preferences with { Appearance = appearance });
            if (!valid.IsSuccess) return Fail(valid.Error!.Code, valid.Error.Message);
            var committed = store.CommitAppearance(appearance);
            error = committed.Error;
            if (committed.IsSuccess)
            {
                preferences = committed.Value!;
                try { applyAppearance(preferences.Appearance); }
                catch (Exception) { error = new("settings_apply_failed", "设置已保存，当前窗口外观未能更新。"); }
            }
            Notify();
            return committed.IsSuccess ? CoreResult<HostSettingsSnapshot>.Success(GetSnapshot()) : CoreResult<HostSettingsSnapshot>.Failure(error!);
        }
    }

    private IReadOnlyList<HostHintEntry> HintEntries(IReadOnlyList<BrokerApplicationSnapshot> applications) =>
        Array.AsReadOnly(applications.Where(value => value.Declaration is not null)
            .SelectMany(application => application.Declaration!.FlyoutEntries
                .Where(entry => entry.Kind is FlyoutKind.ShortHint or FlyoutKind.InteractiveHint)
                .Select(entry => new HostHintEntry(entry.Identity, entry.Kind,
                    preferences.HintVisibility?.GetValueOrDefault(IdentityKey(entry.Identity), true) ?? true,
                    application.IsConnected && application.IsInteractive)))
            .ToArray());

    public CoreResult<HostSettingsSnapshot> SetHints(HostHintPreferences hints)
    {
        lock (gate)
        {
            if (disposed) return Fail("settings_closed", "设置已关闭。");
            if (hints is null) return Fail("settings_invalid", "短提示位置设置无效，保留原值。");
            var valid = LocalHostSettingsPreferenceStore.Validate(preferences with { Hints = hints });
            if (!valid.IsSuccess) return Fail(valid.Error!.Code, valid.Error.Message);
            var committed = store.CommitHints(hints);
            error = committed.Error;
            if (committed.IsSuccess) preferences = committed.Value!;
            Notify();
            return committed.IsSuccess ? CoreResult<HostSettingsSnapshot>.Success(GetSnapshot()) : CoreResult<HostSettingsSnapshot>.Failure(error!);
        }
    }

    public CoreResult<HostSettingsSnapshot> SetHintVisibility(StableIdentity identity, bool visible)
    {
        lock (gate)
        {
            if (disposed) return Fail("settings_closed", "设置已关闭。");
            if (identity is null || !HintEntries(snapshots()).Any(entry => entry.Identity == identity))
                return Fail("hint_not_declared", "当前没有该短提示入口。");
            var committed = store.CommitHintVisibility(identity, visible);
            error = committed.Error;
            if (committed.IsSuccess) preferences = committed.Value!;
            Notify();
            return committed.IsSuccess ? CoreResult<HostSettingsSnapshot>.Success(GetSnapshot()) : CoreResult<HostSettingsSnapshot>.Failure(error!);
        }
    }

    private CoreResult<HostSettingsSnapshot> Fail(string code, string message)
    {
        error = new(code, message);
        if (!disposed) Notify();
        return CoreResult<HostSettingsSnapshot>.Failure(error);
    }

    private IReadOnlyList<HostEventEntry> EventEntries(IReadOnlyList<BrokerApplicationSnapshot> applications) =>
        Array.AsReadOnly(applications.Where(value => value.Declaration is not null)
            .SelectMany(application => application.Declaration!.FlyoutEntries
                .Where(entry => entry.Kind == FlyoutKind.EventGroup && entry.ClosePolicy is not null)
                .Select(entry => new HostEventEntry(entry.Identity, entry.ClosePolicy!.Value,
                    preferences.EventVisibility?.GetValueOrDefault(IdentityKey(entry.Identity), true) ?? true,
                    application.IsConnected && application.IsInteractive)))
            .ToArray());

    public CoreResult<HostSettingsSnapshot> SetEvents(HostEventPreferences events)
    {
        lock (gate)
        {
            if (disposed) return Fail("settings_closed", "设置已关闭。");
            if (events is null) return Fail("settings_invalid", "事件浮窗设置无效，保留原值。");
            var valid = LocalHostSettingsPreferenceStore.Validate(preferences with { Events = events });
            if (!valid.IsSuccess) return Fail(valid.Error!.Code, valid.Error.Message);
            var committed = store.CommitEvents(events);
            error = committed.Error;
            if (committed.IsSuccess) preferences = committed.Value!;
            Notify();
            return committed.IsSuccess ? CoreResult<HostSettingsSnapshot>.Success(GetSnapshot()) : CoreResult<HostSettingsSnapshot>.Failure(error!);
        }
    }
    public CoreResult<HostSettingsSnapshot> SetEventVisibility(StableIdentity identity, bool visible)
    {
        lock (gate)
        {
            if (disposed) return Fail("settings_closed", "设置已关闭。");
            if (identity is null || !EventEntries(snapshots()).Any(entry => entry.Identity == identity))
                return Fail("event_not_declared", "当前没有该事件浮窗入口。");
            var committed = store.CommitEventVisibility(identity, visible);
            error = committed.Error;
            if (committed.IsSuccess) preferences = committed.Value!;
            Notify();
            return committed.IsSuccess ? CoreResult<HostSettingsSnapshot>.Success(GetSnapshot()) : CoreResult<HostSettingsSnapshot>.Failure(error!);
        }
    }

    private void Notify()
    {
        if (disposed) return;
        try { changed(); }
        catch (Exception) { error ??= new("settings_notify_failed", "设置状态已更新，界面刷新暂时不可用。"); }
    }

    public Task<ProtocolResult> RetryAsync(string? applicationId, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<ProtocolResult> completion;
        CancellationTokenSource cancellation;
        string key = applicationId ?? "";
        lock (gate)
        {
            if (disposed) return Task.FromResult(ProtocolResult.Reject("SettingsClosed", "设置已关闭。"));
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(ProtocolResult.Reject("RecoveryCancelled", "恢复请求已取消。"));
            if (applicationId is not null && !snapshots().Any(value => value.ApplicationId == applicationId))
                return Task.FromResult(ProtocolResult.Reject("UnknownApplication", "当前没有该接入应用。"));
            if (recoveries.ContainsKey(key)) return Task.FromResult(ProtocolResult.Reject("RecoveryBusy", "恢复请求正在执行。"));
            if (recoveries.Count >= ProtocolLimits.MaximumApplications + 1)
                return Task.FromResult(ProtocolResult.Reject("RecoveryBusy", "恢复请求已达预算。"));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            recoveries.Add(key, completion);
        }
        _ = RunRecoveryAsync(applicationId, key, completion, cancellation);
        return completion.Task;
    }

    private async Task RunRecoveryAsync(string? applicationId, string key, TaskCompletionSource<ProtocolResult> completion,
        CancellationTokenSource cancellation)
    {
        ProtocolResult result;
        using (cancellation)
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                result = await retry(applicationId, cancellation.Token).WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { result = ProtocolResult.Reject("RecoveryCancelled", "恢复请求已取消。"); }
            catch (Exception) { result = ProtocolResult.Reject("RecoveryFailed", "恢复通道未能完成请求。"); }
        }
        lock (gate)
        {
            if (disposed) { completion.TrySetResult(ProtocolResult.Reject("SettingsClosed", "设置已关闭。")); return; }
            recoveries.Remove(key);
            result ??= ProtocolResult.Reject("RecoveryFailed", "恢复通道未返回结果。");
            error = result.Accepted ? null : new(result.Code, result.Message);
            completion.TrySetResult(result);
            Notify();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var completion in recoveries.Values) completion.TrySetResult(ProtocolResult.Reject("SettingsClosed", "设置已关闭。"));
            recoveries.Clear();
        }
        lifetime.Cancel(); lifetime.Dispose();
    }
}

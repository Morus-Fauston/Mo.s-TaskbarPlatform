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
            return new(navigation.Snapshot, preferences, OrderedComponents(),
                Array.AsReadOnly(snapshots().ToArray()), materialStatus(), error);
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
        return Array.AsReadOnly(display.CurrentComponents.OrderBy(value => rank.GetValueOrDefault(IdentityKey(value.Identity), int.MaxValue)).ToArray());
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

    private CoreResult<HostSettingsSnapshot> Fail(string code, string message)
    {
        error = new(code, message);
        if (!disposed) Notify();
        return CoreResult<HostSettingsSnapshot>.Failure(error);
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

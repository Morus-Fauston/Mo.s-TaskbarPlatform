using Mtp.Contracts;

namespace Mtp.Host;

public sealed record BrokerApplicationSnapshot
{
    internal BrokerApplicationSnapshot(string applicationId, string sessionId,
        ValidatedApplicationDeclaration? declaration, ApplicationState? state,
        bool isConnected, bool isInteractive, ProtocolResult? lastError)
    {
        ApplicationId = applicationId;
        SessionId = sessionId;
        Declaration = declaration;
        State = state;
        IsConnected = isConnected;
        IsInteractive = isInteractive;
        LastError = lastError;
    }

    public string ApplicationId { get; internal init; }
    public string SessionId { get; internal init; }
    public ValidatedApplicationDeclaration? Declaration { get; internal init; }
    public ApplicationState? State { get; internal init; }
    public bool IsConnected { get; internal init; }
    public bool IsInteractive { get; internal init; }
    public ProtocolResult? LastError { get; internal init; }
    public IReadOnlyDictionary<DynamicItemIdentity, long> ItemOccurrences { get; internal init; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<DynamicItemIdentity, long>(new Dictionary<DynamicItemIdentity, long>());
}

/// <summary>Owns the latest fully validated readings received from the authenticated Broker.</summary>
public sealed class BrokerStateStore
{
    private readonly object sync = new();
    private readonly HashSet<string> registered;
    private readonly Dictionary<string, BrokerApplicationSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly HashSet<string> declaredApplications = new(StringComparer.Ordinal);
    private readonly HashSet<string> validDeclarations = new(StringComparer.Ordinal);
    private readonly HashSet<string> awaitingReady = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DisplayPermissionSnapshot> permissions = new(StringComparer.Ordinal);
    private readonly DeclarationValidator validator = new();
    private readonly TimeProvider clock;
    private long itemOccurrence;

    public BrokerStateStore(IReadOnlyCollection<string> registeredApplicationIds, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registeredApplicationIds);
        if (registeredApplicationIds.Count > ProtocolLimits.MaximumApplications ||
            registeredApplicationIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > DeclarationValidator.MaximumIdLength))
            throw new ArgumentException("Invalid application registration budget or identity.", nameof(registeredApplicationIds));
        registered = new HashSet<string>(registeredApplicationIds, StringComparer.Ordinal);
        clock = timeProvider ?? TimeProvider.System;
        FlyoutRequests = new FlyoutRequestRouter(clock);
    }

    public FlyoutRequestRouter FlyoutRequests { get; }

    public IReadOnlyList<BrokerApplicationSnapshot> Snapshots
    {
        get { lock (sync) { TickActivitiesCore(); return Array.AsReadOnly(snapshots.Values.ToArray()); } }
    }

    public BrokerApplicationSnapshot? GetSnapshot(string applicationId)
    {
        lock (sync) { TickActivitiesCore(); return snapshots.GetValueOrDefault(applicationId); }
    }

    public DisplayPermissionSnapshot? GetDisplayPermissions(string applicationId)
    {
        lock (sync) return validDeclarations.Contains(applicationId) && snapshots.GetValueOrDefault(applicationId)?.IsConnected == true
            ? permissions.GetValueOrDefault(applicationId) : null;
    }

    public ProtocolResult SetEntryDisplayAllowed(string applicationId, string featureGroupId, string componentId, bool allowed)
    {
        lock (sync)
        {
            if (!validDeclarations.Contains(applicationId) || !permissions.TryGetValue(applicationId, out var current))
                return ProtocolResult.Reject("DeclarationRequired", "当前会话声明尚未确认");
            var entry = current.Entries.FirstOrDefault(value => value.FeatureGroupId == featureGroupId && value.ComponentId == componentId);
            if (entry is null) return ProtocolResult.Reject("UnknownEntry", "入口不是当前已声明实况岛");
            if (entry.Allowed == allowed) return ProtocolResult.Success("PermissionUnchanged");
            if (current.Revision == long.MaxValue) return ProtocolResult.Reject("PermissionRevisionExhausted", "显示许可序号已耗尽");
            permissions[applicationId] = new(current.Revision + 1, Array.AsReadOnly(current.Entries.Select(value =>
                value == entry ? value with { Allowed = allowed } : value).ToArray()));
            return ProtocolResult.Success();
        }
    }

    public void TickActivities() { lock (sync) TickActivitiesCore(); }

    private void TickActivitiesCore()
    {
        var now = clock.GetUtcNow();
        foreach (var pair in snapshots.ToArray())
            if (pair.Value.Declaration is { } declaration && pair.Value.State is { } state)
            {
                var pruned = ActivityLifecycle.Prune(declaration, state, now);
                if (!ReferenceEquals(pruned, state)) snapshots[pair.Key] = WithOccurrences(pair.Value, pair.Value with { State = pruned });
            }
    }

    // Only current items have entries. A removed item leaves no tombstone; reappearance receives a fresh value.
    private BrokerApplicationSnapshot WithOccurrences(BrokerApplicationSnapshot? previous, BrokerApplicationSnapshot next)
    {
        var occurrences = new Dictionary<DynamicItemIdentity, long>();
        foreach (var entry in next.State?.DynamicEntries ?? [])
        {
            var declaration = next.Declaration!.DynamicContents.First(value =>
                value.ComponentIdentity.Segments[1].Value == entry.FeatureGroupId && value.ComponentIdentity.LocalId.Value == entry.ComponentId).Declaration;
            var oldDeclaration = previous?.Declaration?.DynamicContents.FirstOrDefault(value =>
                value.ComponentIdentity.Segments[1].Value == entry.FeatureGroupId && value.ComponentIdentity.LocalId.Value == entry.ComponentId)?.Declaration;
            var oldItems = previous?.State?.DynamicEntries?.FirstOrDefault(value => value.FeatureGroupId == entry.FeatureGroupId && value.ComponentId == entry.ComponentId)?.Content.Items;
            foreach (var item in entry.Content.Items)
            {
                var key = new DynamicItemIdentity(next.ApplicationId, entry.FeatureGroupId, entry.ComponentId, item.ItemId);
                var structure = declaration.Structures.First(value => value.StructureId == item.StructureId);
                var oldItem = oldItems?.FirstOrDefault(value => value.ItemId == item.ItemId);
                var oldStructure = oldItem is null ? null : oldDeclaration?.Structures.FirstOrDefault(value => value.StructureId == oldItem.StructureId);
                bool retained = previous is not null && previous.ItemOccurrences.TryGetValue(key, out _) &&
                    oldDeclaration?.Kind == declaration.Kind && oldStructure is not null && ItemStructureEquality.Same(oldStructure, structure);
                occurrences.Add(key, retained ? previous!.ItemOccurrences[key] : checked(++itemOccurrence));
            }
        }
        return next with { ItemOccurrences = new System.Collections.ObjectModel.ReadOnlyDictionary<DynamicItemIdentity, long>(occurrences) };
    }

    private DisplayPermissionSnapshot SynchronizePermissions(string applicationId, ValidatedApplicationDeclaration declaration)
    {
        var previous = permissions.GetValueOrDefault(applicationId);
        var entries = ActivityLifecycle.IslandKeys(declaration).Select(key => new EntryDisplayPermission(key.Group, key.Component,
            previous?.Entries.Any(value => value.FeatureGroupId == key.Group && value.ComponentId == key.Component && value.Allowed) == true)).ToArray();
        return permissions[applicationId] = new(previous?.Revision ?? 1, Array.AsReadOnly(entries));
    }

    public ProtocolResult RequireSessionReady(string applicationId)
    {
        lock (sync)
        {
            if (!registered.Contains(applicationId)) return ProtocolResult.Reject("UnknownApplication", "应用未登记");
            awaitingReady.Add(applicationId);
            validDeclarations.Remove(applicationId);
            if (snapshots.TryGetValue(applicationId, out var previous))
                snapshots[applicationId] = previous with { IsInteractive = false };
            return ProtocolResult.Success();
        }
    }

    /// <summary>A matched action may finish after a newer ordinary update; it never rolls readings back.</summary>
    public ProtocolResult ApplyActionState(string applicationId, string sessionId, ApplicationState state)
    {
        lock (sync)
        {
            TickActivitiesCore();
            var previous = snapshots.GetValueOrDefault(applicationId);
            if (previous is null || previous.SessionId != sessionId || !previous.IsInteractive || !previous.IsConnected || previous.Declaration is null)
                return ProtocolResult.Reject("ActionNotAvailable", "动作会话或声明已失效");
            var error = ValidateState(state, previous.Declaration, out var frozen);
            if (error is not null) return error;
            if (state.Revision <= previous.State!.Revision) return ProtocolResult.Success("StateAlreadyCurrent");
            var admitted = ActivityLifecycle.Admit(applicationId, previous.Declaration, frozen!, previous.State, permissions[applicationId], clock.GetUtcNow());
            snapshots[applicationId] = WithOccurrences(previous, previous with { State = admitted.State, LastError = null });
            return admitted.Result;
        }
    }

    public ProtocolMessage Handle(ProtocolMessage message)
    {
        lock (sync)
        {
            if (message is null)
                return new ProtocolMessage
                {
                    Kind = MessageKind.Result,
                    Result = ProtocolResult.Reject("InvalidEnvelope", "消息缺失")
                };
            TickActivitiesCore();
            var result = HandleCore(message);
            return new ProtocolMessage
            {
                Kind = MessageKind.Result,
                ApplicationId = message.ApplicationId,
                SessionId = message.SessionId,
                RequestId = message.RequestId,
                Result = result
            };
        }
    }

    private ProtocolResult HandleCore(ProtocolMessage message)
    {
        if (message.Version != ProtocolLimits.Version)
            return ProtocolResult.Reject("UnsupportedVersion", "协议版本不受支持");
        if (message.Result?.ActivityRejections is not null)
            return ProtocolResult.Reject("InvalidEnvelope", "准入拒绝明细只能由Host生成");
        if (!ValidEnvelopeId(message.ApplicationId) || !ValidEnvelopeId(message.SessionId) ||
            message.RequestId is null || message.RequestId.Length > DeclarationValidator.MaximumIdLength)
            return ProtocolResult.Reject("InvalidEnvelope", "消息标识缺失或超出预算");
        if (!registered.Contains(message.ApplicationId))
            return ProtocolResult.Reject("UnknownApplication", "应用未登记");
        snapshots.TryGetValue(message.ApplicationId, out var previous);
        if (message.Kind == MessageKind.Welcome)
        {
            if (previous?.SessionId == message.SessionId)
                return ProtocolResult.Reject("DuplicateSession", "会话已登记");
            declaredApplications.Remove(message.ApplicationId);
            validDeclarations.Remove(message.ApplicationId);
            if (permissions.TryGetValue(message.ApplicationId, out var previousPermissions))
                permissions[message.ApplicationId] = previousPermissions with { Revision = 1 };
            snapshots[message.ApplicationId] = new(message.ApplicationId, message.SessionId,
                previous?.Declaration, previous?.State, true, false, null)
            { ItemOccurrences = previous?.ItemOccurrences ?? new System.Collections.ObjectModel.ReadOnlyDictionary<DynamicItemIdentity, long>(new Dictionary<DynamicItemIdentity, long>()) };
            return ProtocolResult.Success();
        }
        if (previous is null || previous.SessionId != message.SessionId)
            return ProtocolResult.Reject("StaleSession", "会话已失效");
        if (message.Kind == MessageKind.Disconnected)
        {
            if (!previous.IsConnected) return ProtocolResult.Success("AlreadyDisconnected");
            var failure = message.Result is { Accepted: false } reason &&
                reason.Code is "ProcessExited" or "PipeDisconnected" or "HeartbeatTimedOut" &&
                reason.Message is { Length: <= ProtocolLimits.MaximumTextLength } &&
                (reason.Path is null || reason.Path.Length <= ProtocolLimits.MaximumTextLength)
                ? reason : ProtocolResult.Reject("Disconnected", "应用连接已中断");
            snapshots[message.ApplicationId] = previous with
            {
                IsConnected = false,
                IsInteractive = false,
                LastError = failure
            };
            return ProtocolResult.Success();
        }
        if (!previous.IsConnected) return ProtocolResult.Reject("Disconnected", "应用连接已中断");
        if (message.Kind == MessageKind.SessionReady)
        {
            if (message.Declaration is not null || message.State is not null || message.Result is not null ||
                message.Action is not null || message.ActionCompletion is not null || message.Heartbeat is not null ||
                message.Flyout is not null || message.Registration is not null || message.Permissions is not null || message.BrokerLoad is not null ||
                message.Ticket != "" || message.StartRequestId != "")
                return ProtocolResult.Reject("InvalidEnvelope", "就绪通知包含不允许的载荷");
            if (!validDeclarations.Contains(message.ApplicationId))
                return ProtocolResult.Reject("DeclarationRequired", "当前会话完整声明尚未确认");
            awaitingReady.Remove(message.ApplicationId);
            snapshots[message.ApplicationId] = previous with { IsInteractive = true, LastError = null };
            return ProtocolResult.Success();
        }
        if (message.Kind == MessageKind.FlyoutRequest)
            return FlyoutRequests.Handle(message.ApplicationId, message.SessionId, message.Flyout,
                previous, previous.Declaration?.FlyoutEntries ?? []);
        if (message.Kind == MessageKind.State)
        {
            if (!validDeclarations.Contains(message.ApplicationId) || previous.Declaration is null)
                return ProtocolResult.Reject("DeclarationRequired", "完整声明尚未确认");
            var stateError = ValidateState(message.State, previous.Declaration, out var frozen);
            if (stateError is not null) return stateError;
            if (message.State!.Revision <= previous.State!.Revision)
                return ProtocolResult.Reject("StaleRevision", "状态序号必须递增");
            var admitted = ActivityLifecycle.Admit(message.ApplicationId, previous.Declaration, frozen!, previous.State, permissions[message.ApplicationId], clock.GetUtcNow());
            snapshots[message.ApplicationId] = WithOccurrences(previous, previous with { State = admitted.State, LastError = null });
            return admitted.Result;
        }
        if (message.Kind == MessageKind.Declare)
        {
            if (declaredApplications.Contains(message.ApplicationId))
                return RejectDeclaration(previous, ProtocolResult.Reject("DeclarationAlreadyAccepted", "同一会话不能替换已确认结构"));
            if (message.Declaration?.ApplicationId != message.ApplicationId)
                return RejectDeclaration(previous, ProtocolResult.Reject("IdentityMismatch", "声明身份与会话不符"));
            var declaration = validator.Validate(message.Declaration);
            if (!declaration.IsSuccess)
                return RejectDeclaration(previous, ProtocolResult.Reject(declaration.Error!.Code, declaration.Error.Message, declaration.Error.Path));
            var stateError = ValidateState(message.State, declaration.Value!, out var frozen);
            if (stateError is not null) return RejectDeclaration(previous, stateError);
            var currentPermissions = SynchronizePermissions(message.ApplicationId, declaration.Value!);
            var admitted = ActivityLifecycle.Admit(message.ApplicationId, declaration.Value!, frozen!, previous.State, currentPermissions, clock.GetUtcNow());
            declaredApplications.Add(message.ApplicationId);
            validDeclarations.Add(message.ApplicationId);
            snapshots[message.ApplicationId] = WithOccurrences(previous, previous with
            {
                Declaration = declaration.Value,
                State = admitted.State,
                IsInteractive = !awaitingReady.Contains(message.ApplicationId),
                LastError = null
            });
            FlyoutRequests.SynchronizeDeclaration(snapshots[message.ApplicationId], declaration.Value!.FlyoutEntries);
            return admitted.Result;
        }
        return ProtocolResult.Reject("UnsupportedMessage", "消息类型不受支持");
    }

    private ProtocolResult RejectDeclaration(BrokerApplicationSnapshot previous, ProtocolResult error)
    {
        validDeclarations.Remove(previous.ApplicationId);
        snapshots[previous.ApplicationId] = previous with { IsInteractive = false, LastError = error };
        return error;
    }

    private static bool ValidEnvelopeId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= DeclarationValidator.MaximumIdLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private ProtocolResult? ValidateState(ApplicationState? state, ValidatedApplicationDeclaration declaration,
        out ApplicationState? frozen)
    {
        frozen = null;
        if (state is null || state.Revision < 0 || state.Components is null ||
            state.Components.Count > ProtocolLimits.MaximumStateEntries)
            return ProtocolResult.Reject("InvalidState", "状态缺失或超出预算", "state");
        var permitted = declaration.FeatureGroups.SelectMany(group => group.Components.Select(component =>
            (group.Identity.Segments[1].Value, component.Identity.Segments[2].Value))).ToHashSet();
        var seen = new HashSet<(string, string)>();
        foreach (var reading in state.Components)
        {
            if (reading is null || !permitted.Contains((reading.FeatureGroupId, reading.ComponentId)) ||
                !seen.Add((reading.FeatureGroupId, reading.ComponentId)) || reading.Text is null ||
                reading.Text.Length > ProtocolLimits.MaximumTextLength ||
                (reading.Number is double number && !double.IsFinite(number)))
                return ProtocolResult.Reject("InvalidState", "状态含无效或重复入口及读数", "state.components");
        }
        var dynamicEntries = state.DynamicEntries ?? [];
        if (dynamicEntries.Count > ProtocolLimits.MaximumStateEntries)
            return ProtocolResult.Reject("InvalidState", "动态入口超出预算", "state.dynamicEntries");
        var dynamicSeen = new HashSet<(string, string)>();
        var frozenEntries = new List<DynamicEntryState>();
        var dynamicValidator = new DynamicContentValidator();
        int activityCount = 0, itemCount = 0;
        var now = clock.GetUtcNow();
        foreach (var entry in dynamicEntries)
        {
            if (entry is null || !dynamicSeen.Add((entry.FeatureGroupId, entry.ComponentId)))
                return ProtocolResult.Reject("InvalidState", "动态入口缺失或重复", "state.dynamicEntries");
            var structure = declaration.DynamicContents.FirstOrDefault(value =>
                value.ComponentIdentity.Segments[1].Value == entry.FeatureGroupId &&
                value.ComponentIdentity.Segments[2].Value == entry.ComponentId);
            if (structure is null || entry.Content?.Activities is null || entry.Content.Items is null)
                return ProtocolResult.Reject("InvalidState", "动态入口未声明或集合缺失", "state.dynamicEntries");
            // Subtract from fixed limits before summing so adversarial counts cannot overflow.
            if (entry.Content.Activities.Count > DynamicContentLimits.MaximumActivitiesPerApplication - activityCount ||
                entry.Content.Items.Count > DynamicContentLimits.MaximumItemsPerApplication - itemCount)
                return ProtocolResult.Reject("dynamic_budget_exceeded", "应用动态集合超出预算", "state.dynamicEntries");
            activityCount += entry.Content.Activities.Count;
            itemCount += entry.Content.Items.Count;
            var checkedContent = dynamicValidator.ValidateState(structure, entry.Content, now);
            if (!checkedContent.IsSuccess)
                return ProtocolResult.Reject(checkedContent.Error!.Code, checkedContent.Error.Message, checkedContent.Error.Path);
            frozenEntries.Add(entry with { Content = checkedContent.Value!.Content });
        }
        var templateState = new TemplateValidator().ValidateState(declaration, state.TemplateEntries);
        if (!templateState.IsSuccess) return ProtocolResult.Reject(templateState.Error!.Code, templateState.Error.Message, templateState.Error.Path);
        frozen = new(state.Revision, Array.AsReadOnly(state.Components.ToArray()), Array.AsReadOnly(frozenEntries.ToArray()), templateState.Value!);
        return null;
    }
}

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
}

/// <summary>Owns the latest fully validated readings received from the authenticated Broker.</summary>
public sealed class BrokerStateStore
{
    private readonly object sync = new();
    private readonly HashSet<string> registered;
    private readonly Dictionary<string, BrokerApplicationSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly HashSet<string> declaredApplications = new(StringComparer.Ordinal);
    private readonly DeclarationValidator validator = new();
    private readonly TimeProvider clock;

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
        get { lock (sync) return Array.AsReadOnly(snapshots.Values.ToArray()); }
    }

    public BrokerApplicationSnapshot? GetSnapshot(string applicationId)
    {
        lock (sync) return snapshots.GetValueOrDefault(applicationId);
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
            snapshots[message.ApplicationId] = new(message.ApplicationId, message.SessionId,
                previous?.Declaration, previous?.State, true, false, null);
            return ProtocolResult.Success();
        }
        if (previous is null || previous.SessionId != message.SessionId)
            return ProtocolResult.Reject("StaleSession", "会话已失效");
        if (message.Kind == MessageKind.Disconnected)
        {
            snapshots[message.ApplicationId] = previous with
            {
                IsConnected = false,
                IsInteractive = false,
                LastError = ProtocolResult.Reject("Disconnected", "应用连接已中断")
            };
            return ProtocolResult.Success();
        }
        if (!previous.IsConnected) return ProtocolResult.Reject("Disconnected", "应用连接已中断");
        if (message.Kind == MessageKind.FlyoutRequest)
            return FlyoutRequests.Handle(message.ApplicationId, message.SessionId, message.Flyout,
                previous, previous.Declaration?.FlyoutEntries ?? []);
        if (message.Kind == MessageKind.State)
        {
            if (!previous.IsInteractive || previous.Declaration is null)
                return ProtocolResult.Reject("DeclarationRequired", "完整声明尚未确认");
            var stateError = ValidateState(message.State, previous.Declaration, out var frozen);
            if (stateError is not null) return stateError;
            if (message.State!.Revision <= previous.State!.Revision)
                return ProtocolResult.Reject("StaleRevision", "状态序号必须递增");
            snapshots[message.ApplicationId] = previous with { State = frozen, LastError = null };
            return ProtocolResult.Success();
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
            declaredApplications.Add(message.ApplicationId);
            snapshots[message.ApplicationId] = previous with
            {
                Declaration = declaration.Value,
                State = frozen,
                IsInteractive = true,
                LastError = null
            };
            FlyoutRequests.SynchronizeDeclaration(snapshots[message.ApplicationId], declaration.Value!.FlyoutEntries);
            return ProtocolResult.Success();
        }
        return ProtocolResult.Reject("UnsupportedMessage", "消息类型不受支持");
    }

    private ProtocolResult RejectDeclaration(BrokerApplicationSnapshot previous, ProtocolResult error)
    {
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
        frozen = new(state.Revision, Array.AsReadOnly(state.Components.ToArray()), Array.AsReadOnly(frozenEntries.ToArray()));
        return null;
    }
}

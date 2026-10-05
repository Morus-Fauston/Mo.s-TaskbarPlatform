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

    public BrokerStateStore(IReadOnlyCollection<string> registeredApplicationIds)
    {
        ArgumentNullException.ThrowIfNull(registeredApplicationIds);
        if (registeredApplicationIds.Count > ProtocolLimits.MaximumApplications ||
            registeredApplicationIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > DeclarationValidator.MaximumIdLength))
            throw new ArgumentException("Invalid application registration budget or identity.", nameof(registeredApplicationIds));
        registered = new HashSet<string>(registeredApplicationIds, StringComparer.Ordinal);
    }

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
        if (message.Kind == MessageKind.State)
        {
            if (!previous.IsInteractive || previous.Declaration is null)
                return ProtocolResult.Reject("DeclarationRequired", "完整声明尚未确认");
            var stateError = ValidateState(message.State, previous.Declaration);
            if (stateError is not null) return stateError;
            if (message.State!.Revision <= previous.State!.Revision)
                return ProtocolResult.Reject("StaleRevision", "状态序号必须递增");
            snapshots[message.ApplicationId] = previous with { State = Freeze(message.State), LastError = null };
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
            var stateError = ValidateState(message.State, declaration.Value!);
            if (stateError is not null) return RejectDeclaration(previous, stateError);
            declaredApplications.Add(message.ApplicationId);
            snapshots[message.ApplicationId] = previous with
            {
                Declaration = declaration.Value,
                State = Freeze(message.State!),
                IsInteractive = true,
                LastError = null
            };
            return ProtocolResult.Success();
        }
        return ProtocolResult.Reject("UnsupportedMessage", "消息类型不受支持");
    }

    private ProtocolResult RejectDeclaration(BrokerApplicationSnapshot previous, ProtocolResult error)
    {
        snapshots[previous.ApplicationId] = previous with { IsInteractive = false, LastError = error };
        return error;
    }

    private static ApplicationState Freeze(ApplicationState state) =>
        new(state.Revision, Array.AsReadOnly(state.Components.ToArray()));

    private static bool ValidEnvelopeId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= DeclarationValidator.MaximumIdLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static ProtocolResult? ValidateState(ApplicationState? state, ValidatedApplicationDeclaration declaration)
    {
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
        return null;
    }
}

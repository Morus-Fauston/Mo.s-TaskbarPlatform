using System;
using System.Collections.Generic;

namespace Mtp.Contracts;

public static class ProtocolLimits
{
    public const int Version = 1;
    public const int MaximumApplications = 16;
    public const int MaximumPendingRequests = 64;
    public const int MaximumStateEntries = 128;
    public const int MaximumTextLength = 1024;
    public const int TicketLifetimeSeconds = 30;
}

public enum MessageKind { Hello, Welcome, Declare, State, Result, Disconnected, FlyoutRequest, ActionRequest, ActionCompleted, Heartbeat }

/// <summary>Internal wire envelope. SDK applications use the provider API instead.</summary>
public sealed record ProtocolMessage
{
    public int Version { get; init; } = ProtocolLimits.Version;
    public MessageKind Kind { get; init; }
    public string ApplicationId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string StartRequestId { get; init; } = "";
    public string Ticket { get; init; } = "";
    public ApplicationDeclaration? Declaration { get; init; }
    public ApplicationState? State { get; init; }
    public ProtocolResult? Result { get; init; }
    public BrokerLoad? BrokerLoad { get; init; }
    public FlyoutRequest? Flyout { get; init; }
    public ActionInvocation? Action { get; init; }
    public ActionCompletion? ActionCompletion { get; init; }
    public HeartbeatPulse? Heartbeat { get; init; }
}

public sealed record HeartbeatPulse(long Sequence, DateTimeOffset SentAt);

/// <summary>Broker-owned bounded forwarding diagnostics; service submissions cannot supply this field.</summary>
public sealed record BrokerLoad(int PendingRequests, int PeakPendingRequests);

public sealed record ProtocolResult(bool Accepted, string Code, string Message, string? Path = null)
{
    public static ProtocolResult Success(string code = "Accepted") => new(true, code, "已接收");
    public static ProtocolResult Reject(string code, string message, string? path = null) => new(false, code, message, path);
}

public sealed record ComponentReading(string FeatureGroupId, string ComponentId, string Text, double? Number = null);
public sealed record ApplicationState(long Revision, IReadOnlyList<ComponentReading> Components,
    IReadOnlyList<DynamicEntryState>? DynamicEntries = null);
public sealed record ApplicationSnapshot(ApplicationDeclaration Declaration, ApplicationState State);

/// <summary>Host-created launch credentials, passed on inherited standard input, never command lines.</summary>
public sealed record ServiceLaunch(string PipeName, string ApplicationId, string StartRequestId, string Ticket);
public sealed record LaunchRegistration(string ApplicationId, string StartRequestId, string Ticket, DateTimeOffset ExpiresAt);
public sealed record BrokerLaunch(string HostPipeName, string HostTicket, string ServicePipeName, IReadOnlyList<LaunchRegistration> Registrations);

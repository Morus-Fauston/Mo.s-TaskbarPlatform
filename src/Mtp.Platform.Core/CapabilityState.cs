namespace Mtp.Platform.Core;

/// <summary>
/// An immutable capability status and its optional explanatory reason.
/// </summary>
public sealed record CapabilityState
{
    public CapabilityState(CapabilityStatus status, string? reason = null)
    {
        Status = status;
        Reason = reason;
    }

    public CapabilityStatus Status { get; }

    public string? Reason { get; }

    public static CapabilityState Available => new(CapabilityStatus.Available);

    public static CapabilityState Unavailable(string reason) =>
        new(CapabilityStatus.Unavailable, reason);

    public static CapabilityState Failed(string reason) =>
        new(CapabilityStatus.Failed, reason);
}

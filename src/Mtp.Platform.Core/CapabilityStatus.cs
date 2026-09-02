namespace Mtp.Platform.Core;

/// <summary>
/// Availability states shared by MTP capabilities.
/// </summary>
public enum CapabilityStatus
{
    Available,
    NotApplicable,
    Unsupported,
    Unavailable,
    Recovering,
    Failed,
}

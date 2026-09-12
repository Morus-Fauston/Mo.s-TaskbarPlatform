namespace Mtp.Platform.Core;

/// <summary>Legacy presentation state retained for diagnostics; 05D visibility follows the parent taskbar.</summary>
public enum TaskbarVisibility
{
    Allowed,
    FullScreen,
    TaskbarHidden,
    Unknown,
}

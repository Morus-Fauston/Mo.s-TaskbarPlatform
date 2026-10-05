using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host.Flyouts;

internal enum FlyoutInvocationKind { Automatic, Pointer, Keyboard }
internal sealed record FlyoutEntryKey(string ApplicationId, TemplateEntryReference Entry);
internal sealed record FlyoutOpenRequest(FlyoutEntryKey Entry, string SessionId, string ScreenId,
    PixelRect Anchor, PixelRect WorkArea, uint Dpi, FlyoutInvocationKind Invocation = FlyoutInvocationKind.Automatic,
    uint? TriggerMessageTime = null, nint TriggerWindow = 0, string? InitialTemplateId = null, Func<bool>? IsOriginAvailable = null);
internal enum FlyoutToggleResult { Opened, Closed, Replacing }
internal sealed record FlyoutWindowObservation(nint Handle, string TemplateId, PixelRect Bounds, bool Closing);
internal sealed record TaskbarFlyoutObservation(FlyoutEntryKey Entry, string SessionId, string ScreenId, long Generation,
    FlyoutGroupMode Mode, bool Closing, string? Error, IReadOnlyList<FlyoutWindowObservation> Windows);

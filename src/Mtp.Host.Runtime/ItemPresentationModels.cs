using Mtp.Contracts;

namespace Mtp.Host;

public static class ItemPresentationLimits
{
    public const int MaximumScreens = 16;
    public const int MaximumScreenIdLength = 256;
}

public sealed record DynamicItemIdentity(string ApplicationId, string FeatureGroupId, string ComponentId, string ItemId);
public sealed record ItemInteractionHandle(Guid Owner, string ScreenId, DynamicItemIdentity Item, long Generation);
public sealed record HostItemPresentation(ItemInteractionHandle Handle, string SessionId, DynamicContentKind Kind,
    ItemStructureDeclaration Structure, DynamicItemState Item, bool Expanded, ItemPresentation Presentation, bool IsInteractive,
    long PresenceGeneration = 0);
public sealed record ItemExpansionResult(ProtocolResult Result, IReadOnlyList<HostItemPresentation> Items);

public enum ItemActivationSource { BlankPrimary, PrimaryButton, SecondaryButton }
public sealed record HostItemActivationResult(ProtocolResult Result, bool Handled,
    ActionSlotReference? Action = null, string? TaskbarFlyoutId = null,
    IReadOnlyList<HostItemPresentation>? Items = null, string? SessionId = null, ItemInteractionHandle? Origin = null);

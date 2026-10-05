using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

public enum HostTheme { System, Light, Dark }
public enum SettingsPage { Applications, Layout, Flyouts, Official, About, Global }
public sealed record HostAppearancePreferences(HostTheme Theme = HostTheme.System, MaterialKind Material = MaterialKind.Acrylic, double Opacity = 0.8);
public sealed record HostHintPreferences(FlyoutPosition DefaultPosition = FlyoutPosition.LowerCenter, bool AllowApplicationPosition = false);
public sealed record HostSettingsPreferences(HostAppearancePreferences Appearance, IReadOnlyList<string> ComponentOrder,
    IReadOnlyDictionary<string, DynamicGrouping>? IslandGrouping = null, HostHintPreferences? Hints = null,
    IReadOnlyDictionary<string, bool>? HintVisibility = null);
public interface IHostSettingsPreferenceStore
{
    CoreResult<HostSettingsPreferences> Load();
    CoreResult<HostSettingsPreferences> CommitAppearance(HostAppearancePreferences appearance);
    CoreResult<HostSettingsPreferences> CommitOrder(IReadOnlyList<string> order);
    CoreResult<HostSettingsPreferences> CommitGrouping(StableIdentity identity, DynamicGrouping grouping);
    CoreResult<HostSettingsPreferences> CommitHints(HostHintPreferences hints);
    CoreResult<HostSettingsPreferences> CommitHintVisibility(StableIdentity identity, bool visible);
}
public sealed record HostHintEntry(StableIdentity Identity, FlyoutKind Kind, bool IsVisible, bool IsAvailable);
public sealed record SettingsNavigationSnapshot(SettingsPage Current, bool CanGoBack);
public sealed record HostSettingsSnapshot(SettingsNavigationSnapshot Navigation, HostSettingsPreferences Preferences,
    IReadOnlyList<HostComponentDisplayModel> Components, IReadOnlyList<BrokerApplicationSnapshot> Applications,
    string MaterialStatus, StructuredError? Error)
{
    public IReadOnlyList<HostIslandGrouping> Groupings { get; init; } = Array.Empty<HostIslandGrouping>();
    public IReadOnlyList<HostHintEntry> HintEntries { get; init; } = Array.Empty<HostHintEntry>();
}

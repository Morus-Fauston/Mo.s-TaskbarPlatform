using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

public enum HostTheme { System, Light, Dark }
public enum SettingsPage { Applications, Layout, Flyouts, Official, About, Global }
public sealed record HostAppearancePreferences(HostTheme Theme = HostTheme.System, MaterialKind Material = MaterialKind.Acrylic, double Opacity = 0.8);
public sealed record HostSettingsPreferences(HostAppearancePreferences Appearance, IReadOnlyList<string> ComponentOrder,
    IReadOnlyDictionary<string, DynamicGrouping>? IslandGrouping = null);
public interface IHostSettingsPreferenceStore
{
    CoreResult<HostSettingsPreferences> Load();
    CoreResult<HostSettingsPreferences> CommitAppearance(HostAppearancePreferences appearance);
    CoreResult<HostSettingsPreferences> CommitOrder(IReadOnlyList<string> order);
    CoreResult<HostSettingsPreferences> CommitGrouping(StableIdentity identity, DynamicGrouping grouping);
}
public sealed record SettingsNavigationSnapshot(SettingsPage Current, bool CanGoBack);
public sealed record HostSettingsSnapshot(SettingsNavigationSnapshot Navigation, HostSettingsPreferences Preferences,
    IReadOnlyList<HostComponentDisplayModel> Components, IReadOnlyList<BrokerApplicationSnapshot> Applications,
    string MaterialStatus, StructuredError? Error)
{
    public IReadOnlyList<HostIslandGrouping> Groupings { get; init; } = Array.Empty<HostIslandGrouping>();
}

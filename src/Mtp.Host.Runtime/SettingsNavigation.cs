namespace Mtp.Host;

/// <summary>Bounded history of the currently delivered Host settings destinations.</summary>
public sealed class SettingsNavigation
{
    public const int MaximumHistory = 64;
    private readonly List<SettingsPage> history = [];
    private readonly Func<SettingsPage, bool> reachable;
    private SettingsPage current = SettingsPage.Applications;

    public SettingsNavigation(Func<SettingsPage, bool>? reachable = null) => this.reachable = reachable ?? (_ => true);
    public SettingsNavigationSnapshot Snapshot => new(current, history.Any(reachable));
    public SettingsNavigationSnapshot Navigate(SettingsPage page)
    {
        if (!Enum.IsDefined(page) || !reachable(page) || page == current) return Snapshot;
        if (history.Count == MaximumHistory) history.RemoveAt(0);
        history.Add(current);
        current = page;
        return Snapshot;
    }
    public SettingsNavigationSnapshot Back()
    {
        while (history.Count != 0)
        {
            var target = history[^1];
            history.RemoveAt(history.Count - 1);
            if (!reachable(target)) continue;
            current = target;
            break;
        }
        return Snapshot;
    }
}

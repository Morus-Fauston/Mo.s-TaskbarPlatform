using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

public sealed record HostIslandInstance(TaskbarComponentKey Entry, string InstanceId, IReadOnlyList<HostItemPresentation> Items);
public sealed record HostIslandGrouping(StableIdentity Identity, DynamicGrouping Capability, DynamicGrouping Effective);

/// <summary>Organizes accepted current activities without owning business or item presentation state.</summary>
public static class HostIslandOrganization
{
    public static DynamicGrouping Effective(DynamicGrouping capability, DynamicGrouping? preference) => capability switch
    {
        DynamicGrouping.Together => DynamicGrouping.Together,
        DynamicGrouping.Separate => DynamicGrouping.Separate,
        DynamicGrouping.UserChoice => preference == DynamicGrouping.Separate ? DynamicGrouping.Separate : DynamicGrouping.Together,
        _ => throw new ArgumentOutOfRangeException(nameof(capability))
    };

    public static IReadOnlyList<HostIslandInstance> Project(TaskbarComponentKey entry, DynamicGrouping capability,
        DynamicGrouping? preference, IReadOnlyList<ActivityState> activities, IReadOnlyList<HostItemPresentation> items)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(items);
        if (activities.Count > DynamicContentLimits.MaximumActivitiesPerApplication || items.Count > DynamicContentLimits.MaximumItemsPerApplication)
            throw new ArgumentOutOfRangeException(nameof(items));
        var effective = Effective(capability, preference);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < activities.Count; i++)
            if (activities[i] is not { Ended: false } activity || string.IsNullOrWhiteSpace(activity.ActivityId) ||
                activity.ActivityId.Length > 256 || !positions.TryAdd(activity.ActivityId, i))
                throw new ArgumentException("Expected unique current activities.", nameof(activities));
        var parents = Enumerable.Range(0, activities.Count).ToArray();
        int Root(int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item?.Item?.ActivityIds is not { Count: > 0 and <= DynamicContentLimits.MaximumActivityReferencesPerItem } refs ||
                !itemIds.Add(item.Item.ItemId) || item.Handle.Item.ApplicationId != entry.ApplicationId ||
                item.Handle.Item.FeatureGroupId != entry.FeatureGroupId || item.Handle.Item.ComponentId != entry.ComponentId ||
                refs.Any(id => id is null || !positions.ContainsKey(id)))
                throw new ArgumentException("Expected unique items referencing current entry activities.", nameof(items));
            int first = positions[refs[0]];
            foreach (var id in refs.Skip(1)) parents[Root(positions[id])] = Root(first);
        }
        if (items.Count == 0) return Array.Empty<HostIslandInstance>();
        if (effective == DynamicGrouping.Together)
            return Array.AsReadOnly(new[] { new HostIslandInstance(entry, "$together", Array.AsReadOnly(items.ToArray())) });
        var groups = Enumerable.Range(0, activities.Count).GroupBy(Root)
            .Select(group => new
            {
                Root = group.Key,
                Id = group.Select(index => activities[index].ActivityId).Order(StringComparer.Ordinal).First(),
                First = group.OrderBy(index => activities[index].Order).ThenBy(index => index).First()
            }).OrderBy(group => activities[group.First].Order).ThenBy(group => group.First);
        return Array.AsReadOnly(groups.Select(group => new HostIslandInstance(entry, group.Id,
                Array.AsReadOnly(items.Where(item => Root(positions[item.Item.ActivityIds[0]]) == group.Root).ToArray())))
            .Where(group => group.Items.Count != 0).ToArray());
    }
}

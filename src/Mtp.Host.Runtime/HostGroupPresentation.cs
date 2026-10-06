using System.Collections.ObjectModel;
using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

public sealed record HostGroupPresentationSnapshot(TaskbarGroupLayoutResult Layout,
    IReadOnlyList<HostComponentDisplayModel> Components, IReadOnlyList<HostItemPresentation> Items,
    IReadOnlyDictionary<TaskbarComponentKey, HostComponentDisplayModel> ComponentsByKey,
    IReadOnlyDictionary<TaskbarItemKey, HostItemPresentation> ItemsByKey)
{
    public IReadOnlyList<HostIslandInstance> Instances { get; init; } = Array.Empty<HostIslandInstance>();
    public HostPresentationEnvironment Environment { get; init; } = new();
}

/// <summary>Projects stable, visible Host entries into fixed-right-edge layout targets without owning screen state.</summary>
public static class HostGroupPresentation
{
    public static HostGroupPresentationSnapshot Build(IReadOnlyList<HostComponentDisplayModel> orderedComponents,
        BrokerStateStore? states, ItemPresentationController? presentations, string screenId, bool compact = false,
        double availableWidthDip = double.MaxValue, IReadOnlyDictionary<string, DynamicGrouping>? grouping = null,
        HostPresentationEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(orderedComponents);
        if (orderedComponents.Count > TaskbarGroupLayout.MaximumComponents)
            throw new ArgumentOutOfRangeException(nameof(orderedComponents));
        if (string.IsNullOrWhiteSpace(screenId) || screenId.Length > ItemPresentationLimits.MaximumScreenIdLength)
            throw new ArgumentException("Screen identity is invalid.", nameof(screenId));
        var visible = new List<HostComponentDisplayModel>();
        var measured = new List<TaskbarMeasuredComponent>();
        var componentsByKey = new Dictionary<TaskbarComponentKey, HostComponentDisplayModel>();
        var itemsByKey = new Dictionary<TaskbarItemKey, HostItemPresentation>();
        var allItems = new List<HostItemPresentation>();
        var instances = new List<HostIslandInstance>();
        var resolvedEnvironment = environment ?? new HostPresentationEnvironment(compact ? HostPresentationDensity.Compact : HostPresentationDensity.Normal);
        var applications = states?.Snapshots.ToDictionary(value => value.ApplicationId, StringComparer.Ordinal)
            ?? new Dictionary<string, BrokerApplicationSnapshot>(StringComparer.Ordinal);
        var declaredIdentities = applications.Values.Where(value => value.Declaration is not null)
            .SelectMany(value => value.Declaration!.FeatureGroups.SelectMany(group => group.Components))
            .Select(value => value.Identity).ToHashSet();
        var dynamicIdentities = applications.Values.Where(value => value.Declaration is not null)
            .SelectMany(value => value.Declaration!.DynamicContents).Select(value => value.ComponentIdentity).ToHashSet();
        var sourceItems = (presentations?.GetPresentation(screenId) ?? []).ToLookup(value =>
            new TaskbarComponentKey(value.Handle.Item.ApplicationId, value.Handle.Item.FeatureGroupId, value.Handle.Item.ComponentId));
        var seen = new HashSet<TaskbarComponentKey>();
        foreach (var component in orderedComponents)
        {
            if (component?.Identity is null || component.Identity.Segments.Count != 3)
                throw new ArgumentException("A component requires a complete stable identity.", nameof(orderedComponents));
            var ids = component.Identity.Segments;
            var key = new TaskbarComponentKey(ids[0].Value, ids[1].Value, ids[2].Value);
            if (!seen.Add(key)) throw new ArgumentException("Component identities must be unique.", nameof(orderedComponents));
            if (!component.IsVisible) continue;
            applications.TryGetValue(key.ApplicationId, out var application);
            if (application is not null && !declaredIdentities.Contains(component.Identity)) continue;
            if (dynamicIdentities.Contains(component.Identity))
            {
                var currentItems = sourceItems[key].Where(value => value.SessionId == application!.SessionId &&
                    application.ItemOccurrences.TryGetValue(value.Handle.Item, out var occurrence) && occurrence == value.PresenceGeneration).ToArray();
                if (currentItems.Length == 0) continue;
                // Presentation synchronization can see a newer Broker frame than this captured snapshot.
                // Keep this projection's fields and activity references on one authoritative frame.
                var acceptedEntry = application!.State!.DynamicEntries!.Single(value => value.FeatureGroupId == key.FeatureGroupId && value.ComponentId == key.ComponentId);
                var presentedItems = currentItems.ToDictionary(value => value.Item.ItemId, StringComparer.Ordinal);
                currentItems = acceptedEntry.Content.Items.Where(value => presentedItems.ContainsKey(value.ItemId))
                    .Select(value => presentedItems[value.ItemId] with { Item = value, IsInteractive = application.IsInteractive }).ToArray();
                if (currentItems.Length == 0) continue;
                var declaration = application!.Declaration!.DynamicContents.Single(value => value.ComponentIdentity == component.Identity).Declaration;
                IReadOnlyList<HostIslandInstance> entryInstances = [];
                if (declaration.Kind == DynamicContentKind.LiveIsland)
                {
                    var activities = acceptedEntry.Content.Activities;
                    entryInstances = HostIslandOrganization.Project(key, declaration.Grouping,
                        grouping?.GetValueOrDefault(HostSettingsController.IdentityKey(component.Identity)), activities, currentItems);
                    instances.AddRange(entryInstances);
                    currentItems = entryInstances.SelectMany(value => value.Items).ToArray();
                }
                var instanceByItem = entryInstances.SelectMany(instance => instance.Items.Select(item => (item.Item.ItemId, instance.InstanceId)))
                    .ToDictionary(value => value.ItemId, value => value.InstanceId, StringComparer.Ordinal);
                var itemMeasurements = new List<TaskbarMeasuredItem>(currentItems.Length);
                foreach (var item in currentItems)
                {
                    var itemKey = new TaskbarItemKey(key, item.Item.ItemId, item.PresenceGeneration);
                    itemsByKey.Add(itemKey, item);
                    allItems.Add(item);
                    itemMeasurements.Add(new(itemKey, DynamicWidthMetrics.Measure(item.Presentation.Width, environment: resolvedEnvironment), item.IsInteractive, instanceByItem.GetValueOrDefault(item.Item.ItemId)));
                }
                measured.Add(new(key, Items: itemMeasurements));
            }
            else measured.Add(new(key, (component.HasTemplate ? 480 : 240) * resolvedEnvironment.SlotWidthDip / 32d));
            visible.Add(component); componentsByKey.Add(key, component);
        }
        // The group keeps the taskbar's fixed 32 DIP height; density and text scale
        // affect slot widths and text metrics only.
        var layout = TaskbarGroupLayout.Calculate(measured, 32, availableWidthDip);
        return new(layout, visible.AsReadOnly(), allItems.AsReadOnly(),
            new ReadOnlyDictionary<TaskbarComponentKey, HostComponentDisplayModel>(componentsByKey),
            new ReadOnlyDictionary<TaskbarItemKey, HostItemPresentation>(itemsByKey)) { Instances = instances.AsReadOnly(), Environment = resolvedEnvironment };
    }
}

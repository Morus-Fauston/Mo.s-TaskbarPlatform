using Mtp.Contracts;

namespace Mtp.Host;

/// <summary>Owns temporary item presentation. All calls serialize under one lock; no UI objects or workers are retained.</summary>
public sealed class ItemPresentationController(BrokerStateStore states)
{
    private readonly BrokerStateStore states = states ?? throw new ArgumentNullException(nameof(states));
    private readonly object gate = new();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Dictionary<string, Dictionary<DynamicItemIdentity, HostItemPresentation>> screens = new(StringComparer.Ordinal);
    private long generation;
    private bool closed;

    public ProtocolResult UpdateScreens(IReadOnlyList<string> screenIds)
    {
        lock (gate)
        {
            if (closed) return ProtocolResult.Reject("PresentationClosed", "项呈现已关闭");
            if (screenIds is null || screenIds.Count > ItemPresentationLimits.MaximumScreens ||
                screenIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > ItemPresentationLimits.MaximumScreenIdLength) ||
                screenIds.Distinct(StringComparer.Ordinal).Count() != screenIds.Count)
                return ProtocolResult.Reject("InvalidScreens", "屏幕集合无效或超出预算");
            foreach (var id in screens.Keys.Except(screenIds, StringComparer.Ordinal).ToArray()) screens.Remove(id);
            foreach (var id in screenIds) screens.TryAdd(id, []);
            Synchronize();
            return ProtocolResult.Success();
        }
    }

    public IReadOnlyList<HostItemPresentation> GetPresentation(string screenId)
    {
        lock (gate)
        {
            if (closed) return Array.Empty<HostItemPresentation>();
            Synchronize();
            return Snapshot(screenId);
        }
    }

    public HostItemPresentation? Resolve(ItemInteractionHandle handle)
    {
        lock (gate)
        {
            if (closed) return null;
            Synchronize();
            return ResolveCurrent(handle);
        }
    }

    public ItemExpansionResult Toggle(ItemInteractionHandle handle, bool declaredTargets = false)
    {
        lock (gate)
        {
            if (closed) return Rejected("PresentationClosed", "项呈现已关闭");
            Synchronize();
            var current = ResolveCurrent(handle);
            if (current is null) return Rejected("StaleItem", "项交互句柄已失效");
            var screen = screens[handle.ScreenId];
            HostItemPresentation[] targets;
            if (declaredTargets)
            {
                var structures = new HashSet<string>(current.Structure.ExpandTargetStructureIds ?? [], StringComparer.Ordinal);
                targets = screen.Values.Where(item => item.Handle.Item.ApplicationId == handle.Item.ApplicationId &&
                    item.Handle.Item.FeatureGroupId == handle.Item.FeatureGroupId && item.Handle.Item.ComponentId == handle.Item.ComponentId &&
                    structures.Contains(item.Structure.StructureId)).ToArray();
                if (targets.Length == 0) return Rejected("NoExpansionTargets", "当前没有展开目标");
            }
            else targets = [current];
            if (targets.Any(item => item.Structure.Expanded is null)) return Rejected("ItemNotExpandable", "当前项未声明展开态");
            bool expanded = !targets.All(item => item.Expanded);
            foreach (var target in targets)
                screen[target.Handle.Item] = target with
                { Expanded = expanded, Presentation = expanded ? target.Structure.Expanded! : target.Structure.Normal };
            return new(ProtocolResult.Success(), Snapshot(handle.ScreenId));
        }
    }

    public void Close()
    {
        lock (gate) { closed = true; screens.Clear(); }
    }

    private void Synchronize()
    {
        var source = new List<(DynamicItemIdentity Key, BrokerApplicationSnapshot Snapshot, DynamicContentKind Kind,
            ItemStructureDeclaration Structure, DynamicItemState Item)>();
        foreach (var snapshot in states.Snapshots)
        {
            if (snapshot.Declaration is null || snapshot.State is null) continue;
            foreach (var entry in snapshot.State.DynamicEntries ?? [])
            {
                var declaration = snapshot.Declaration.DynamicContents.First(value =>
                    value.ComponentIdentity.Segments[1].Value == entry.FeatureGroupId && value.ComponentIdentity.LocalId.Value == entry.ComponentId).Declaration;
                foreach (var item in entry.Content.Items)
                    source.Add((new(snapshot.ApplicationId, entry.FeatureGroupId, entry.ComponentId, item.ItemId), snapshot,
                        declaration.Kind, declaration.Structures.First(value => value.StructureId == item.StructureId), item));
            }
        }
        foreach (var screenId in screens.Keys.ToArray())
        {
            var previous = screens[screenId];
            var next = new Dictionary<DynamicItemIdentity, HostItemPresentation>();
            foreach (var item in source)
            {
                previous.TryGetValue(item.Key, out var old);
                bool sameStructure = old is not null && old.Kind == item.Kind && ItemStructureEquality.Same(old.Structure, item.Structure) &&
                    old.PresenceGeneration == item.Snapshot.ItemOccurrences[item.Key];
                var expanded = sameStructure ? old!.Expanded : item.Structure.InitiallyExpanded;
                var handle = sameStructure && old!.SessionId == item.Snapshot.SessionId ? old.Handle :
                    new ItemInteractionHandle(owner, screenId, item.Key, checked(++generation));
                next.Add(item.Key, new(handle, item.Snapshot.SessionId, item.Kind, item.Structure, item.Item, expanded,
                    expanded ? item.Structure.Expanded! : item.Structure.Normal, item.Snapshot.IsInteractive, item.Snapshot.ItemOccurrences[item.Key]));
            }
            screens[screenId] = next;
        }
    }

    private HostItemPresentation? ResolveCurrent(ItemInteractionHandle? handle) =>
        handle is not null && handle.Owner == owner && handle.ScreenId is not null && handle.Item is not null &&
        screens.TryGetValue(handle.ScreenId, out var screen) && screen.TryGetValue(handle.Item, out var item) && item.Handle == handle ? item : null;

    private IReadOnlyList<HostItemPresentation> Snapshot(string screenId) =>
        screenId is not null && screens.TryGetValue(screenId, out var screen) ? Array.AsReadOnly(screen.Values.ToArray()) : Array.Empty<HostItemPresentation>();

    private static ItemExpansionResult Rejected(string code, string message) => new(ProtocolResult.Reject(code, message), Array.Empty<HostItemPresentation>());
}

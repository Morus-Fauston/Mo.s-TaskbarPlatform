using Mtp.Contracts;

namespace Mtp.Host;

/// <summary>Runs only after whole-frame validation. The Store owns synchronization and frozen snapshots.</summary>
internal static class ActivityLifecycle
{
    public static (ApplicationState State, ProtocolResult Result) Admit(string applicationId,
        ValidatedApplicationDeclaration declaration, ApplicationState incoming, ApplicationState? previous,
        DisplayPermissionSnapshot permissions, DateTimeOffset now)
    {
        var islands = IslandKeys(declaration);
        var rejected = new List<ActivityAdmissionRejection>();
        var entries = new List<DynamicEntryState>();
        foreach (var entry in incoming.DynamicEntries ?? [])
        {
            if (!islands.Contains((entry.FeatureGroupId, entry.ComponentId))) { entries.Add(entry); continue; }
            bool allowed = permissions.Entries.Any(value => value.FeatureGroupId == entry.FeatureGroupId && value.ComponentId == entry.ComponentId && value.Allowed);
            var held = previous?.DynamicEntries?.FirstOrDefault(value => value.FeatureGroupId == entry.FeatureGroupId && value.ComponentId == entry.ComponentId)?
                .Content.Activities.Where(value => !value.Ended && value.ExpiresAt > now).Select(value => value.ActivityId).ToHashSet(StringComparer.Ordinal) ?? [];
            var admitted = new List<ActivityState>();
            foreach (var activity in entry.Content.Activities)
            {
                if (activity.Ended) continue;
                if (!allowed && !held.Contains(activity.ActivityId))
                    rejected.Add(new(applicationId, entry.FeatureGroupId, entry.ComponentId, activity.ActivityId, "DisplayNotAllowed"));
                else admitted.Add(activity);
            }
            entries.Add(Filter(entry, admitted));
        }
        var accepted = incoming with { DynamicEntries = Array.AsReadOnly(entries.ToArray()) };
        var result = rejected.Count == 0 ? ProtocolResult.Success() : new ProtocolResult(true, "AcceptedWithActivityRejections",
            "合法状态已接收，部分活动创建未获显示许可", ActivityRejections: Array.AsReadOnly(rejected.ToArray()));
        return (accepted, result);
    }

    public static ApplicationState Prune(ValidatedApplicationDeclaration declaration, ApplicationState state, DateTimeOffset now)
    {
        var islands = IslandKeys(declaration);
        bool changed = false;
        var entries = new List<DynamicEntryState>();
        foreach (var entry in state.DynamicEntries ?? [])
        {
            if (!islands.Contains((entry.FeatureGroupId, entry.ComponentId)) ||
                entry.Content.Activities.All(value => !value.Ended && value.ExpiresAt > now)) { entries.Add(entry); continue; }
            entries.Add(Filter(entry, entry.Content.Activities.Where(value => !value.Ended && value.ExpiresAt > now).ToArray()));
            changed = true;
        }
        return changed ? state with { DynamicEntries = Array.AsReadOnly(entries.ToArray()) } : state;
    }

    public static HashSet<(string Group, string Component)> IslandKeys(ValidatedApplicationDeclaration declaration) => declaration.DynamicContents
        .Where(value => value.Declaration.Kind == DynamicContentKind.LiveIsland)
        .Select(value => (value.ComponentIdentity.Segments[1].Value, value.ComponentIdentity.Segments[2].Value)).ToHashSet();

    private static DynamicEntryState Filter(DynamicEntryState entry, IReadOnlyList<ActivityState> activities)
    {
        var ids = activities.Select(value => value.ActivityId).ToHashSet(StringComparer.Ordinal);
        var items = entry.Content.Items.Select(value => value with
        { ActivityIds = Array.AsReadOnly(value.ActivityIds.Where(ids.Contains).ToArray()) }).Where(value => value.ActivityIds.Count > 0).ToArray();
        return entry with { Content = new(Array.AsReadOnly(activities.ToArray()), Array.AsReadOnly(items)) };
    }
}

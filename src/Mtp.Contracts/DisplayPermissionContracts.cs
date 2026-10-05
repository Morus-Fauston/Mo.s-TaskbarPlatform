using System.Collections.Generic;

namespace Mtp.Contracts;

public static class DisplayPermissionLimits
{
    public const int MaximumEntriesPerApplication = 128;
}

public sealed record ActivityAdmissionRejection(string ApplicationId, string FeatureGroupId,
    string ComponentId, string ActivityId, string Code);

public sealed record EntryDisplayPermission(string FeatureGroupId, string ComponentId, bool Allowed);

public sealed record DisplayPermissionSnapshot(long Revision, IReadOnlyList<EntryDisplayPermission> Entries);

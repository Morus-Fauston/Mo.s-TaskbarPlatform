using System;
using System.Collections.Generic;

namespace Mtp.Platform.Core;

/// <summary>
/// Keeps the latest accepted component state in memory.
/// </summary>
public sealed class ComponentStateStore
{
    private readonly Dictionary<StableIdentity, ComponentStateSnapshot> snapshots = new();

    public bool TryApply(ComponentStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshots.TryGetValue(snapshot.Component, out var current) && snapshot.Revision <= current.Revision)
        {
            return false;
        }

        snapshots[snapshot.Component] = snapshot;
        return true;
    }

    public bool TryGet(StableIdentity component, out ComponentStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(component);
        return snapshots.TryGetValue(component, out snapshot!);
    }
}

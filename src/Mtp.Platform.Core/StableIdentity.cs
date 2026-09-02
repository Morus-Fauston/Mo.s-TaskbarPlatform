using System;
using System.Collections.Generic;

namespace Mtp.Platform.Core;

/// <summary>
/// A stable identifier plus its structured parent chain.
/// </summary>
public sealed record StableIdentity
{
    public StableIdentity(StableId localId, StableIdentity? parent = null)
    {
        if (string.IsNullOrEmpty(localId.Value))
        {
            throw new ArgumentException("A stable identity requires a valid local ID.", nameof(localId));
        }

        LocalId = localId;
        Parent = parent;
    }

    public StableId LocalId { get; }

    public StableIdentity? Parent { get; }

    public StableIdentity CreateChild(StableId localId) => new(localId, this);

    public IReadOnlyList<StableId> Segments
    {
        get
        {
            var segments = new List<StableId>();
            for (var current = this; current is not null; current = current.Parent)
            {
                segments.Add(current.LocalId);
            }

            segments.Reverse();
            return segments;
        }
    }
}

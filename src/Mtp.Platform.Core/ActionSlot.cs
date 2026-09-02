using System;

namespace Mtp.Platform.Core;

/// <summary>
/// A named, stable location to which an interaction can be bound.
/// </summary>
public sealed record ActionSlot
{
    public ActionSlot(StableIdentity identity)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public StableIdentity Identity { get; }
}

using System;

namespace Mtp.Platform.Core;

/// <summary>
/// An immutable component state update with a monotonic revision.
/// </summary>
public sealed record ComponentStateSnapshot
{
    public ComponentStateSnapshot(StableIdentity component, CapabilityState capability, long revision)
    {
        Component = component ?? throw new ArgumentNullException(nameof(component));
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "A state revision cannot be negative.");
        }

        Revision = revision;
    }

    public StableIdentity Component { get; }

    public CapabilityState Capability { get; }

    public long Revision { get; }
}

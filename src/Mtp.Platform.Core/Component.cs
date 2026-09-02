using System;
using System.Collections.Generic;
using System.Linq;

namespace Mtp.Platform.Core;

/// <summary>
/// The smallest platform-owned component description.
/// </summary>
public sealed record Component
{
    public Component(
        StableIdentity identity,
        CapabilityState capability,
        IEnumerable<ActionSlot>? actionSlots = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));

        var slots = (actionSlots ?? Enumerable.Empty<ActionSlot>()).ToArray();
        if (slots.Any(slot => slot is null))
        {
            throw new ArgumentException("Action slots cannot contain null values.", nameof(actionSlots));
        }

        if (slots.Any(slot => !Equals(slot.Identity.Parent, identity)))
        {
            throw new ArgumentException("Each action slot must be a child of the component identity.", nameof(actionSlots));
        }

        ActionSlots = Array.AsReadOnly(slots);
    }

    public StableIdentity Identity { get; }

    public CapabilityState Capability { get; }

    public IReadOnlyList<ActionSlot> ActionSlots { get; }

    public Component WithCapability(CapabilityState capability) =>
        new(Identity, capability, ActionSlots);
}

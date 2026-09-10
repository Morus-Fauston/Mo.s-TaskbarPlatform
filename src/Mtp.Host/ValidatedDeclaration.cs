using System;
using System.Collections.Generic;
using System.Linq;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// A fully validated declaration snapshot that Host modules can consume.
/// </summary>
public sealed class ValidatedApplicationDeclaration
{
    internal ValidatedApplicationDeclaration(
        StableIdentity identity,
        IEnumerable<ValidatedFeatureGroup> featureGroups)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (identity.Parent is not null)
        {
            throw new ArgumentException("An application identity cannot have a parent.", nameof(identity));
        }

        var groups = featureGroups?.ToArray() ?? throw new ArgumentNullException(nameof(featureGroups));
        if (groups.Length == 0 || groups.Any(group => group is null || group.Identity.Parent != identity))
        {
            throw new ArgumentException("A validated application requires feature groups that belong to it.", nameof(featureGroups));
        }

        FeatureGroups = Array.AsReadOnly(groups);
    }

    public StableIdentity Identity { get; }

    public IReadOnlyList<ValidatedFeatureGroup> FeatureGroups { get; }
}

/// <summary>
/// A validated feature group and its complete entry set.
/// </summary>
public sealed class ValidatedFeatureGroup
{
    internal ValidatedFeatureGroup(
        StableIdentity identity,
        IEnumerable<Component> components,
        IEnumerable<ValidatedTaskbarFlyout> taskbarFlyouts)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (identity.Parent is null)
        {
            throw new ArgumentException("A feature group identity requires an application parent.", nameof(identity));
        }

        var componentArray = components?.ToArray() ?? throw new ArgumentNullException(nameof(components));
        var flyoutArray = taskbarFlyouts?.ToArray() ?? throw new ArgumentNullException(nameof(taskbarFlyouts));
        if (componentArray.Length == 0 || flyoutArray.Length == 0 ||
            componentArray.Any(component => component is null || component.Identity.Parent != identity) ||
            flyoutArray.Any(flyout => flyout is null || flyout.Identity.Parent != identity))
        {
            throw new ArgumentException("A validated feature group requires entries that belong to it.");
        }

        Components = Array.AsReadOnly(componentArray);
        TaskbarFlyouts = Array.AsReadOnly(flyoutArray);
    }

    public StableIdentity Identity { get; }

    public IReadOnlyList<Component> Components { get; }

    public IReadOnlyList<ValidatedTaskbarFlyout> TaskbarFlyouts { get; }
}

/// <summary>
/// A validated taskbar-operation flyout entry.
/// </summary>
public sealed class ValidatedTaskbarFlyout
{
    internal ValidatedTaskbarFlyout(
        StableIdentity identity,
        IEnumerable<ActionSlot> actionSlots)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (identity.Parent is null)
        {
            throw new ArgumentException("A taskbar flyout identity requires a feature group parent.", nameof(identity));
        }

        var slots = actionSlots?.ToArray() ?? throw new ArgumentNullException(nameof(actionSlots));
        if (slots.Length == 0 || slots.Any(slot => slot is null || slot.Identity.Parent != identity))
        {
            throw new ArgumentException("A validated taskbar flyout requires action slots that belong to it.", nameof(actionSlots));
        }

        ActionSlots = Array.AsReadOnly(slots);
    }

    public StableIdentity Identity { get; }

    public IReadOnlyList<ActionSlot> ActionSlots { get; }
}

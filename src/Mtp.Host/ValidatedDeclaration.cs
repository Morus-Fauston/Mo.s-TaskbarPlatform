using System.Collections.Generic;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// A fully validated declaration snapshot that Host modules can consume.
/// </summary>
public sealed record ValidatedApplicationDeclaration(
    StableIdentity Identity,
    IReadOnlyList<ValidatedFeatureGroup> FeatureGroups);

/// <summary>
/// A validated feature group and its complete entry set.
/// </summary>
public sealed record ValidatedFeatureGroup(
    StableIdentity Identity,
    IReadOnlyList<Component> Components,
    IReadOnlyList<ValidatedTaskbarFlyout> TaskbarFlyouts);

/// <summary>
/// A validated taskbar-operation flyout entry.
/// </summary>
public sealed record ValidatedTaskbarFlyout(
    StableIdentity Identity,
    IReadOnlyList<ActionSlot> ActionSlots);

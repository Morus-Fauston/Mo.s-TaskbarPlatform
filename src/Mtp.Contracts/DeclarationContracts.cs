using System.Collections.Generic;

namespace Mtp.Contracts;

/// <summary>
/// The minimum declaration submitted by one MTP-connected application.
/// </summary>
public sealed record ApplicationDeclaration(
    string? ApplicationId,
    IReadOnlyList<FeatureGroupDeclaration>? FeatureGroups);

/// <summary>
/// A functional group and its Host-owned entry points.
/// </summary>
public sealed record FeatureGroupDeclaration(
    string? FeatureGroupId,
    IReadOnlyList<ComponentDeclaration>? Components,
    IReadOnlyList<TaskbarFlyoutDeclaration>? TaskbarFlyouts);

/// <summary>
/// A component declaration with action bindings.
/// </summary>
public sealed record ComponentDeclaration(
    string? ComponentId,
    IReadOnlyList<ActionSlotDeclaration>? ActionSlots);

/// <summary>
/// A taskbar-operation flyout declaration with action bindings.
/// </summary>
public sealed record TaskbarFlyoutDeclaration(
    string? TaskbarFlyoutId,
    IReadOnlyList<ActionSlotDeclaration>? ActionSlots);

/// <summary>
/// A named action slot under a component or taskbar-operation flyout.
/// </summary>
public sealed record ActionSlotDeclaration(string? ActionSlotId);

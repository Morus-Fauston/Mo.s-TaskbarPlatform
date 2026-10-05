using System.Collections.Generic;

namespace Mtp.Contracts;

public enum FlyoutKind { TaskbarGroup, ShortHint, InteractiveHint, EventGroup }
public enum FlyoutScreen { Trigger, Primary }
public enum FlyoutPosition { Default, TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight, Center, LowerCenter }
public enum EventClosePolicy { AutoClose, Persistent }

/// <summary>A declared hint entry. Only ShortHint and InteractiveHint are valid kinds.</summary>
public sealed record HintEntryDeclaration(string? EntryId, FlyoutKind Kind, EntryTemplateDeclaration? Template = null,
    IReadOnlyList<ActionSlotDeclaration>? ActionSlots = null, HintExpansionTarget? Expansion = null);
public sealed record HintExpansionTarget(string TaskbarFlyoutId, string? PanelTemplateId = null);

/// <summary>A declared event source; absence of its close policy is invalid.</summary>
public sealed record EventChannelDeclaration(string? ChannelId, EventClosePolicy? ClosePolicy, EntryTemplateDeclaration? Template = null,
    IReadOnlyList<ActionSlotDeclaration>? ActionSlots = null);

/// <summary>A controlled request, without templates, coordinates, or caller-selected durations.</summary>
public sealed record FlyoutRequest(
    string RequestId,
    long RequestSequence,
    string FeatureGroupId,
    string EntryId,
    FlyoutKind Kind,
    long StateRevision,
    FlyoutScreen Screen = FlyoutScreen.Trigger,
    FlyoutPosition Position = FlyoutPosition.Default);

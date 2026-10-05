namespace Mtp.Contracts;

public enum FlyoutKind { TaskbarGroup, ShortHint, InteractiveHint, EventGroup }
public enum FlyoutScreen { Trigger, Primary }
public enum FlyoutPosition { Default }
public enum EventClosePolicy { AutoClose, Persistent }

/// <summary>A declared hint entry. Only ShortHint and InteractiveHint are valid kinds.</summary>
public sealed record HintEntryDeclaration(string? EntryId, FlyoutKind Kind);

/// <summary>A declared event source; absence of its close policy is invalid.</summary>
public sealed record EventChannelDeclaration(string? ChannelId, EventClosePolicy? ClosePolicy);

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

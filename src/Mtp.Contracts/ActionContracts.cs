namespace Mtp.Contracts;

public enum ActionParameterKind { None, Boolean, Number, Text }
public enum ActionEntryKind { Component, TaskbarFlyout }

public sealed record ActionParameter(ActionParameterKind Kind = ActionParameterKind.None,
    bool? Boolean = null, double? Number = null, string? Text = null);
public sealed record ActionSlotReference(string ApplicationId, string FeatureGroupId,
    ActionEntryKind EntryKind, string EntryId, string ActionSlotId);
public sealed record ActionInvocation(string RequestId, long Sequence, ActionSlotReference Slot, ActionParameter Parameter);
public sealed record ActionCompletion(string RequestId, long Sequence, ProtocolResult Result, ApplicationState? State = null);

public static class ActionLimits
{
    public const int MaximumOutstandingPerApplication = 4;
    public const int WaitTimeoutSeconds = 5;
}

using System;
using System.Collections.Generic;

namespace Mtp.Contracts;

public static class DynamicContentLimits
{
    public const int MaximumStructuresPerEntry = 16;
    public const int MaximumActivitiesPerApplication = 64;
    public const int MaximumItemsPerApplication = 128;
    public const int MaximumActivityReferencesPerItem = 8;
    public const int MaximumSlots = 8;
    public static readonly TimeSpan MaximumRetention = TimeSpan.FromHours(24);
}

public enum DynamicContentKind { OrdinaryItems, LiveIsland }
public enum DynamicGrouping { Together, Separate, UserChoice }
public enum PresetTemplate { Timer, Progress, Counter, Status, Composite }
[Flags]
public enum ContentFields { None = 0, Timer = 1, Progress = 2, Counter = 4, Status = 8 }
public enum WidthTier { Small, Medium, Large }
public enum SemanticAnimation { None, ContentChange, EnterExit }
public enum TextOverflow { Ellipsis, Scroll }
public enum TimerDirection { CountUp, CountDown }
public enum ProgressMode { Determinate, Indeterminate }
public enum CounterSemantics { CompletedCount, CurrentIndex }
public enum StatusMarker { Normal, Attention, Error }

/// <summary>Exactly one Host width tier or bounded slot capacity; never a provider pixel width.</summary>
public sealed record ContentWidth(WidthTier? Tier = null, int? Slots = null);
public sealed record ItemPresentation(PresetTemplate Template, ContentFields Fields, ContentWidth Width);
public sealed record ItemStructureDeclaration(
    string StructureId,
    bool IsRepeated,
    ItemPresentation Normal,
    ItemPresentation? Expanded = null,
    bool InitiallyExpanded = false,
    IReadOnlyList<string>? ExpandTargetStructureIds = null,
    SemanticAnimation Animation = SemanticAnimation.ContentChange,
    TextOverflow Overflow = TextOverflow.Ellipsis,
    ItemActivationBinding? PrimaryActivation = null,
    IReadOnlyList<ItemControlActivation>? ControlActivations = null);
public sealed record DynamicContentDeclaration(
    DynamicContentKind Kind,
    IReadOnlyList<ItemStructureDeclaration> Structures,
    DynamicGrouping Grouping = DynamicGrouping.Together);

public sealed record TimerBasis(TimerDirection Direction, DateTimeOffset ReferenceUtc,
    double ValueMillisecondsAtReference, bool IsPaused = false, bool ShowOvertime = false,
    double? ProgressDurationMilliseconds = null);
public sealed record ProgressReading(ProgressMode Mode, double? Value = null, double? Maximum = null);
public sealed record CounterReading(CounterSemantics Semantics, long Value, long? Total = null);
public sealed record StatusReading(string Text, StatusMarker Marker = StatusMarker.Normal);
public sealed record DynamicItemFields(TimerBasis? Timer = null, ProgressReading? Progress = null,
    CounterReading? Counter = null, StatusReading? Status = null);
public sealed record ActivityState(string ActivityId, DateTimeOffset ExpiresAt, bool Ended = false, int Order = 0);
public sealed record DynamicItemState(string ItemId, string StructureId,
    IReadOnlyList<string> ActivityIds, DynamicItemFields Fields);
public sealed record DynamicContentState(IReadOnlyList<ActivityState> Activities, IReadOnlyList<DynamicItemState> Items);
public sealed record DynamicEntryState(string FeatureGroupId, string ComponentId, DynamicContentState Content);

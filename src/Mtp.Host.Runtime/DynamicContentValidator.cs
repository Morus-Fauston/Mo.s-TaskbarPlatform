using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

public sealed class ValidatedDynamicContentDeclaration
{
    internal ValidatedDynamicContentDeclaration(StableIdentity identity, DynamicContentDeclaration declaration)
    {
        ComponentIdentity = identity;
        Declaration = declaration;
    }
    public StableIdentity ComponentIdentity { get; }
    public DynamicContentDeclaration Declaration { get; }
}

public sealed class ValidatedDynamicContentState
{
    internal ValidatedDynamicContentState(DynamicContentState content) => Content = content;
    public DynamicContentState Content { get; }
}

/// <summary>Validates preset dynamic declarations and whole entry snapshots before publication.</summary>
public sealed class DynamicContentValidator
{
    public CoreResult<ValidatedDynamicContentDeclaration> ValidateDeclaration(
        DynamicContentDeclaration declaration, StableIdentity componentIdentity, ref int remainingNodes)
    {
        if (declaration?.Structures is null || componentIdentity is null || componentIdentity.Segments.Count != 3 ||
            !Enum.IsDefined(declaration.Kind) || !Enum.IsDefined(declaration.Grouping))
            return Failure<ValidatedDynamicContentDeclaration>("dynamic_structure_invalid", "动态入口结构缺失", "dynamicContent");
        if (declaration.Structures.Count is < 1 or > DynamicContentLimits.MaximumStructuresPerEntry)
            return Failure<ValidatedDynamicContentDeclaration>("dynamic_budget_exceeded", "动态结构数量超限", "dynamicContent.structures");
        var cost = 1;
        var structures = new Dictionary<string, ItemStructureDeclaration>(StringComparer.Ordinal);
        var frozenStructures = new List<ItemStructureDeclaration>();
        foreach (var item in declaration.Structures)
        {
            if (item is null || !ValidId(item.StructureId))
                return Failure<ValidatedDynamicContentDeclaration>("dynamic_identity_invalid", "结构标识无效", "dynamicContent.structures");
            if (!structures.TryAdd(item.StructureId, item))
                return Failure<ValidatedDynamicContentDeclaration>("dynamic_duplicate_identity", "结构标识重复", "dynamicContent.structures");
            if (!ValidPresentation(item.Normal) || (item.Expanded is not null && !ValidPresentation(item.Expanded)) ||
                (item.InitiallyExpanded && item.Expanded is null) || !Enum.IsDefined(item.Animation) || !Enum.IsDefined(item.Overflow))
                return Failure<ValidatedDynamicContentDeclaration>("dynamic_structure_invalid", "预置呈现、宽度或语义无效", "dynamicContent.structures." + item.StructureId);
            if (item.ExpandTargetStructureIds?.Count > DynamicContentLimits.MaximumStructuresPerEntry)
                return Failure<ValidatedDynamicContentDeclaration>("dynamic_budget_exceeded", "展开目标数量超限", "dynamicContent.expandTargets");
            cost += 3 + (item.Expanded is null ? 0 : 2) + (item.ExpandTargetStructureIds?.Count ?? 0);
            frozenStructures.Add(item with
            {
                ExpandTargetStructureIds = Array.AsReadOnly(item.ExpandTargetStructureIds?.ToArray() ?? [])
            });
        }
        foreach (var item in frozenStructures)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in item.ExpandTargetStructureIds!)
                if (!ValidId(target) || !targets.Add(target) || !structures.TryGetValue(target, out var selected) || selected.Expanded is null)
                    return Failure<ValidatedDynamicContentDeclaration>("dynamic_reference_invalid", "展开目标未声明可展开结构或重复", "dynamicContent.expandTargets");
        }
        if (remainingNodes < cost)
            return Failure<ValidatedDynamicContentDeclaration>("dynamic_budget_exceeded", "动态结构超出共享节点预算", "dynamicContent");
        var frozen = declaration with { Structures = Array.AsReadOnly(frozenStructures.ToArray()) };
        remainingNodes -= cost;
        return CoreResult<ValidatedDynamicContentDeclaration>.Success(new(componentIdentity, frozen));
    }

    public CoreResult<ValidatedDynamicContentState> ValidateState(
        ValidatedDynamicContentDeclaration declaration, DynamicContentState state, DateTimeOffset now)
    {
        if (declaration is null || state?.Activities is null || state.Items is null)
            return Failure<ValidatedDynamicContentState>("dynamic_structure_invalid", "动态状态缺失", "dynamicContent");
        if (state.Activities.Count > DynamicContentLimits.MaximumActivitiesPerApplication ||
            state.Items.Count > DynamicContentLimits.MaximumItemsPerApplication)
            return Failure<ValidatedDynamicContentState>("dynamic_budget_exceeded", "动态活动或项数量超限", "dynamicContent");
        if (declaration.Declaration.Kind == DynamicContentKind.OrdinaryItems && state.Activities.Count != 0)
            return Failure<ValidatedDynamicContentState>("dynamic_reference_invalid", "普通动态项不携带活动", "dynamicContent.activities");
        var activities = new Dictionary<string, ActivityState>(StringComparer.Ordinal);
        foreach (var activity in state.Activities)
        {
            if (activity is null || !ValidId(activity.ActivityId))
                return Failure<ValidatedDynamicContentState>("dynamic_identity_invalid", "活动标识无效", "dynamicContent.activities");
            if (!activities.TryAdd(activity.ActivityId, activity))
                return Failure<ValidatedDynamicContentState>("dynamic_duplicate_identity", "活动标识重复", "dynamicContent.activities");
            if ((!activity.Ended && activity.ExpiresAt <= now) || activity.ExpiresAt - now > DynamicContentLimits.MaximumRetention)
                return Failure<ValidatedDynamicContentState>("dynamic_expiry_invalid", "活动有效期必须处于本次允许保留范围", "dynamicContent.activities.expiresAt");
            if (activity.Order < 0)
                return Failure<ValidatedDynamicContentState>("dynamic_fields_invalid", "活动排序不能为负", "dynamicContent.activities.order");
        }
        var structures = declaration.Declaration.Structures.ToDictionary(item => item.StructureId, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<DynamicItemState>();
        foreach (var item in state.Items)
        {
            if (item is null || !ValidId(item.ItemId) || !ValidId(item.StructureId))
                return Failure<ValidatedDynamicContentState>("dynamic_identity_invalid", "内容项标识无效", "dynamicContent.items");
            if (!ids.Add(item.ItemId))
                return Failure<ValidatedDynamicContentState>("dynamic_duplicate_identity", "内容项标识重复", "dynamicContent.items");
            if (!structures.TryGetValue(item.StructureId, out var structure))
                return Failure<ValidatedDynamicContentState>("dynamic_structure_invalid", "内容项结构未声明", "dynamicContent.items.structureId");
            if (!structure.IsRepeated && item.ItemId != structure.StructureId)
                return Failure<ValidatedDynamicContentState>("dynamic_identity_invalid", "固定项必须使用声明标识", "dynamicContent.items.itemId");
            if (item.ActivityIds is null)
                return Failure<ValidatedDynamicContentState>("dynamic_reference_invalid", "内容项活动引用缺失", "dynamicContent.items.activityIds");
            if (item.ActivityIds.Count > DynamicContentLimits.MaximumActivityReferencesPerItem)
                return Failure<ValidatedDynamicContentState>("dynamic_budget_exceeded", "内容项活动引用数量超限", "dynamicContent.items.activityIds");
            if (declaration.Declaration.Kind == DynamicContentKind.LiveIsland ? item.ActivityIds.Count == 0 : item.ActivityIds.Count != 0)
                return Failure<ValidatedDynamicContentState>("dynamic_reference_invalid", "内容项活动引用不符合入口种类", "dynamicContent.items.activityIds");
            var references = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in item.ActivityIds)
                if (!ValidId(reference) || !references.Add(reference) || !activities.TryGetValue(reference, out var activity) || activity.Ended)
                    return Failure<ValidatedDynamicContentState>("dynamic_reference_invalid", "内容项引用不存在、已结束或重复活动", "dynamicContent.items.activityIds");
            if (!ValidFields(item.Fields, structure.Normal.Fields | (structure.Expanded?.Fields ?? ContentFields.None)))
                return Failure<ValidatedDynamicContentState>("dynamic_fields_invalid", "字段必须匹配预置呈现并具有有效读数", "dynamicContent.items.fields");
            items.Add(item with { ActivityIds = Array.AsReadOnly(item.ActivityIds.ToArray()) });
        }
        return CoreResult<ValidatedDynamicContentState>.Success(new(new(
            Array.AsReadOnly(state.Activities.ToArray()), Array.AsReadOnly(items.ToArray()))));
    }

    private static CoreResult<T> Failure<T>(string code, string message, string path) =>
        CoreResult<T>.Failure(new(code, message, path));

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) &&
        id.Length <= DeclarationValidator.MaximumIdLength && id == id.Trim();

    private static bool ValidPresentation(ItemPresentation? presentation)
    {
        if (presentation?.Width is null || !Enum.IsDefined(presentation.Template)) return false;
        var width = presentation.Width;
        if (width.Tier is { } tier ? !Enum.IsDefined(tier) || width.Slots is not null :
            width.Slots is not (>= 1 and <= DynamicContentLimits.MaximumSlots)) return false;
        var fields = presentation.Fields;
        const ContentFields all = ContentFields.Timer | ContentFields.Progress | ContentFields.Counter | ContentFields.Status;
        if (fields == ContentFields.None || (fields & ~all) != 0) return false;
        return presentation.Template switch
        {
            PresetTemplate.Timer => fields == ContentFields.Timer,
            PresetTemplate.Progress => fields == ContentFields.Progress,
            PresetTemplate.Counter => fields == ContentFields.Counter,
            PresetTemplate.Status => fields == ContentFields.Status,
            PresetTemplate.Composite => ((int)fields & ((int)fields - 1)) != 0,
            _ => false
        };
    }

    private static bool ValidFields(DynamicItemFields? fields, ContentFields required)
    {
        if (fields is null) return false;
        var present = (fields.Timer is null ? ContentFields.None : ContentFields.Timer) |
            (fields.Progress is null ? ContentFields.None : ContentFields.Progress) |
            (fields.Counter is null ? ContentFields.None : ContentFields.Counter) |
            (fields.Status is null ? ContentFields.None : ContentFields.Status);
        if (present != required) return false;
        if (fields.Timer is { } timer &&
            (!Enum.IsDefined(timer.Direction) || timer.ReferenceUtc == default ||
             !double.IsFinite(timer.ValueMillisecondsAtReference) ||
             Math.Abs(timer.ValueMillisecondsAtReference) > TimeSpan.MaxValue.TotalMilliseconds ||
             (timer.ValueMillisecondsAtReference < 0 && !(timer.Direction == TimerDirection.CountDown && timer.ShowOvertime))))
            return false;
        if (fields.Progress is { } progress)
        {
            if (!Enum.IsDefined(progress.Mode)) return false;
            if (progress.Mode == ProgressMode.Indeterminate)
            {
                if (progress.Value is not null || progress.Maximum is not null) return false;
            }
            else if (progress.Value is not { } value || progress.Maximum is not { } maximum ||
                !double.IsFinite(value) || !double.IsFinite(maximum) || maximum <= 0 || value < 0 || value > maximum)
                return false;
        }
        if (fields.Counter is { } counter &&
            (!Enum.IsDefined(counter.Semantics) || counter.Value < 0 ||
             (counter.Total is { } total && (total < 0 || counter.Value > total))))
            return false;
        if (fields.Status is { } status &&
            (status.Text is null || status.Text.Length > ProtocolLimits.MaximumTextLength || !Enum.IsDefined(status.Marker)))
            return false;
        return true;
    }
}

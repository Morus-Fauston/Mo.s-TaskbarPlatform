using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// Validates the minimum declaration contract as one atomic unit.
/// </summary>
public sealed class DeclarationValidator
{
    public const int MaximumJsonSizeInBytes = 1024 * 1024;
    public const int MaximumIdLength = 256;
    public const int MaximumFeatureGroups = 64;
    public const int MaximumEntriesPerKind = 128;
    public const int MaximumActionSlots = 32;
    public const int MaximumNodes = 4096;

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public CoreResult<ValidatedApplicationDeclaration> Validate(ApplicationDeclaration? declaration)
    {
        if (declaration is null)
        {
            return Failure("declaration_required", "An application declaration is required.", "declaration");
        }

        if (!TryCreateId(declaration.ApplicationId, "applicationId", "application", out var applicationId, out var idError))
        {
            return CoreResult<ValidatedApplicationDeclaration>.Failure(idError!);
        }

        if (declaration.FeatureGroups is null || declaration.FeatureGroups.Count == 0)
        {
            return Failure("required_entry_missing", "At least one feature group is required.", "featureGroups");
        }

        var remainingNodes = MaximumNodes - 1;
        var images = declaration.Images ?? [];
        if (!ConsumeNodes(images.Count, TemplateLimits.ImagesPerApplication, ref remainingNodes)) return BudgetFailure("images");
        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            if (image is null || !Enum.IsDefined(image.Format)) return Failure("template_resource_invalid", "Image registration is invalid.", "images");
            if (!TryCreateId(image.ResourceId, "images.resourceId", "image resource", out _, out idError))
                return CoreResult<ValidatedApplicationDeclaration>.Failure(idError!);
            if (!imageIds.Add(image.ResourceId)) return Failure("duplicate_id", "Image resource IDs must be unique.", "images");
        }
        if (!ConsumeNodes(declaration.FeatureGroups.Count, MaximumFeatureGroups, ref remainingNodes))
        {
            return BudgetFailure("featureGroups");
        }

        var applicationIdentity = new StableIdentity(applicationId);
        var featureGroups = new List<ValidatedFeatureGroup>(declaration.FeatureGroups.Count);
        var featureGroupIds = new HashSet<StableId>();
        var dynamicContents = new List<ValidatedDynamicContentDeclaration>();
        var dynamicValidator = new DynamicContentValidator();
        var flyoutEntries = new List<ValidatedFlyoutEntry>();
        var declaredActions = new List<ValidatedActionSlot>();
        var liveIslandEntries = 0;
        var templates = new List<ValidatedEntryTemplate>();
        var templateValidator = new TemplateValidator();

        for (var featureIndex = 0; featureIndex < declaration.FeatureGroups.Count; featureIndex++)
        {
            var feature = declaration.FeatureGroups[featureIndex];
            var featurePath = $"featureGroups[{featureIndex}]";
            if (feature is null)
            {
                return Failure("unsupported_structure", "A feature group entry cannot be null.", featurePath);
            }

            if (!TryCreateId(feature.FeatureGroupId, $"{featurePath}.featureGroupId", "feature group", out var featureGroupId, out idError))
            {
                return CoreResult<ValidatedApplicationDeclaration>.Failure(idError!);
            }

            if (!featureGroupIds.Add(featureGroupId))
            {
                return Failure("duplicate_id", "Feature group IDs must be unique within an application.", $"{featurePath}.featureGroupId");
            }

            if (feature.Components is null || feature.Components.Count == 0 ||
                feature.TaskbarFlyouts is null || feature.TaskbarFlyouts.Count == 0)
            {
                return Failure(
                    "required_entry_missing",
                    "A minimum feature group requires at least one component and one taskbar-operation flyout.",
                    featurePath);
            }

            if (!ConsumeNodes(feature.Components.Count, MaximumEntriesPerKind, ref remainingNodes) ||
                !ConsumeNodes(feature.TaskbarFlyouts.Count, MaximumEntriesPerKind, ref remainingNodes))
            {
                return BudgetFailure(featurePath);
            }

            var featureIdentity = applicationIdentity.CreateChild(featureGroupId);
            var components = new List<Component>(feature.Components.Count);
            var entryIds = new HashSet<StableId>();

            for (var componentIndex = 0; componentIndex < feature.Components.Count; componentIndex++)
            {
                var component = feature.Components[componentIndex];
                var componentPath = $"{featurePath}.components[{componentIndex}]";
                if (component is null)
                {
                    return Failure("unsupported_structure", "A component entry cannot be null.", componentPath);
                }

                if (!TryCreateId(component.ComponentId, $"{componentPath}.componentId", "component", out var componentId, out idError))
                {
                    return CoreResult<ValidatedApplicationDeclaration>.Failure(idError!);
                }

                if (!entryIds.Add(componentId))
                {
                    return Failure("duplicate_id", "Component IDs must be unique within a feature group.", $"{componentPath}.componentId");
                }

                if (!TryValidateActionSlots(
                    component.ActionSlots,
                    featureIdentity.CreateChild(componentId),
                    $"{componentPath}.actionSlots",
                    ref remainingNodes,
                    out var actionSlots,
                    out var actionError))
                {
                    return CoreResult<ValidatedApplicationDeclaration>.Failure(actionError!);
                }

                components.Add(new Component(
                    featureIdentity.CreateChild(componentId),
                    CapabilityState.Available,
                    actionSlots));
                foreach (var slot in component.ActionSlots!)
                    declaredActions.Add(new ValidatedActionSlot(new(declaration.ApplicationId!, feature.FeatureGroupId!,
                        ActionEntryKind.Component, component.ComponentId!, slot.ActionSlotId!), slot.ParameterKind));
                if (component.Template is not null)
                {
                    if (component.DynamicContent is not null)
                        return Failure("template_structure_invalid", "Component cannot declare both a template and dynamic content.", componentPath);
                    var templateResult = templateValidator.Validate(new(feature.FeatureGroupId!, TemplateEntryKind.Component, component.ComponentId!),
                        component.Template, component.ActionSlots!, declaration.Images ?? [], ref remainingNodes);
                    if (!templateResult.IsSuccess) return CoreResult<ValidatedApplicationDeclaration>.Failure(templateResult.Error!);
                    templates.Add(templateResult.Value!);
                }
                if (component.DynamicContent is not null)
                {
                    if (component.DynamicContent.Kind == DynamicContentKind.LiveIsland && ++liveIslandEntries > DisplayPermissionLimits.MaximumEntriesPerApplication)
                        return BudgetFailure(componentPath);
                    var dynamicResult = dynamicValidator.ValidateDeclaration(component.DynamicContent,
                        featureIdentity.CreateChild(componentId), ref remainingNodes);
                    if (!dynamicResult.IsSuccess)
                        return CoreResult<ValidatedApplicationDeclaration>.Failure(dynamicResult.Error!);
                    foreach (var structure in dynamicResult.Value!.Declaration.Structures)
                    {
                        var bindings = (structure.ControlActivations ?? []).Select(value => value.Binding)
                            .Prepend(structure.PrimaryActivation);
                        foreach (var binding in bindings)
                        {
                            if (binding?.Kind == ItemActivationKind.BusinessAction &&
                                !component.ActionSlots!.Any(slot => slot.ActionSlotId == binding.TargetId) ||
                                binding?.Kind == ItemActivationKind.TaskbarFlyout &&
                                !feature.TaskbarFlyouts.Any(flyout => flyout?.TaskbarFlyoutId == binding.TargetId))
                                return Failure("dynamic_reference_invalid", "项激活引用必须指向本组件动作或同组任务栏面板。", componentPath + ".dynamicContent");
                        }
                    }
                    dynamicContents.Add(dynamicResult.Value!);
                }
            }

            var taskbarFlyouts = new List<ValidatedTaskbarFlyout>(feature.TaskbarFlyouts.Count);
            for (var flyoutIndex = 0; flyoutIndex < feature.TaskbarFlyouts.Count; flyoutIndex++)
            {
                var flyout = feature.TaskbarFlyouts[flyoutIndex];
                var flyoutPath = $"{featurePath}.taskbarFlyouts[{flyoutIndex}]";
                if (flyout is null)
                {
                    return Failure("unsupported_structure", "A taskbar-operation flyout entry cannot be null.", flyoutPath);
                }

                if (!TryCreateId(flyout.TaskbarFlyoutId, $"{flyoutPath}.taskbarFlyoutId", "taskbar-operation flyout", out var flyoutId, out idError))
                {
                    return CoreResult<ValidatedApplicationDeclaration>.Failure(idError!);
                }

                if (!entryIds.Add(flyoutId))
                {
                    return Failure("hierarchy_conflict", "Component and taskbar-operation flyout IDs cannot collide within a feature group.", $"{flyoutPath}.taskbarFlyoutId");
                }

                var flyoutIdentity = featureIdentity.CreateChild(flyoutId);
                if (!TryValidateActionSlots(
                    flyout.ActionSlots,
                    flyoutIdentity,
                    $"{flyoutPath}.actionSlots",
                    ref remainingNodes,
                    out var actionSlots,
                    out var actionError))
                {
                    return CoreResult<ValidatedApplicationDeclaration>.Failure(actionError!);
                }

                taskbarFlyouts.Add(new ValidatedTaskbarFlyout(flyoutIdentity, actionSlots));
                foreach (var slot in flyout.ActionSlots!)
                    declaredActions.Add(new ValidatedActionSlot(new(declaration.ApplicationId!, feature.FeatureGroupId!,
                        ActionEntryKind.TaskbarFlyout, flyout.TaskbarFlyoutId!, slot.ActionSlotId!), slot.ParameterKind));
                flyoutEntries.Add(new ValidatedFlyoutEntry(flyoutIdentity, FlyoutKind.TaskbarGroup));
                if (flyout.Template is not null)
                {
                    var templateResult = templateValidator.Validate(new(feature.FeatureGroupId!, TemplateEntryKind.TaskbarFlyout, flyout.TaskbarFlyoutId!),
                        flyout.Template, flyout.ActionSlots!, images, ref remainingNodes);
                    if (!templateResult.IsSuccess) return CoreResult<ValidatedApplicationDeclaration>.Failure(templateResult.Error!);
                    templates.Add(templateResult.Value!);
                }
            }

            var hints = feature.Hints ?? [];
            var events = feature.EventChannels ?? [];
            if (!ConsumeNodes(hints.Count, MaximumEntriesPerKind, ref remainingNodes) ||
                !ConsumeNodes(events.Count, MaximumEntriesPerKind, ref remainingNodes))
                return BudgetFailure(featurePath);
            foreach (var hint in hints)
            {
                if (hint is null || hint.Kind is not (FlyoutKind.ShortHint or FlyoutKind.InteractiveHint))
                    return Failure("unsupported_structure", "Hint type must be ordinary or interactive.", featurePath + ".hints");
                if (!TryCreateId(hint.EntryId, featurePath + ".hints", "hint", out var hintId, out idError))
                    return CoreResult<ValidatedApplicationDeclaration>.Failure(idError!);
                if (!entryIds.Add(hintId))
                    return Failure("hierarchy_conflict", "Entry identities must be unique within the feature group.", featurePath + ".hints");
                if (hint.Kind == FlyoutKind.ShortHint && (hint.ActionSlots is { Count: > 0 } || hint.Expansion is not null))
                    return Failure("unsupported_structure", "Ordinary hints cannot declare actions or expansion.", featurePath + ".hints");
                if (hint.ActionSlots is { Count: > 0 })
                {
                    if (!TryValidateActionSlots(hint.ActionSlots, featureIdentity.CreateChild(hintId), featurePath + ".hints.actionSlots",
                        ref remainingNodes, out _, out var actionError))
                        return CoreResult<ValidatedApplicationDeclaration>.Failure(actionError!);
                    foreach (var slot in hint.ActionSlots)
                        declaredActions.Add(new(new(declaration.ApplicationId!, feature.FeatureGroupId!, ActionEntryKind.Hint,
                            hint.EntryId!, slot.ActionSlotId!), slot.ParameterKind));
                }
                if (hint.Expansion is { } expansion)
                {
                    if (!ConsumeNodes(1, 1, ref remainingNodes)) return BudgetFailure(featurePath + ".hints.expansion");
                    if (!TryCreateId(expansion.TaskbarFlyoutId, featurePath + ".hints.expansion", "expansion entry", out _, out idError) ||
                        (expansion.PanelTemplateId is not null && !TryCreateId(expansion.PanelTemplateId,
                            featurePath + ".hints.expansion", "expansion panel", out _, out idError)))
                        return CoreResult<ValidatedApplicationDeclaration>.Failure(idError!);
                    var target = templates.FirstOrDefault(value => value.Entry ==
                        new TemplateEntryReference(feature.FeatureGroupId!, TemplateEntryKind.TaskbarFlyout, expansion.TaskbarFlyoutId));
                    if (target is null || expansion.PanelTemplateId is { } panel &&
                        target.Declaration.Panels?.Any(value => value.TemplateId == panel) != true)
                        return Failure("hint_expansion_invalid", "Hint expansion must reference a declared taskbar template and optional associated panel in the same feature group.",
                            featurePath + ".hints.expansion");
                }
                flyoutEntries.Add(new ValidatedFlyoutEntry(featureIdentity.CreateChild(hintId), hint.Kind, expansion: hint.Expansion));
                if (hint.Template is not null)
                {
                    var templateResult = templateValidator.Validate(new(feature.FeatureGroupId!, TemplateEntryKind.Hint, hint.EntryId!),
                        hint.Template, hint.ActionSlots ?? [], images, ref remainingNodes,
                        allowHintActions: hint.Kind == FlyoutKind.InteractiveHint, allowHintExpansion: hint.Expansion is not null);
                    if (!templateResult.IsSuccess) return CoreResult<ValidatedApplicationDeclaration>.Failure(templateResult.Error!);
                    templates.Add(templateResult.Value!);
                }
            }
            foreach (var channel in events)
            {
                if (channel?.ClosePolicy is null || !Enum.IsDefined(channel.ClosePolicy.Value))
                    return Failure("unsupported_structure", "Event channel close policy is required.", featurePath + ".eventChannels");
                if (!TryCreateId(channel.ChannelId, featurePath + ".eventChannels", "event channel", out var channelId, out idError))
                    return CoreResult<ValidatedApplicationDeclaration>.Failure(idError!);
                if (!entryIds.Add(channelId))
                    return Failure("hierarchy_conflict", "Entry identities must be unique within the feature group.", featurePath + ".eventChannels");
                if (channel.ActionSlots is { Count: > 0 })
                {
                    if (!TryValidateActionSlots(channel.ActionSlots, featureIdentity.CreateChild(channelId), featurePath + ".eventChannels.actionSlots",
                        ref remainingNodes, out _, out var actionError))
                        return CoreResult<ValidatedApplicationDeclaration>.Failure(actionError!);
                    foreach (var slot in channel.ActionSlots)
                        declaredActions.Add(new(new(declaration.ApplicationId!, feature.FeatureGroupId!, ActionEntryKind.EventChannel,
                            channel.ChannelId!, slot.ActionSlotId!), slot.ParameterKind));
                }
                flyoutEntries.Add(new ValidatedFlyoutEntry(featureIdentity.CreateChild(channelId), FlyoutKind.EventGroup, channel.ClosePolicy));
                if (channel.Template is not null)
                {
                    var templateResult = templateValidator.Validate(new(feature.FeatureGroupId!, TemplateEntryKind.EventChannel, channel.ChannelId!),
                        channel.Template, channel.ActionSlots ?? [], images, ref remainingNodes);
                    if (!templateResult.IsSuccess) return CoreResult<ValidatedApplicationDeclaration>.Failure(templateResult.Error!);
                    templates.Add(templateResult.Value!);
                }
            }

            featureGroups.Add(new ValidatedFeatureGroup(featureIdentity, components, taskbarFlyouts));
        }

        return CoreResult<ValidatedApplicationDeclaration>.Success(
            new ValidatedApplicationDeclaration(applicationIdentity, featureGroups, dynamicContents, flyoutEntries, declaredActions, templates, declaration.Images));
    }

    public CoreResult<ValidatedApplicationDeclaration> ValidateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failure("declaration_required", "Declaration JSON cannot be empty.", "json");
        }

        if (json.Length > MaximumJsonSizeInBytes || Encoding.UTF8.GetByteCount(json) > MaximumJsonSizeInBytes)
        {
            return Failure(
                "declaration_too_large",
                "The declaration JSON exceeds the 1 MiB limit.",
                "json");
        }

        try
        {
            var declaration = JsonSerializer.Deserialize<ApplicationDeclaration>(json, jsonOptions);
            return Validate(declaration);
        }
        catch (JsonException exception)
        {
            return Failure("unsupported_structure", "The declaration JSON structure is not supported.", exception.Path);
        }
        catch (NotSupportedException exception)
        {
            return Failure("unsupported_structure", "The declaration JSON structure is not supported.", exception.Message);
        }
    }

    private static bool TryCreateId(
        string? value,
        string path,
        string label,
        out StableId id,
        out StructuredError? error)
    {
        if (value?.Length > MaximumIdLength)
        {
            id = default;
            error = new StructuredError("declaration_budget_exceeded", "A declaration ID exceeds the length limit.", path);
            return false;
        }

        try
        {
            id = new StableId(value!);
            error = null;
            return true;
        }
        catch (ArgumentException)
        {
            id = default;
            error = new StructuredError("invalid_id", $"The {label} ID is missing or invalid.", path);
            return false;
        }
    }

    private static bool TryValidateActionSlots(
        IReadOnlyList<ActionSlotDeclaration>? declarations,
        StableIdentity parent,
        string path,
        ref int remainingNodes,
        out IReadOnlyList<ActionSlot> actionSlots,
        out StructuredError? error)
    {
        if (declarations is null || declarations.Count == 0)
        {
            actionSlots = Array.Empty<ActionSlot>();
            error = new StructuredError("required_entry_missing", "At least one action slot is required.", path);
            return false;
        }

        if (!ConsumeNodes(declarations.Count, MaximumActionSlots, ref remainingNodes))
        {
            actionSlots = Array.Empty<ActionSlot>();
            error = BudgetFailure(path).Error;
            return false;
        }

        var slots = new List<ActionSlot>(declarations.Count);
        var ids = new HashSet<StableId>();
        for (var index = 0; index < declarations.Count; index++)
        {
            var declaration = declarations[index];
            var slotPath = $"{path}[{index}]";
            if (declaration is null)
            {
                actionSlots = Array.Empty<ActionSlot>();
                error = new StructuredError("unsupported_structure", "An action slot entry cannot be null.", slotPath);
                return false;
            }
            if (!Enum.IsDefined(declaration.ParameterKind))
            {
                actionSlots = Array.Empty<ActionSlot>();
                error = new StructuredError("unsupported_parameter", "The action parameter kind is not supported.", slotPath);
                return false;
            }

            if (!TryCreateId(declaration.ActionSlotId, $"{slotPath}.actionSlotId", "action slot", out var actionId, out error))
            {
                actionSlots = Array.Empty<ActionSlot>();
                return false;
            }

            if (!ids.Add(actionId))
            {
                actionSlots = Array.Empty<ActionSlot>();
                error = new StructuredError("duplicate_id", "Action slot IDs must be unique within an entry.", $"{slotPath}.actionSlotId");
                return false;
            }

            slots.Add(new ActionSlot(parent.CreateChild(actionId)));
        }

        actionSlots = slots;
        error = null;
        return true;
    }

    private static bool ConsumeNodes(int count, int collectionLimit, ref int remainingNodes)
    {
        if (count < 0 || count > collectionLimit || count > remainingNodes)
        {
            return false;
        }

        remainingNodes -= count;
        return true;
    }

    private static CoreResult<ValidatedApplicationDeclaration> BudgetFailure(string path) =>
        Failure("declaration_budget_exceeded", "The declaration exceeds its collection or total node budget.", path);

    private static CoreResult<ValidatedApplicationDeclaration> Failure(string code, string message, string? path) =>
        CoreResult<ValidatedApplicationDeclaration>.Failure(new StructuredError(code, message, path));
}

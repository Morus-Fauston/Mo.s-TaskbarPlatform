using System;
using System.Collections.Generic;
using System.Linq;
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

        var applicationIdentity = new StableIdentity(applicationId);
        var featureGroups = new List<ValidatedFeatureGroup>(declaration.FeatureGroups.Count);
        var featureGroupIds = new HashSet<StableId>();

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
                    out var actionSlots,
                    out var actionError))
                {
                    return CoreResult<ValidatedApplicationDeclaration>.Failure(actionError!);
                }

                components.Add(new Component(
                    featureIdentity.CreateChild(componentId),
                    CapabilityState.Available,
                    actionSlots));
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
                    out var actionSlots,
                    out var actionError))
                {
                    return CoreResult<ValidatedApplicationDeclaration>.Failure(actionError!);
                }

                taskbarFlyouts.Add(new ValidatedTaskbarFlyout(flyoutIdentity, actionSlots));
            }

            featureGroups.Add(new ValidatedFeatureGroup(featureIdentity, components, taskbarFlyouts));
        }

        return CoreResult<ValidatedApplicationDeclaration>.Success(
            new ValidatedApplicationDeclaration(applicationIdentity, featureGroups));
    }

    public CoreResult<ValidatedApplicationDeclaration> ValidateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failure("declaration_required", "Declaration JSON cannot be empty.", "json");
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
        out IReadOnlyList<ActionSlot> actionSlots,
        out StructuredError? error)
    {
        if (declarations is null || declarations.Count == 0)
        {
            actionSlots = Array.Empty<ActionSlot>();
            error = new StructuredError("required_entry_missing", "At least one action slot is required.", path);
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

    private static CoreResult<ValidatedApplicationDeclaration> Failure(string code, string message, string? path) =>
        CoreResult<ValidatedApplicationDeclaration>.Failure(new StructuredError(code, message, path));
}

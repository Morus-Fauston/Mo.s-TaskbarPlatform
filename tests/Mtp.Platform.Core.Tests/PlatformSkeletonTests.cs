using Mtp.Contracts;
using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class PlatformSkeletonTests
{
    [Fact]
    public void CoreAssemblyHasNoWindowsOrStorageDependencies()
    {
        var referencedAssemblyNames = typeof(PlatformCoreMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();

        var forbiddenPrefixes = new[]
        {
            "Microsoft.UI",
            "Microsoft.WindowsAppSDK",
            "Microsoft.Data.Sqlite",
            "System.Data.SQLite",
            "System.IO.Pipes",
            "WindowsBase",
        };

        Assert.DoesNotContain(
            referencedAssemblyNames,
            name => forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CoreAndContractsDoNotDependOnHostOrEachOther()
    {
        var coreReferences = typeof(PlatformCoreMarker).Assembly.GetReferencedAssemblies();
        var contractReferences = typeof(ContractAssemblyMarker).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(coreReferences, reference => reference.Name is "Mtp.Host" or "Mtp.Contracts");
        Assert.DoesNotContain(contractReferences, reference => reference.Name is "Mtp.Host" or "Mtp.Platform.Core");
    }

    [Fact]
    public void StableIdentityKeepsParentChainAndDistinguishesSiblingContexts()
    {
        var application = new StableIdentity(new StableId("music"));
        var firstFeature = application.CreateChild(new StableId("controls"));
        var secondFeature = new StableIdentity(new StableId("podcast")).CreateChild(new StableId("controls"));

        Assert.Equal(new[] { "music", "controls" }, firstFeature.Segments.Select(segment => segment.Value));
        Assert.NotEqual(firstFeature, secondFeature);
        Assert.NotEqual(firstFeature, application.CreateChild(new StableId("Controls")));
    }

    [Fact]
    public void StableIdRejectsMissingOrTrimmedValues()
    {
        Assert.Throws<ArgumentException>(() => new StableId(""));
        Assert.Throws<ArgumentException>(() => new StableId(" controls "));
    }

    [Fact]
    public void ComponentAndActionSlotUseTheComponentHierarchy()
    {
        var componentIdentity = new StableIdentity(new StableId("music"))
            .CreateChild(new StableId("controls"))
            .CreateChild(new StableId("widget"));
        var actionIdentity = componentIdentity.CreateChild(new StableId("toggle"));
        var component = new Component(
            componentIdentity,
            CapabilityState.Available,
            new[] { new ActionSlot(actionIdentity) });

        Assert.Single(component.ActionSlots);
        Assert.Equal(componentIdentity, component.ActionSlots[0].Identity.Parent);
    }

    [Fact]
    public void ComponentStateStoreAcceptsNewerStateAndRejectsStaleOverwrite()
    {
        var component = new StableIdentity(new StableId("music"))
            .CreateChild(new StableId("controls"))
            .CreateChild(new StableId("widget"));
        var store = new ComponentStateStore();

        Assert.True(store.TryApply(new ComponentStateSnapshot(component, CapabilityState.Available, 1)));
        Assert.True(store.TryApply(new ComponentStateSnapshot(component, CapabilityState.Unavailable("service offline"), 2)));
        Assert.False(store.TryApply(new ComponentStateSnapshot(component, CapabilityState.Available, 1)));

        Assert.True(store.TryGet(component, out var current));
        Assert.Equal(2, current.Revision);
        Assert.Equal(CapabilityStatus.Unavailable, current.Capability.Status);
        Assert.Equal("service offline", current.Capability.Reason);
    }

    [Fact]
    public void StructuredResultCarriesEitherValueOrStableError()
    {
        var success = CoreResult<string>.Success("accepted");
        var failure = CoreResult<string>.Failure(new StructuredError("invalid_declaration", "Declaration is invalid."));

        Assert.True(success.IsSuccess);
        Assert.Equal("accepted", success.Value);
        Assert.Null(success.Error);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Value);
        Assert.Equal("invalid_declaration", failure.Error!.Code);
        Assert.Equal("Declaration is invalid.", failure.Error.Message);
    }

    [Fact]
    public void CapabilityStateExposesAllDocumentedStatuses()
    {
        var statuses = Enum.GetValues<CapabilityStatus>();

        Assert.Equal(
            new[]
            {
                CapabilityStatus.Available,
                CapabilityStatus.NotApplicable,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unavailable,
                CapabilityStatus.Recovering,
                CapabilityStatus.Failed,
            },
            statuses);
        Assert.Equal("service offline", CapabilityState.Unavailable("service offline").Reason);
    }
}

public sealed class DeclarationValidationTests
{
    [Fact]
    public void ValidDeclarationReturnsACompleteHostSnapshot()
    {
        var result = new DeclarationValidator().Validate(CreateValidDeclaration());

        Assert.True(result.IsSuccess);
        var snapshot = Assert.IsType<ValidatedApplicationDeclaration>(result.Value);
        Assert.Equal("music", snapshot.Identity.LocalId.Value);
        var feature = Assert.Single(snapshot.FeatureGroups);
        Assert.Equal("controls", feature.Identity.LocalId.Value);
        Assert.Single(feature.Components);
        Assert.Single(feature.TaskbarFlyouts);
        Assert.Equal("widget", feature.Components[0].Identity.LocalId.Value);
        Assert.Equal("panel", feature.TaskbarFlyouts[0].Identity.LocalId.Value);
    }

    [Fact]
    public void ValidJsonDeclarationIsParsedIntoTheSameCompleteSnapshot()
    {
        var json = """
        {
          "applicationId": "music",
          "featureGroups": [
            {
              "featureGroupId": "controls",
              "components": [
                { "componentId": "widget", "actionSlots": [{ "actionSlotId": "increment" }] }
              ],
              "taskbarFlyouts": [
                { "taskbarFlyoutId": "panel", "actionSlots": [{ "actionSlotId": "increment" }] }
              ]
            }
          ]
        }
        """;

        var result = new DeclarationValidator().ValidateJson(json);

        Assert.True(result.IsSuccess);
        Assert.Equal("music", result.Value!.Identity.LocalId.Value);
        Assert.Single(result.Value.FeatureGroups[0].Components);
        Assert.Single(result.Value.FeatureGroups[0].TaskbarFlyouts);
    }

    [Fact]
    public void DuplicateFeatureGroupIdsAreRejectedAsStructuredErrors()
    {
        var declaration = new ApplicationDeclaration(
            "music",
            new[]
            {
                CreateValidDeclaration().FeatureGroups![0],
                CreateValidDeclaration("music", "controls-2").FeatureGroups![0] with { FeatureGroupId = "controls" },
            });

        var result = new DeclarationValidator().Validate(declaration);

        Assert.False(result.IsSuccess);
        Assert.Equal("duplicate_id", result.Error!.Code);
        Assert.Contains("feature group", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateChildIdsAndHierarchyConflictsAreRejected()
    {
        var baseDeclaration = CreateValidDeclaration();
        var baseFeature = baseDeclaration.FeatureGroups![0];
        var duplicateComponents = baseFeature with
        {
            Components = new[]
            {
                baseFeature.Components![0],
                baseFeature.Components[0] with { ActionSlots = new[] { new ActionSlotDeclaration("other") } },
            },
        };
        var duplicateComponentDeclaration = baseDeclaration with { FeatureGroups = new[] { duplicateComponents } };

        var duplicateResult = new DeclarationValidator().Validate(duplicateComponentDeclaration);
        Assert.False(duplicateResult.IsSuccess);
        Assert.Equal("duplicate_id", duplicateResult.Error!.Code);

        var conflictFeature = baseFeature with
        {
            TaskbarFlyouts = new[]
            {
                baseFeature.TaskbarFlyouts![0] with { TaskbarFlyoutId = "widget" },
            },
        };
        var conflictResult = new DeclarationValidator().Validate(baseDeclaration with { FeatureGroups = new[] { conflictFeature } });

        Assert.False(conflictResult.IsSuccess);
        Assert.Equal("hierarchy_conflict", conflictResult.Error!.Code);
    }

    [Fact]
    public void MissingRequiredEntryAndActionAreRejected()
    {
        var valid = CreateValidDeclaration();
        var feature = valid.FeatureGroups![0] with
        {
            TaskbarFlyouts = Array.Empty<TaskbarFlyoutDeclaration>(),
            Components = new[]
            {
                valid.FeatureGroups[0].Components![0] with { ActionSlots = Array.Empty<ActionSlotDeclaration>() },
            },
        };

        var result = new DeclarationValidator().Validate(valid with { FeatureGroups = new[] { feature } });

        Assert.False(result.IsSuccess);
        Assert.Equal("required_entry_missing", result.Error!.Code);
    }

    [Fact]
    public void DuplicateActionSlotIdsAreRejectedWithinAnEntry()
    {
        var valid = CreateValidDeclaration();
        var component = valid.FeatureGroups![0].Components![0] with
        {
            ActionSlots = new[]
            {
                new ActionSlotDeclaration("increment"),
                new ActionSlotDeclaration("increment"),
            },
        };
        var feature = valid.FeatureGroups[0] with { Components = new[] { component } };

        var result = new DeclarationValidator().Validate(valid with { FeatureGroups = new[] { feature } });

        Assert.False(result.IsSuccess);
        Assert.Equal("duplicate_id", result.Error!.Code);
        Assert.Contains("action slot", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedJsonMembersAreRejectedWithoutDeserializingPartially()
    {
        var json = """
        {
          "applicationId": "music",
          "featureGroups": [],
          "unsupportedField": true
        }
        """;

        var result = new DeclarationValidator().ValidateJson(json);

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported_structure", result.Error!.Code);
    }

    [Fact]
    public void InvalidDeclarationDoesNotReplaceTheLastAcceptedSnapshot()
    {
        var store = new DeclarationSnapshotStore();
        var accepted = store.Submit(CreateValidDeclaration());
        var previous = store.Current;
        var invalid = CreateValidDeclaration() with
        {
            FeatureGroups = new[]
            {
                CreateValidDeclaration().FeatureGroups![0] with
                {
                    Components = new[]
                    {
                        CreateValidDeclaration().FeatureGroups![0].Components![0],
                        CreateValidDeclaration().FeatureGroups![0].Components![0],
                    },
                },
            },
        };

        var rejected = store.Submit(invalid);

        Assert.True(accepted.IsSuccess);
        Assert.False(rejected.IsSuccess);
        Assert.Same(previous, store.Current);
    }

    private static ApplicationDeclaration CreateValidDeclaration(
        string applicationId = "music",
        string featureGroupId = "controls") =>
        new(
            applicationId,
            new[]
            {
                new FeatureGroupDeclaration(
                    featureGroupId,
                    new[]
                    {
                        new ComponentDeclaration(
                            "widget",
                            new[] { new ActionSlotDeclaration("increment") }),
                    },
                    new[]
                    {
                        new TaskbarFlyoutDeclaration(
                            "panel",
                            new[] { new ActionSlotDeclaration("increment") }),
                    }),
            });
}

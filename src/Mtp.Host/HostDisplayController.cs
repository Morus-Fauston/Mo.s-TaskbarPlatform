using System;
using System.Collections.Generic;
using System.Linq;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// Combines the current validated declaration with Host-owned display preferences.
/// </summary>
public sealed record HostDisplayLoadResult(
    bool Accepted,
    ValidatedApplicationDeclaration? Declaration,
    IReadOnlyList<HostComponentDisplayModel> Components,
    StructuredError? DeclarationError,
    StructuredError? PreferenceError);

public sealed class HostDisplayController
{
    private readonly HostDeclarationLoader declarationLoader;
    private readonly IComponentDisplayPreferenceStore preferenceStore;
    private ComponentDisplayPreferences preferences = new();
    private IReadOnlyList<HostComponentDisplayModel> components = Array.Empty<HostComponentDisplayModel>();
    private bool preferencesLoaded;

    public HostDisplayController(
        IDeclarationSource declarationSource,
        IComponentDisplayPreferenceStore preferenceStore)
    {
        declarationLoader = new HostDeclarationLoader(declarationSource);
        this.preferenceStore = preferenceStore ?? throw new ArgumentNullException(nameof(preferenceStore));
    }

    public IReadOnlyList<HostComponentDisplayModel> CurrentComponents => components;

    public HostDisplayLoadResult Load()
    {
        var declarationResult = declarationLoader.Load();
        if (declarationResult.Current is null)
        {
            return new HostDisplayLoadResult(
                false,
                null,
                components,
                declarationResult.Error,
                null);
        }

        var preferenceResult = preferenceStore.Load();
        preferences = preferenceResult.Preferences;
        preferencesLoaded = true;
        components = BuildComponents(declarationResult.Current);

        return new HostDisplayLoadResult(
            declarationResult.Accepted,
            declarationResult.Current,
            components,
            declarationResult.Error,
            preferenceResult.Error);
    }

    public CoreResult<HostComponentDisplayModel> SetVisibility(StableIdentity identity, bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var declaredComponent = declarationLoader.SnapshotStore.Current?
            .FeatureGroups
            .SelectMany(featureGroup => featureGroup.Components)
            .FirstOrDefault(component => component.Identity == identity);
        if (declaredComponent is null)
        {
            return CoreResult<HostComponentDisplayModel>.Failure(
                new StructuredError("component_not_declared", "The component is not present in the current declaration.", identity.ToString()));
        }

        if (!preferencesLoaded)
        {
            var preferenceResult = preferenceStore.Load();
            preferences = preferenceResult.Preferences;
            preferencesLoaded = true;
        }

        var previous = preferences.IsVisible(identity);
        preferences.Set(identity, isVisible);
        var saveResult = preferenceStore.Save(preferences);
        if (!saveResult.IsSuccess)
        {
            preferences.Set(identity, previous);
            return CoreResult<HostComponentDisplayModel>.Failure(saveResult.Error!);
        }

        components = BuildComponents(declarationLoader.SnapshotStore.Current!);
        return CoreResult<HostComponentDisplayModel>.Success(
            components.First(component => component.Identity == identity));
    }

    private IReadOnlyList<HostComponentDisplayModel> BuildComponents(ValidatedApplicationDeclaration declaration) =>
        declaration.FeatureGroups
            .SelectMany(featureGroup => featureGroup.Components)
            .Select(component => HostComponentDisplayModel.From(component, preferences.IsVisible(component.Identity)))
            .ToArray();
}

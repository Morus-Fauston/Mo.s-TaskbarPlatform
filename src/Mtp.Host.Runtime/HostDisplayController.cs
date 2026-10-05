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
    StructuredError? PreferenceError)
{
    public IReadOnlyList<StructuredError> Errors =>
        new[] { DeclarationError, PreferenceError }
            .Where(error => error is not null)
            .Cast<StructuredError>()
            .ToArray();
}

public sealed class HostDisplayController
{
    private readonly HostDeclarationLoader declarationLoader;
    private readonly ComponentDisplayPreferenceManager preferenceManager;
    private IReadOnlyList<HostComponentDisplayModel> components = Array.Empty<HostComponentDisplayModel>();
    private BrokerApplicationSnapshot? brokerSnapshot;

    public HostDisplayController(
        IDeclarationSource declarationSource,
        IComponentDisplayPreferenceStore preferenceStore)
    {
        declarationLoader = new HostDeclarationLoader(declarationSource);
        preferenceManager = new ComponentDisplayPreferenceManager(preferenceStore);
    }

    public IReadOnlyList<HostComponentDisplayModel> CurrentComponents => components;

    public void ApplyBrokerSnapshot(BrokerApplicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Declaration is null) return;
        if (brokerSnapshot is null) preferenceManager.Load();
        brokerSnapshot = snapshot;
        components = BuildComponents(snapshot.Declaration);
    }

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

        var preferenceResult = preferenceManager.Load();
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

        var declaration = brokerSnapshot?.Declaration ?? declarationLoader.SnapshotStore.Current;
        var declaredComponent = declaration?
            .FeatureGroups
            .SelectMany(featureGroup => featureGroup.Components)
            .FirstOrDefault(component => component.Identity == identity);
        if (declaredComponent is null)
        {
            return CoreResult<HostComponentDisplayModel>.Failure(
                new StructuredError("component_not_declared", "The component is not present in the current declaration.", identity.ToString()));
        }

        var saveResult = preferenceManager.SetVisibility(identity, isVisible);
        if (!saveResult.IsSuccess)
        {
            return CoreResult<HostComponentDisplayModel>.Failure(saveResult.Error!);
        }

        components = BuildComponents(declaration!);
        return CoreResult<HostComponentDisplayModel>.Success(
            components.First(component => component.Identity == identity));
    }

    private IReadOnlyList<HostComponentDisplayModel> BuildComponents(ValidatedApplicationDeclaration declaration) =>
        Array.AsReadOnly(declaration.FeatureGroups
            .SelectMany(featureGroup => featureGroup.Components)
            .Select(component => Project(component))
            .ToArray());

    private HostComponentDisplayModel Project(Component component)
    {
        var model = HostComponentDisplayModel.From(component, preferenceManager.Current.IsVisible(component.Identity));
        if (brokerSnapshot is null) return model;
        var reading = brokerSnapshot.State?.Components.FirstOrDefault(value =>
            value.FeatureGroupId == component.Identity.Segments[1].Value && value.ComponentId == component.Identity.LocalId.Value);
        return model with
        {
            Text = reading?.Text ?? "等待服务状态",
            Status = brokerSnapshot.IsInteractive ? CapabilityStatus.Available : CapabilityStatus.Unavailable,
            StatusLabel = brokerSnapshot.IsInteractive ? "可用" : "服务未连接" +
                (brokerSnapshot.LastError is { } error ? " · " + error.Message : "")
        };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Mtp.Contracts;
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
    private IReadOnlyDictionary<string, BrokerApplicationSnapshot> brokerSnapshots =
        new Dictionary<string, BrokerApplicationSnapshot>(StringComparer.Ordinal);
    private bool preferencesLoaded;

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
        var next = new Dictionary<string, BrokerApplicationSnapshot>(brokerSnapshots, StringComparer.Ordinal)
        {
            [snapshot.ApplicationId] = snapshot
        };
        ApplyBrokerSnapshots(next.Values.ToArray());
    }

    /// <summary>UI-thread entry point replacing a complete batch with only the latest accepted state per application.</summary>
    public void ApplyBrokerSnapshots(IReadOnlyList<BrokerApplicationSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count > ProtocolLimits.MaximumApplications)
            throw new ArgumentException("Snapshot batch exceeds the application budget.", nameof(snapshots));
        var next = new Dictionary<string, BrokerApplicationSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null || !next.TryAdd(snapshot.ApplicationId, snapshot))
                throw new ArgumentException("Snapshot batch contains a missing or duplicate application.", nameof(snapshots));
        }
        if (next.Count == brokerSnapshots.Count &&
            next.All(pair => brokerSnapshots.TryGetValue(pair.Key, out var current) && ReferenceEquals(pair.Value, current)))
            return;
        if (!preferencesLoaded)
        {
            preferenceManager.Load();
            preferencesLoaded = true;
        }
        var nextComponents = BuildComponents(next);
        brokerSnapshots = next;
        components = nextComponents;
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
        preferencesLoaded = true;
        components = BuildComponents(brokerSnapshots);

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

        var declaredComponent = components.FirstOrDefault(component => component.Identity == identity);
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

        components = BuildComponents(brokerSnapshots);
        return CoreResult<HostComponentDisplayModel>.Success(
            components.First(component => component.Identity == identity));
    }

    private IReadOnlyList<HostComponentDisplayModel> BuildComponents(IReadOnlyDictionary<string, BrokerApplicationSnapshot> snapshots)
    {
        var models = new Dictionary<StableIdentity, HostComponentDisplayModel>();
        if (declarationLoader.SnapshotStore.Current is { } local)
            foreach (var component in local.FeatureGroups.SelectMany(group => group.Components))
                models[component.Identity] = Project(component, null);
        foreach (var snapshot in snapshots.Values)
        {
            if (snapshot.Declaration is null) continue;
            foreach (var component in snapshot.Declaration.FeatureGroups.SelectMany(group => group.Components))
                models[component.Identity] = Project(component, snapshot);
        }
        return Array.AsReadOnly(models.Values.ToArray());
    }

    private HostComponentDisplayModel Project(Component component, BrokerApplicationSnapshot? brokerSnapshot)
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

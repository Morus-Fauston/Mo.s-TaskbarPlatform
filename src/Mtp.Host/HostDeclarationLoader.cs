using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// The outcome of one local declaration load attempt.
/// </summary>
public sealed record DeclarationLoadResult(
    bool Accepted,
    ValidatedApplicationDeclaration? Current,
    StructuredError? Error);

/// <summary>
/// Loads and validates a declaration without replacing a valid snapshot on failure.
/// </summary>
public sealed class HostDeclarationLoader
{
    private readonly IDeclarationSource source;
    private readonly DeclarationSnapshotStore snapshotStore;

    public HostDeclarationLoader(
        IDeclarationSource source,
        DeclarationSnapshotStore? snapshotStore = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.snapshotStore = snapshotStore ?? new DeclarationSnapshotStore();
    }

    public DeclarationSnapshotStore SnapshotStore => snapshotStore;

    public DeclarationLoadResult Load()
    {
        var sourceResult = source.Read();
        if (!sourceResult.IsSuccess)
        {
            return new DeclarationLoadResult(false, snapshotStore.Current, sourceResult.Error);
        }

        var validationResult = snapshotStore.SubmitJson(sourceResult.Value!);
        return new DeclarationLoadResult(
            validationResult.IsSuccess,
            snapshotStore.Current,
            validationResult.Error);
    }
}

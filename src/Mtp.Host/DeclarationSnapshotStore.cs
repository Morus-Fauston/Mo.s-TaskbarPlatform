using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// Atomically replaces the last accepted declaration only after full validation.
/// </summary>
public sealed class DeclarationSnapshotStore
{
    private readonly DeclarationValidator validator;
    private ValidatedApplicationDeclaration? current;

    public DeclarationSnapshotStore(DeclarationValidator? validator = null)
    {
        this.validator = validator ?? new DeclarationValidator();
    }

    public ValidatedApplicationDeclaration? Current => current;

    public CoreResult<ValidatedApplicationDeclaration> Submit(ApplicationDeclaration? declaration)
    {
        var result = validator.Validate(declaration);
        if (result.IsSuccess)
        {
            current = result.Value;
        }

        return result;
    }
}

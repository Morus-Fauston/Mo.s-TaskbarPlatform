using Mtp.Contracts;

namespace Mtp.Host;

public sealed class ValidatedActionSlot
{
    internal ValidatedActionSlot(ActionSlotReference reference, ActionParameterKind parameterKind)
    {
        Reference = reference;
        ParameterKind = parameterKind;
    }

    public ActionSlotReference Reference { get; }
    public ActionParameterKind ParameterKind { get; }
}

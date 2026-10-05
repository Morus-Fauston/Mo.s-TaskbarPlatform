using Mtp.Contracts;

namespace Mtp.Host;

internal static class ItemStructureEquality
{
    public static bool Same(ItemStructureDeclaration left, ItemStructureDeclaration right) =>
        ReferenceEquals(left, right) ||
        left.StructureId == right.StructureId && left.IsRepeated == right.IsRepeated && left.Normal == right.Normal &&
        left.Expanded == right.Expanded && left.InitiallyExpanded == right.InitiallyExpanded && left.Animation == right.Animation &&
        left.Overflow == right.Overflow && left.PrimaryActivation == right.PrimaryActivation &&
        new HashSet<string>(left.ExpandTargetStructureIds ?? [], StringComparer.Ordinal).SetEquals(right.ExpandTargetStructureIds ?? []) &&
        new HashSet<ItemControlActivation>(left.ControlActivations ?? []).SetEquals(right.ControlActivations ?? []);
}

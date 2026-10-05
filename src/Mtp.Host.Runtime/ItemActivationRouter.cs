using Mtp.Contracts;

namespace Mtp.Host;

public sealed class ItemActivationRouter(ItemPresentationController items, BrokerStateStore states)
{
    private readonly ItemPresentationController items = items ?? throw new ArgumentNullException(nameof(items));
    private readonly BrokerStateStore states = states ?? throw new ArgumentNullException(nameof(states));

    public HostItemActivationResult Activate(ItemInteractionHandle handle, ItemActivationSource source, bool alreadyHandled = false)
    {
        if (alreadyHandled) return new(ProtocolResult.Success(), true);
        if (!Enum.IsDefined(source)) return new(ProtocolResult.Reject("InvalidActivationSource", "项激活来源无效"), true);
        var current = items.Resolve(handle);
        if (current is null) return new(ProtocolResult.Reject("StaleItem", "项交互句柄已失效"), true);
        var binding = source switch
        {
            ItemActivationSource.BlankPrimary => current.Structure.PrimaryActivation,
            ItemActivationSource.PrimaryButton => current.Structure.ControlActivations?.FirstOrDefault(value => value.Control == ItemControlKind.PrimaryButton)?.Binding,
            ItemActivationSource.SecondaryButton => current.Structure.ControlActivations?.FirstOrDefault(value => value.Control == ItemControlKind.SecondaryButton)?.Binding,
            _ => null
        };
        if (binding?.Kind is ItemActivationKind.ToggleSelf or ItemActivationKind.ToggleDeclaredTargets)
        {
            var expansion = items.Toggle(handle, binding.Kind == ItemActivationKind.ToggleDeclaredTargets);
            return new(expansion.Result, true, Items: expansion.Items);
        }
        if (binding?.Kind == ItemActivationKind.BusinessAction)
        {
            var snapshot = states.GetSnapshot(handle.Item.ApplicationId);
            if (!current.IsInteractive || snapshot?.IsInteractive != true || !snapshot.IsConnected || snapshot.SessionId != current.SessionId)
                return new(ProtocolResult.Reject("ActionNotAvailable", "动作会话或声明已失效"), true);
            var reference = new ActionSlotReference(handle.Item.ApplicationId, handle.Item.FeatureGroupId,
                ActionEntryKind.Component, handle.Item.ComponentId, binding.TargetId!);
            if (snapshot.Declaration?.ActionSlots.Any(value => value.Reference == reference) != true)
                return new(ProtocolResult.Reject("UnknownAction", "动作未在当前组件声明"), true);
            return new(ProtocolResult.Success(), true, Action: reference, SessionId: current.SessionId, Origin: current.Handle);
        }
        if (binding?.Kind == ItemActivationKind.TaskbarFlyout)
        {
            var snapshot = states.GetSnapshot(handle.Item.ApplicationId);
            if (snapshot?.SessionId != current.SessionId || snapshot.Declaration?.FeatureGroups.Any(group =>
                group.Identity.LocalId.Value == handle.Item.FeatureGroupId &&
                group.TaskbarFlyouts.Any(flyout => flyout.Identity.LocalId.Value == binding.TargetId)) != true)
                return new(ProtocolResult.Reject("UnknownFlyout", "面板未在当前功能组声明"), true);
            return new(ProtocolResult.Success(), true, TaskbarFlyoutId: binding.TargetId, SessionId: current.SessionId, Origin: current.Handle);
        }
        return new(ProtocolResult.Success(), true);
    }
}

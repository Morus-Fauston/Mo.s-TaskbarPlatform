namespace Mtp.Contracts;

public enum ItemActivationKind { None, ToggleSelf, ToggleDeclaredTargets, TaskbarFlyout, BusinessAction }
public enum ItemControlKind { PrimaryButton, SecondaryButton }

/// <summary>One controlled outcome; target names an existing local action slot or same-group panel.</summary>
public sealed record ItemActivationBinding(ItemActivationKind Kind, string? TargetId = null);
public sealed record ItemControlActivation(ItemControlKind Control, ItemActivationBinding Binding);

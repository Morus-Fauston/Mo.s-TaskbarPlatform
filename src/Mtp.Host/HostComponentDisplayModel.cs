using System;
using System.Linq;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>
/// The small, UI-neutral display projection used by the initial Host window.
/// </summary>
public sealed record HostComponentDisplayModel(
    StableIdentity Identity,
    string Text,
    CapabilityStatus Status,
    string StatusLabel,
    bool IsVisible)
{
    public static HostComponentDisplayModel From(Component component)
        => From(component, true);

    public static HostComponentDisplayModel From(Component component, bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(component);

        return new HostComponentDisplayModel(
            component.Identity,
            "MTP Host 组件",
            component.Capability.Status,
            GetStatusLabel(component.Capability.Status),
            isVisible);
    }

    public static HostComponentDisplayModel From(ValidatedApplicationDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var component = declaration.FeatureGroups
            .SelectMany(featureGroup => featureGroup.Components)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("A validated declaration must contain a component.");

        return From(component);
    }

    private static string GetStatusLabel(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Available => "可用",
        CapabilityStatus.NotApplicable => "不适用",
        CapabilityStatus.Unsupported => "不支持",
        CapabilityStatus.Unavailable => "不可用",
        CapabilityStatus.Recovering => "恢复中",
        CapabilityStatus.Failed => "失败",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未知能力状态。"),
    };
}

using System;
using System.Collections.Generic;

namespace Mtp.Platform.Core;

/// <summary>
/// 材质种类。这是纯数据枚举：平台核心只描述"可以声明哪些材质"，
/// 不包含任何 Windows、WinUI 或合成 API 依赖；实际渲染由 Host 的 Windows 适配层完成。
/// </summary>
public enum MaterialKind
{
    /// <summary>纯色半透明表面。透明度可调，颜色可调，但不是模糊材质。</summary>
    Solid,

    /// <summary>亚克力（磨砂玻璃）。半透明材质，能透出后面的窗口；透明度以浓淡表达，不能指定任意颜色 alpha。</summary>
    Acrylic,

    /// <summary>
    /// 云母。**不透明材质**：它只采样一次桌面壁纸作为底色并叠加主题色，不会透出后面的窗口。
    /// 因此其不透明度数值只影响材质亮度，不会让窗口变透明。Windows 11 及以上可用。
    /// </summary>
    Mica,

    /// <summary>不使用任何材质，使用不透明表面。</summary>
    None,
}

/// <summary>
/// 一种材质的完整声明：种类加不透明度。
/// 不透明度对 <see cref="MaterialKind.Solid"/> 表示表面 alpha；
/// 对其他种类表示系统材质的浓淡程度。
/// </summary>
public readonly record struct MaterialSpec(MaterialKind Kind, double Opacity)
{
    public const double MinimumOpacity = 0.0;
    public const double MaximumOpacity = 1.0;

    /// <summary>默认材质：亚克力，浓淡 0.8，与设计规格中的默认不透明度一致。</summary>
    public static MaterialSpec Default { get; } = new(MaterialKind.Acrylic, 0.8);

    public bool IsValid => Kind is MaterialKind.Solid or MaterialKind.Acrylic or MaterialKind.Mica or MaterialKind.None
        && double.IsFinite(Opacity)
        && Opacity >= MinimumOpacity
        && Opacity <= MaximumOpacity;

    public static double ClampOpacity(double opacity)
    {
        if (!double.IsFinite(opacity))
        {
            return Default.Opacity;
        }

        return Math.Clamp(opacity, MinimumOpacity, MaximumOpacity);
    }
}

/// <summary>
/// 当前环境实际支持哪些材质。由 Host 的 Windows 适配层探测后传入；
/// 平台核心只做纯逻辑降级，不自己探测 Windows 能力。
/// </summary>
public readonly record struct MaterialCapabilities(
    bool SupportsSolid,
    bool SupportsAcrylic,
    bool SupportsMica)
{
    /// <summary>最保守的能力集：只有纯色可用。</summary>
    public static MaterialCapabilities SolidOnly { get; } = new(true, false, false);
}

/// <summary>
/// 材质降级结果。请求的材质与实际可用的材质分开记录，
/// 便于设置页显示"已降级"而不是静默改写用户偏好。
/// </summary>
public readonly record struct MaterialResolution(
    MaterialSpec Requested,
    MaterialSpec Effective,
    bool WasDowngraded)
{
    /// <summary>用户可读的降级原因；未降级时为 null。</summary>
    public string? DowngradeReason { get; init; }
}

/// <summary>
/// 纯逻辑的材质解析：把"用户或接入应用声明的材质"映射到"当前环境真正能用的材质"。
/// 不修改用户的请求值，只返回实际生效值，符合“保留用户偏好、运行时降级”的既有要求。
/// </summary>
public static class MaterialResolver
{
    /// <summary>
    /// 解析材质。降级顺序为：请求的材质不可用 → 亚克力 → 云母 → 纯色 → 无材质。
    /// 不透明度始终保留请求值（被限制在合法区间内），不因降级而被改写。
    /// </summary>
    public static MaterialResolution Resolve(MaterialSpec requested, MaterialCapabilities capabilities)
    {
        var opacity = MaterialSpec.ClampOpacity(requested.Opacity);
        var normalizedRequest = requested with { Opacity = opacity };

        if (!IsSupported(requested.Kind, capabilities))
        {
            var fallback = FirstSupported(capabilities);
            var effective = new MaterialSpec(fallback, opacity);
            return new MaterialResolution(normalizedRequest, effective, WasDowngraded: fallback != requested.Kind)
            {
                DowngradeReason = DescribeDowngrade(requested.Kind, fallback),
            };
        }

        return new MaterialResolution(normalizedRequest, normalizedRequest, WasDowngraded: false);
    }

    private static bool IsSupported(MaterialKind kind, MaterialCapabilities capabilities) => kind switch
    {
        MaterialKind.None => true,
        MaterialKind.Solid => capabilities.SupportsSolid,
        MaterialKind.Acrylic => capabilities.SupportsAcrylic,
        MaterialKind.Mica => capabilities.SupportsMica,
        _ => false,
    };

    private static MaterialKind FirstSupported(MaterialCapabilities capabilities)
    {
        if (capabilities.SupportsAcrylic)
        {
            return MaterialKind.Acrylic;
        }

        if (capabilities.SupportsMica)
        {
            return MaterialKind.Mica;
        }

        return capabilities.SupportsSolid ? MaterialKind.Solid : MaterialKind.None;
    }

    private static string? DescribeDowngrade(MaterialKind requested, MaterialKind effective) =>
        requested == effective
            ? null
            : $"{Describe(requested)} 在当前环境不可用，已降级为{Describe(effective)}。";

    /// <summary>材质的中文显示名，供设置页与状态提示复用。</summary>
    public static string Describe(MaterialKind kind) => kind switch
    {
        MaterialKind.Solid => "纯色半透明",
        MaterialKind.Acrylic => "亚克力",
        MaterialKind.Mica => "云母",
        MaterialKind.None => "不透明",
        _ => "未知材质",
    };

    /// <summary>
    /// 该材质是否会透出窗口后面的内容。云母按官方定义是不透明材质（只采样一次壁纸），
    /// 因此返回 false；纯色与亚克力可透出。
    /// </summary>
    public static bool IsTranslucent(MaterialKind kind) => kind is MaterialKind.Solid or MaterialKind.Acrylic;

    /// <summary>
    /// 不透明度数值对该材质意味着什么。同一数值在不同材质上的视觉含义不同，
    /// 界面必须如实说明，避免用户以为"调到 0 就该全透"。
    /// </summary>
    public static string DescribeOpacityMeaning(MaterialKind kind) => kind switch
    {
        MaterialKind.Solid => "表面透明度（0 完全透明，1 完全不透明）",
        MaterialKind.Acrylic => "磨砂层浓淡（不会让窗口变成完全透明）",
        MaterialKind.Mica => "材质亮度（云母不透明，不会透出后面的窗口）",
        MaterialKind.None => "不适用（无材质时窗口为不透明表面）",
        _ => "不适用",
    };

    /// <summary>
    /// 设置页材质下拉框的固定顺序。顺序是产品行为，放在核心层以便测试，
    /// 不含任何界面框架依赖。
    /// </summary>
    public static IReadOnlyList<MaterialKind> SelectionOrder { get; } = new[]
    {
        MaterialKind.Acrylic,
        MaterialKind.Mica,
        MaterialKind.Solid,
        MaterialKind.None,
    };
}

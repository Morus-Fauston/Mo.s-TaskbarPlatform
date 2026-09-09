using Mtp.Platform.Core;
using Xunit;

namespace Mtp.Platform.Core.Tests;

/// <summary>
/// 材质解析是纯逻辑：把声明的材质映射到当前环境可用的材质，并保留用户的原始请求值。
/// 这些测试不涉及任何 Windows 或合成 API，因此可以在任意环境运行。
/// </summary>
public sealed class MaterialResolverTests
{
    private static readonly MaterialCapabilities FullCapabilities = new(
        SupportsSolid: true,
        SupportsAcrylic: true,
        SupportsMica: true);

    [Fact]
    public void Resolve_KeepsRequestedMaterialWhenSupported()
    {
        var requested = new MaterialSpec(MaterialKind.Acrylic, 0.8);

        var resolution = MaterialResolver.Resolve(requested, FullCapabilities);

        Assert.False(resolution.WasDowngraded);
        Assert.Equal(MaterialKind.Acrylic, resolution.Effective.Kind);
        Assert.Equal(0.8, resolution.Effective.Opacity);
        Assert.Null(resolution.DowngradeReason);
    }

    [Fact]
    public void Resolve_DowngradesMicaToAcrylicWhenMicaUnsupported()
    {
        var capabilities = new MaterialCapabilities(SupportsSolid: true, SupportsAcrylic: true, SupportsMica: false);

        var resolution = MaterialResolver.Resolve(new MaterialSpec(MaterialKind.Mica, 0.6), capabilities);

        Assert.True(resolution.WasDowngraded);
        Assert.Equal(MaterialKind.Mica, resolution.Requested.Kind);
        Assert.Equal(MaterialKind.Acrylic, resolution.Effective.Kind);
        Assert.Equal(0.6, resolution.Effective.Opacity);
        Assert.NotNull(resolution.DowngradeReason);
    }

    [Fact]
    public void Resolve_DowngradesAcrylicToSolidWhenOnlySolidSupported()
    {
        var resolution = MaterialResolver.Resolve(new MaterialSpec(MaterialKind.Acrylic, 0.5), MaterialCapabilities.SolidOnly);

        Assert.True(resolution.WasDowngraded);
        Assert.Equal(MaterialKind.Solid, resolution.Effective.Kind);
        Assert.Equal(0.5, resolution.Effective.Opacity);
    }

    [Fact]
    public void Resolve_DowngradesToNoneWhenNothingIsSupported()
    {
        var capabilities = new MaterialCapabilities(SupportsSolid: false, SupportsAcrylic: false, SupportsMica: false);

        var resolution = MaterialResolver.Resolve(new MaterialSpec(MaterialKind.Acrylic, 0.7), capabilities);

        Assert.True(resolution.WasDowngraded);
        Assert.Equal(MaterialKind.None, resolution.Effective.Kind);
    }

    [Fact]
    public void Resolve_PrefersAcrylicOverMicaWhenBothSupported()
    {
        var resolution = MaterialResolver.Resolve(new MaterialSpec(MaterialKind.Mica, 0.5), MaterialCapabilities.SolidOnly with { SupportsAcrylic = true });

        Assert.Equal(MaterialKind.Acrylic, resolution.Effective.Kind);
    }

    [Fact]
    public void Resolve_NoneIsAlwaysSupportedAndNeverDowngraded()
    {
        var capabilities = new MaterialCapabilities(SupportsSolid: false, SupportsAcrylic: false, SupportsMica: false);

        var resolution = MaterialResolver.Resolve(new MaterialSpec(MaterialKind.None, 1.0), capabilities);

        Assert.False(resolution.WasDowngraded);
        Assert.Equal(MaterialKind.None, resolution.Effective.Kind);
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(double.NaN, 0.8)]
    [InlineData(double.PositiveInfinity, 0.8)]
    public void Resolve_ClampsOpacityIntoValidRange(double requestedOpacity, double expectedOpacity)
    {
        var resolution = MaterialResolver.Resolve(new MaterialSpec(MaterialKind.Solid, requestedOpacity), FullCapabilities);

        Assert.Equal(expectedOpacity, resolution.Effective.Opacity);
    }

    [Fact]
    public void Resolve_PreservesRequestedOpacityWhileDowngrading()
    {
        var resolution = MaterialResolver.Resolve(new MaterialSpec(MaterialKind.Mica, 0.35), MaterialCapabilities.SolidOnly);

        Assert.Equal(0.35, resolution.Effective.Opacity);
        Assert.Equal(0.35, resolution.Requested.Opacity);
    }

    [Fact]
    public void Resolve_DoesNotMutateRequestedKind()
    {
        var requested = new MaterialSpec(MaterialKind.Mica, 0.4);

        var resolution = MaterialResolver.Resolve(requested, MaterialCapabilities.SolidOnly);

        Assert.Equal(MaterialKind.Mica, resolution.Requested.Kind);
        Assert.Equal(MaterialKind.Mica, requested.Kind);
    }

    [Fact]
    public void DefaultSpecIsValidAndUsesAcrylic()
    {
        Assert.True(MaterialSpec.Default.IsValid);
        Assert.Equal(MaterialKind.Acrylic, MaterialSpec.Default.Kind);
    }

    [Fact]
    public void SelectionOrderContainsEverySelectableMaterialOnce()
    {
        Assert.Equal(
            new[] { MaterialKind.Acrylic, MaterialKind.Mica, MaterialKind.Solid, MaterialKind.None },
            MaterialResolver.SelectionOrder);
    }

    [Fact]
    public void DescribeReturnsNonEmptyTextForEveryKind()
    {
        foreach (var kind in MaterialResolver.SelectionOrder)
        {
            Assert.False(string.IsNullOrWhiteSpace(MaterialResolver.Describe(kind)));
        }
    }

    [Theory]
    [InlineData(MaterialKind.Solid, true)]
    [InlineData(MaterialKind.Acrylic, true)]
    [InlineData(MaterialKind.Mica, false)]
    [InlineData(MaterialKind.None, false)]
    public void IsTranslucentMatchesTheDocumentedMaterialBehaviour(MaterialKind kind, bool expected)
    {
        // Mica is an opaque material by definition: it samples the wallpaper once and never lets the
        // content behind the window show through. Solid and Acrylic are translucent.
        Assert.Equal(expected, MaterialResolver.IsTranslucent(kind));
    }

    [Fact]
    public void DescribeOpacityMeaningExplainsThatMicaDoesNotBecomeTransparent()
    {
        var meaning = MaterialResolver.DescribeOpacityMeaning(MaterialKind.Mica);

        Assert.Contains("不透明", meaning);
        Assert.Contains("不会透出", meaning);
    }

    [Fact]
    public void DescribeOpacityMeaningIsNonEmptyForEverySelectableKind()
    {
        foreach (var kind in MaterialResolver.SelectionOrder)
        {
            Assert.False(string.IsNullOrWhiteSpace(MaterialResolver.DescribeOpacityMeaning(kind)));
        }
    }
}

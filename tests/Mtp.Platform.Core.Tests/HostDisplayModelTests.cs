using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class HostDisplayModelTests
{
    [Fact]
    public void AvailableComponentConvertsToFixedTextAndStatusMarker()
    {
        var identity = new StableIdentity(new StableId("mtp"))
            .CreateChild(new StableId("baseline"));
        var component = new Component(identity, CapabilityState.Available);

        var display = HostComponentDisplayModel.From(component);

        Assert.Equal(identity, display.Identity);
        Assert.Equal("MTP Host 组件", display.Text);
        Assert.Equal(CapabilityStatus.Available, display.Status);
        Assert.Equal("可用", display.StatusLabel);
    }

    [Fact]
    public void ValidatedDeclarationProjectsItsFirstComponentUsingStableIdentity()
    {
        var application = new StableIdentity(new StableId("local-app"));
        var feature = application.CreateChild(new StableId("feature"));
        var component = feature.CreateChild(new StableId("component"));
        var declaration = new ValidatedApplicationDeclaration(
            application,
            new[]
            {
                new ValidatedFeatureGroup(
                    feature,
                    new[] { new Component(component, CapabilityState.Available) },
                    Array.Empty<ValidatedTaskbarFlyout>()),
            });

        var display = HostComponentDisplayModel.From(declaration);

        Assert.Equal("component", display.Identity.LocalId.Value);
        Assert.Equal(new[] { "local-app", "feature", "component" },
            display.Identity.Segments.Select(segment => segment.Value));
        Assert.Equal("MTP Host 组件", display.Text);
        Assert.Equal("可用", display.StatusLabel);
    }
}

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
        var declaration = new DeclarationValidator().ValidateJson("""
            {
              "applicationId": "local-app",
              "featureGroups": [
                {
                  "featureGroupId": "feature",
                  "components": [
                    { "componentId": "component", "actionSlots": [{ "actionSlotId": "go" }] }
                  ],
                  "taskbarFlyouts": [
                    { "taskbarFlyoutId": "panel", "actionSlots": [{ "actionSlotId": "go" }] }
                  ]
                }
              ]
            }
            """).Value!;

        var display = HostComponentDisplayModel.From(declaration);

        Assert.Equal("component", display.Identity.LocalId.Value);
        Assert.Equal(new[] { "local-app", "feature", "component" },
            display.Identity.Segments.Select(segment => segment.Value));
        Assert.Equal("MTP Host 组件", display.Text);
        Assert.Equal("可用", display.StatusLabel);
    }

    [Fact]
    public void CurrentDisplayComponentsCannotBeChangedThroughThePublicCollection()
    {
        var controller = new HostDisplayController(
            new DeclarationSource("""
                {
                  "applicationId": "local-app",
                  "featureGroups": [
                    {
                      "featureGroupId": "feature",
                      "components": [
                        { "componentId": "component", "actionSlots": [{ "actionSlotId": "go" }] }
                      ],
                      "taskbarFlyouts": [
                        { "taskbarFlyoutId": "panel", "actionSlots": [{ "actionSlotId": "go" }] }
                      ]
                    }
                  ]
                }
                """),
            new PreferenceStore());

        var loaded = controller.Load();

        Assert.IsAssignableFrom<System.Collections.IList>(loaded.Components);
        Assert.True(((System.Collections.IList)loaded.Components).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((System.Collections.IList)loaded.Components)[0] = loaded.Components[0]);
        Assert.Same(loaded.Components, controller.CurrentComponents);
    }

    private sealed class DeclarationSource(string json) : IDeclarationSource
    {
        public CoreResult<string> Read() => CoreResult<string>.Success(json);
    }

    private sealed class PreferenceStore : IComponentDisplayPreferenceStore
    {
        private ComponentDisplayPreferences preferences = new();

        public ComponentDisplayPreferenceLoadResult Load() =>
            new(preferences, null, ComponentDisplayPreferenceLoadState.Loaded);

        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool isVisible)
        {
            preferences = preferences.WithVisibility(identity, isVisible);
            return CoreResult<ComponentDisplayPreferences>.Success(preferences);
        }
    }
}

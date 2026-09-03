using System;
using System.Collections.Generic;
using System.Linq;
using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class IndependentDockWindowTests
{
    [Fact]
    public void VisibleValidatedComponentCanBeShownAndClosedThroughHostLifecycle()
    {
        var declarationController = CreateDisplayController();
        var component = declarationController.Load().Components.Single();
        Assert.True(declarationController.SetVisibility(component.Identity, true).IsSuccess);

        var adapter = new RecordingDockWindowAdapter();
        using var controller = new IndependentDockWindowController(declarationController, adapter);

        var shown = controller.ShowCurrent();

        Assert.True(shown.IsSuccess);
        Assert.True(controller.State.IsOpen);
        Assert.Equal(component.Identity, controller.State.Component!.Identity);
        Assert.Same(component.Identity, adapter.ShownComponent!.Identity);

        var closed = controller.Close();

        Assert.True(closed.IsSuccess);
        Assert.False(controller.State.IsOpen);
        Assert.Null(controller.State.Component);
        Assert.Equal(1, adapter.CloseCount);
    }

    [Fact]
    public void ShowFailureKeepsValidatedDisplayAndVisibilityPreference()
    {
        var declarationController = CreateDisplayController();
        var component = declarationController.Load().Components.Single();
        Assert.True(declarationController.SetVisibility(component.Identity, true).IsSuccess);
        var adapter = new RecordingDockWindowAdapter
        {
            ShowError = new StructuredError("dock_window_show_failed", "The dock window could not be created.")
        };
        using var controller = new IndependentDockWindowController(declarationController, adapter);

        var result = controller.ShowCurrent();

        Assert.False(result.IsSuccess);
        Assert.Equal("dock_window_show_failed", result.Error!.Code);
        Assert.False(controller.State.IsOpen);
        Assert.Equal(component.Identity, declarationController.CurrentComponents.Single().Identity);
        Assert.True(declarationController.CurrentComponents.Single().IsVisible);
        Assert.True(declarationController.SetVisibility(component.Identity, true).IsSuccess);
    }

    [Fact]
    public void CloseFailureKeepsOpenStateAndComponentModel()
    {
        var declarationController = CreateDisplayController();
        var component = declarationController.Load().Components.Single();
        Assert.True(declarationController.SetVisibility(component.Identity, true).IsSuccess);
        var adapter = new RecordingDockWindowAdapter
        {
            CloseError = new StructuredError("dock_window_close_failed", "The dock window could not be closed.")
        };
        using var controller = new IndependentDockWindowController(declarationController, adapter);
        Assert.True(controller.ShowCurrent().IsSuccess);

        var result = controller.Close();

        Assert.False(result.IsSuccess);
        Assert.Equal("dock_window_close_failed", result.Error!.Code);
        Assert.True(controller.State.IsOpen);
        Assert.Equal(component.Identity, controller.State.Component!.Identity);
        Assert.Equal(component.Identity, declarationController.CurrentComponents.Single().Identity);
    }

    [Fact]
    public void HiddenOrUndeclaredComponentCannotCreateAWindow()
    {
        var declarationController = CreateDisplayController();
        var component = declarationController.Load().Components.Single();
        var adapter = new RecordingDockWindowAdapter();
        using var controller = new IndependentDockWindowController(declarationController, adapter);

        var hidden = controller.ShowCurrent();
        var undeclared = controller.Show(HostComponentDisplayModel.From(
            new Component(new StableIdentity(new StableId("other")), CapabilityState.Available), true));

        Assert.False(hidden.IsSuccess);
        Assert.Equal("dock_component_not_visible", hidden.Error!.Code);
        Assert.False(undeclared.IsSuccess);
        Assert.Equal("dock_component_not_declared", undeclared.Error!.Code);
        Assert.Equal(0, adapter.ShowCount);
    }

    private static HostDisplayController CreateDisplayController()
    {
        return new HostDisplayController(
            new StubDeclarationSource(ValidJson()),
            new MemoryPreferenceStore());
    }

    private static string ValidJson() => """
        {
          "applicationId": "music",
          "featureGroups": [
            {
              "featureGroupId": "controls",
              "components": [
                { "componentId": "widget", "actionSlots": [{ "actionSlotId": "go" }] }
              ],
              "taskbarFlyouts": [
                { "taskbarFlyoutId": "panel", "actionSlots": [{ "actionSlotId": "go" }] }
              ]
            }
          ]
        }
        """;

    private sealed class StubDeclarationSource : IDeclarationSource
    {
        private readonly string content;

        public StubDeclarationSource(string content) => this.content = content;

        public CoreResult<string> Read() => CoreResult<string>.Success(content);
    }

    private sealed class MemoryPreferenceStore : IComponentDisplayPreferenceStore
    {
        private ComponentDisplayPreferences preferences = new();

        public ComponentDisplayPreferenceLoadResult Load() =>
            new(preferences, null);

        public CoreResult<ComponentDisplayPreferences> Save(ComponentDisplayPreferences preferences)
        {
            this.preferences = preferences;
            return CoreResult<ComponentDisplayPreferences>.Success(preferences);
        }
    }

    private sealed class RecordingDockWindowAdapter : IIndependentDockWindowAdapter
    {
        public event EventHandler? Closed;

        public bool IsOpen { get; private set; }

        public HostComponentDisplayModel? ShownComponent { get; private set; }

        public StructuredError? ShowError { get; init; }

        public StructuredError? CloseError { get; init; }

        public int ShowCount { get; private set; }

        public int CloseCount { get; private set; }

        public CoreResult<HostComponentDisplayModel> Show(HostComponentDisplayModel component)
        {
            ShowCount++;
            if (ShowError is not null)
            {
                return CoreResult<HostComponentDisplayModel>.Failure(ShowError);
            }

            IsOpen = true;
            ShownComponent = component;
            return CoreResult<HostComponentDisplayModel>.Success(component);
        }

        public CoreResult<bool> Close()
        {
            CloseCount++;
            if (CloseError is not null)
            {
                return CoreResult<bool>.Failure(CloseError);
            }

            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
            return CoreResult<bool>.Success(true);
        }
    }
}

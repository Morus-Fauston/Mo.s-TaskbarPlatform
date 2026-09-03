using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class DisplayPreferenceTests
{
    [Fact]
    public void NewComponentsDefaultToHiddenAndCanBeShownAndSavedSeparately()
    {
        var path = PreferencePath();
        try
        {
            var controller = new HostDisplayController(
                new StubDeclarationSource(ValidJson("music", "controls", "widget")),
                new LocalComponentDisplayPreferenceStore(path));

            var loaded = controller.Load();
            var component = loaded.Components.Single();

            Assert.False(component.IsVisible);
            var changed = controller.SetVisibility(component.Identity, true);

            Assert.True(changed.IsSuccess);
            Assert.True(changed.Value!.IsVisible);
            Assert.NotEqual(File.ReadAllText(path), ValidJson("music", "controls", "widget"));
            Assert.DoesNotContain("displayPreferences", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void VisibilitySurvivesAControllerRestartWithoutChangingDeclaration()
    {
        var path = PreferencePath();
        var declaration = ValidJson("music", "controls", "widget");
        try
        {
            var first = new HostDisplayController(
                new StubDeclarationSource(declaration),
                new LocalComponentDisplayPreferenceStore(path));
            var component = first.Load().Components.Single();
            Assert.True(first.SetVisibility(component.Identity, true).IsSuccess);

            var second = new HostDisplayController(
                new StubDeclarationSource(declaration),
                new LocalComponentDisplayPreferenceStore(path));
            var restored = second.Load();

            Assert.True(restored.Components.Single().IsVisible);
            Assert.DoesNotContain("applicationId", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void MissingOrCorruptPreferenceFileUsesHiddenDefaultsAndExplainsTheProblem()
    {
        var missingPath = PreferencePath();
        var missing = new HostDisplayController(
            new StubDeclarationSource(ValidJson("music", "controls", "widget")),
            new LocalComponentDisplayPreferenceStore(missingPath)).Load();

        Assert.False(missing.Components.Single().IsVisible);
        Assert.Equal("preference_not_found", missing.PreferenceError!.Code);

        var corruptPath = PreferencePath();
        try
        {
            File.WriteAllText(corruptPath, "{\"components\": [");
            var corrupt = new HostDisplayController(
                new StubDeclarationSource(ValidJson("music", "controls", "widget")),
                new LocalComponentDisplayPreferenceStore(corruptPath)).Load();

            Assert.False(corrupt.Components.Single().IsVisible);
            Assert.Equal("preference_invalid", corrupt.PreferenceError!.Code);
        }
        finally
        {
            Delete(missingPath);
            Delete(corruptPath);
        }
    }

    [Fact]
    public void PreferenceEntriesForMissingComponentsAreRetainedButCannotCreatePhantoms()
    {
        var path = PreferencePath();
        var firstDeclaration = ValidJson("music", "controls", "widget");
        var secondDeclaration = ValidJson("music", "controls", "other");
        try
        {
            var first = new HostDisplayController(
                new StubDeclarationSource(firstDeclaration),
                new LocalComponentDisplayPreferenceStore(path));
            var firstComponent = first.Load().Components.Single();
            Assert.True(first.SetVisibility(firstComponent.Identity, true).IsSuccess);

            var secondSource = new StubDeclarationSource(secondDeclaration);
            var second = new HostDisplayController(
                secondSource,
                new LocalComponentDisplayPreferenceStore(path));
            var changed = second.Load();

            Assert.Single(changed.Components);
            Assert.Equal("other", changed.Components.Single().Identity.LocalId.Value);
            Assert.False(changed.Components.Single().IsVisible);

            secondSource.Content = firstDeclaration;
            var restored = second.Load();
            Assert.True(restored.Components.Single().IsVisible);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void InvalidVisibilityTargetIsRejectedWithoutWritingAPhantomPreference()
    {
        var path = PreferencePath();
        try
        {
            var controller = new HostDisplayController(
                new StubDeclarationSource(ValidJson("music", "controls", "widget")),
                new LocalComponentDisplayPreferenceStore(path));
            controller.Load();
            var unknown = new StableIdentity(new StableId("unknown"));

            var result = controller.SetVisibility(unknown, true);

            Assert.False(result.IsSuccess);
            Assert.Equal("component_not_declared", result.Error!.Code);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void FailedPreferenceWriteRestoresThePreviousVisibility()
    {
        var store = new FailingPreferenceStore();
        var controller = new HostDisplayController(
            new StubDeclarationSource(ValidJson("music", "controls", "widget")),
            store);
        var component = controller.Load().Components.Single();

        var result = controller.SetVisibility(component.Identity, true);

        Assert.False(result.IsSuccess);
        Assert.Equal("preference_write_failed", result.Error!.Code);
        Assert.False(controller.CurrentComponents.Single().IsVisible);
    }

    private static string PreferencePath() => Path.Combine(Path.GetTempPath(), $"mtp-preferences-{Guid.NewGuid():N}.json");

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ValidJson(string applicationId, string featureGroupId, string componentId) => $$"""
        {
          "applicationId": "{{applicationId}}",
          "featureGroups": [
            {
              "featureGroupId": "{{featureGroupId}}",
              "components": [
                { "componentId": "{{componentId}}", "actionSlots": [{ "actionSlotId": "go" }] }
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
        public StubDeclarationSource(string content) => Content = content;

        public string Content { get; set; }

        public CoreResult<string> Read() => CoreResult<string>.Success(Content);
    }

    private sealed class FailingPreferenceStore : IComponentDisplayPreferenceStore
    {
        public ComponentDisplayPreferenceLoadResult Load() =>
            new(new ComponentDisplayPreferences(), null);

        public CoreResult<ComponentDisplayPreferences> Save(ComponentDisplayPreferences preferences) =>
            CoreResult<ComponentDisplayPreferences>.Failure(
                new StructuredError("preference_write_failed", "The display preference file cannot be written."));
    }
}

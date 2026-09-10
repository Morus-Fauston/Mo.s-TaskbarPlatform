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

    [Fact]
    public void TemporaryReadFailureDoesNotErasePreferencesForComponentsMissingFromTheCurrentDeclaration()
    {
        var path = PreferencePath();
        try
        {
            var original = new HostDisplayController(
                new StubDeclarationSource(ValidJson("music", "controls", "remembered")),
                new LocalComponentDisplayPreferenceStore(path));
            var remembered = original.Load().Components.Single();
            Assert.True(original.SetVisibility(remembered.Identity, true).IsSuccess);

            var current = new HostDisplayController(
                new StubDeclarationSource(ValidJson("music", "controls", "current")),
                new LocalComponentDisplayPreferenceStore(path));
            HostComponentDisplayModel currentComponent;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var unavailable = current.Load();
                Assert.Equal("preference_read_failed", unavailable.PreferenceError!.Code);
                currentComponent = unavailable.Components.Single();
            }

            Assert.True(current.SetVisibility(currentComponent.Identity, true).IsSuccess);

            var restored = new HostDisplayController(
                new StubDeclarationSource(ValidJson("music", "controls", "remembered")),
                new LocalComponentDisplayPreferenceStore(path)).Load();
            Assert.True(restored.Components.Single().IsVisible);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void PreferenceLoadDistinguishesMissingInvalidAndTemporarilyUnavailableFiles()
    {
        var path = PreferencePath();
        try
        {
            var store = new LocalComponentDisplayPreferenceStore(path);
            var missing = store.Load();
            Assert.Equal(ComponentDisplayPreferenceLoadState.Missing, missing.State);

            File.WriteAllText(path, "{\"components\": [");
            var invalid = store.Load();
            Assert.Equal(ComponentDisplayPreferenceLoadState.Invalid, invalid.State);

            File.WriteAllText(path, "{\"components\": []}");
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var unavailable = store.Load();
            Assert.Equal(ComponentDisplayPreferenceLoadState.Unavailable, unavailable.State);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void PreferenceLoadResultRejectsContradictoryStateAndErrorCombinations()
    {
        var error = new StructuredError("preference_read_failed", "Cannot read preferences.");

        Assert.Throws<ArgumentException>(() => new ComponentDisplayPreferenceLoadResult(
            new ComponentDisplayPreferences(),
            error,
            ComponentDisplayPreferenceLoadState.Loaded));
        Assert.Throws<ArgumentException>(() => new ComponentDisplayPreferenceLoadResult(
            new ComponentDisplayPreferences(),
            null,
            ComponentDisplayPreferenceLoadState.Unavailable));
    }

    [Fact]
    public void DisplayLoadResultPreservesDeclarationAndPreferenceErrorsTogether()
    {
        var declarationError = new StructuredError("declaration_invalid", "Invalid declaration.");
        var preferenceError = new StructuredError("preference_invalid", "Invalid preferences.");
        var result = new HostDisplayLoadResult(
            false,
            null,
            Array.Empty<HostComponentDisplayModel>(),
            declarationError,
            preferenceError);

        Assert.Equal(new[] { declarationError, preferenceError }, result.Errors);
    }

    [Fact]
    public void AtomicReplacementFailureKeepsThePreviousFileAndCleansTemporaryOutput()
    {
        var path = PreferencePath();
        try
        {
            var originalController = new HostDisplayController(
                new StubDeclarationSource(ValidJson("music", "controls", "remembered")),
                new LocalComponentDisplayPreferenceStore(path));
            var original = originalController.Load().Components.Single();
            Assert.True(originalController.SetVisibility(original.Identity, true).IsSuccess);
            var previousContent = File.ReadAllText(path);

            var replacement = new ComponentDisplayPreferences();
            CoreResult<ComponentDisplayPreferences> saveResult;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                saveResult = new LocalComponentDisplayPreferenceStore(path)
                    .CommitVisibility(original.Identity, false);
            }

            Assert.False(saveResult.IsSuccess);
            Assert.Equal("preference_write_failed", saveResult.Error!.Code);
            Assert.Equal(previousContent, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                $"{Path.GetFileName(path)}.*.tmp"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void InvalidPreferencePathReturnsAStructuredFailureInsteadOfThrowing()
    {
        var store = new LocalComponentDisplayPreferenceStore("\0");

        var load = store.Load();
        var save = store.CommitVisibility(new StableIdentity(new StableId("test")), true);

        Assert.Equal(ComponentDisplayPreferenceLoadState.Unavailable, load.State);
        Assert.Equal("preference_read_failed", load.Error!.Code);
        Assert.False(save.IsSuccess);
        Assert.Equal("preference_write_failed", save.Error!.Code);
    }

    [Fact]
    public void TwoHostsMergeVisibilityChangesAgainstTheLatestPreferenceFile()
    {
        var path = PreferencePath();
        const string declaration = """
            {
              "applicationId": "music",
              "featureGroups": [
                {
                  "featureGroupId": "controls",
                  "components": [
                    { "componentId": "first", "actionSlots": [{ "actionSlotId": "go" }] },
                    { "componentId": "second", "actionSlots": [{ "actionSlotId": "go" }] }
                  ],
                  "taskbarFlyouts": [
                    { "taskbarFlyoutId": "panel", "actionSlots": [{ "actionSlotId": "go" }] }
                  ]
                }
              ]
            }
            """;
        try
        {
            var firstHost = new HostDisplayController(
                new StubDeclarationSource(declaration),
                new LocalComponentDisplayPreferenceStore(path));
            var secondHost = new HostDisplayController(
                new StubDeclarationSource(declaration),
                new LocalComponentDisplayPreferenceStore(path));
            var firstSnapshot = firstHost.Load().Components;
            var secondSnapshot = secondHost.Load().Components;

            Assert.True(firstHost.SetVisibility(firstSnapshot[0].Identity, true).IsSuccess);
            Assert.True(secondHost.SetVisibility(secondSnapshot[1].Identity, true).IsSuccess);

            var reloaded = new HostDisplayController(
                new StubDeclarationSource(declaration),
                new LocalComponentDisplayPreferenceStore(path)).Load();
            Assert.All(reloaded.Components, component => Assert.True(component.IsVisible));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void OversizedPreferenceFileIsRejectedBeforeDeserialization()
    {
        var path = PreferencePath();
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(LocalComponentDisplayPreferenceStore.MaximumJsonSizeInBytes + 1L);
            }

            var result = new LocalComponentDisplayPreferenceStore(path).Load();

            Assert.Equal(ComponentDisplayPreferenceLoadState.Invalid, result.State);
            Assert.Equal("preference_too_large", result.Error!.Code);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void OversizedPreferenceCommitKeepsThePreviousFile()
    {
        var path = PreferencePath();
        try
        {
            var store = new LocalComponentDisplayPreferenceStore(path);
            var originalIdentity = new StableIdentity(new StableId("original"));
            Assert.True(store.CommitVisibility(originalIdentity, true).IsSuccess);
            var previousContent = File.ReadAllText(path);
            var oversizedIdentity = new StableIdentity(
                new StableId(new string('x', LocalComponentDisplayPreferenceStore.MaximumJsonSizeInBytes)));

            var result = store.CommitVisibility(oversizedIdentity, true);

            Assert.False(result.IsSuccess);
            Assert.Equal("preference_too_large", result.Error!.Code);
            Assert.Equal(previousContent, File.ReadAllText(path));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void ExcessivelyLongPreferencePathReturnsStructuredFailures()
    {
        var path = @"C:\" + new string('a', 40000);
        var store = new LocalComponentDisplayPreferenceStore(path);

        var load = store.Load();
        var save = store.CommitVisibility(new StableIdentity(new StableId("test")), true);

        Assert.Equal(ComponentDisplayPreferenceLoadState.Unavailable, load.State);
        Assert.Equal("preference_read_failed", load.Error!.Code);
        Assert.False(save.IsSuccess);
        Assert.Equal("preference_write_failed", save.Error!.Code);
    }

    [Fact]
    public void BoundedUtf8ReaderRejectsContentBeyondTheRequestedLimit()
    {
        var path = PreferencePath();
        try
        {
            File.WriteAllBytes(path, new byte[4096]);

            var accepted = BoundedUtf8File.TryRead(path, 32, out var content);

            Assert.False(accepted);
            Assert.Empty(content);
        }
        finally
        {
            Delete(path);
        }
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
            new(
                new ComponentDisplayPreferences(),
                null,
                ComponentDisplayPreferenceLoadState.Loaded);

        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool isVisible) =>
            CoreResult<ComponentDisplayPreferences>.Failure(
                new StructuredError("preference_write_failed", "The display preference file cannot be written."));
    }
}

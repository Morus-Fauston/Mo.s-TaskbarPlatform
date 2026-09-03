using Mtp.Contracts;
using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class DeclarationLoadingTests
{
    [Fact]
    public void LoaderReadsAValidDeclarationThroughAReplaceableSource()
    {
        var source = new StubDeclarationSource(ValidJson("music", "controls", "widget"));
        var loader = new HostDeclarationLoader(source);

        var result = loader.Load();

        Assert.True(result.Accepted);
        Assert.Null(result.Error);
        Assert.Equal(new[] { "music", "controls", "widget" },
            result.Current!.FeatureGroups[0].Components[0].Identity.Segments.Select(segment => segment.Value));
    }

    [Theory]
    [InlineData("", "declaration_required")]
    [InlineData("{", "unsupported_structure")]
    [InlineData("{\"applicationId\":\"music\",\"featureGroups\":[],\"extra\":true}", "unsupported_structure")]
    [InlineData("{\"applicationId\":\"music\",\"featureGroups\":[{\"featureGroupId\":\"controls\",\"components\":[{\"componentId\":\"widget\",\"actionSlots\":[{\"actionSlotId\":\"go\"}]}],\"taskbarFlyouts\":[{\"taskbarFlyoutId\":\"panel\",\"actionSlots\":[{\"actionSlotId\":\"go\"}]}]},{\"featureGroupId\":\"controls\",\"components\":[{\"componentId\":\"other\",\"actionSlots\":[{\"actionSlotId\":\"go\"}]}],\"taskbarFlyouts\":[{\"taskbarFlyoutId\":\"other-panel\",\"actionSlots\":[{\"actionSlotId\":\"go\"}]}]}]}", "duplicate_id")]
    [InlineData("{\"applicationId\":\"music\",\"featureGroups\":[{\"featureGroupId\":\"controls\",\"components\":[],\"taskbarFlyouts\":[{\"taskbarFlyoutId\":\"panel\",\"actionSlots\":[{\"actionSlotId\":\"go\"}]}]}]}", "required_entry_missing")]
    public void LoaderReturnsStructuredRejectionForInvalidSourceContent(string json, string expectedCode)
    {
        var loader = new HostDeclarationLoader(new StubDeclarationSource(json));

        var result = loader.Load();

        Assert.False(result.Accepted);
        Assert.Null(result.Current);
        Assert.Equal(expectedCode, result.Error!.Code);
    }

    [Fact]
    public void RejectedDeclarationKeepsTheLastAcceptedSnapshot()
    {
        var source = new StubDeclarationSource(ValidJson("music", "controls", "widget"));
        var loader = new HostDeclarationLoader(source);
        var accepted = loader.Load();
        var previous = accepted.Current;

        source.Content = ValidJson("music", "controls", "widget")
            .Replace("\"applicationId\": \"music\"", "\"applicationId\": \"music\", \"unexpected\": true", StringComparison.Ordinal);

        var rejected = loader.Load();

        Assert.False(rejected.Accepted);
        Assert.Same(previous, rejected.Current);
        Assert.Same(previous, loader.SnapshotStore.Current);
        Assert.Equal("unsupported_structure", rejected.Error!.Code);
    }

    [Fact]
    public void LocalSourceReadsTheConfiguredJsonFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mtp-declaration-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, ValidJson("music", "controls", "widget"));

            var result = new LocalJsonDeclarationSource(path).Read();

            Assert.True(result.IsSuccess);
            Assert.Contains("\"applicationId\": \"music\"", result.Value, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void LoaderTurnsTheConfiguredLocalFileIntoAValidatedComponentSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mtp-loader-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, ValidJson("music", "controls", "widget"));

            var loader = new HostDeclarationLoader(new LocalJsonDeclarationSource(path));
            var result = loader.Load();

            Assert.True(result.Accepted);
            Assert.Equal(new[] { "music", "controls", "widget" },
                result.Current!.FeatureGroups[0].Components[0].Identity.Segments.Select(segment => segment.Value));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void MissingLocalFileIsAStableStructuredError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mtp-missing-{Guid.NewGuid():N}.json");

        var result = new LocalJsonDeclarationSource(path).Read();

        Assert.False(result.IsSuccess);
        Assert.Equal("declaration_not_found", result.Error!.Code);
        Assert.Equal(path, result.Error.Path);
    }

    [Fact]
    public void SourceFailureAlsoKeepsTheLastAcceptedSnapshot()
    {
        var store = new DeclarationSnapshotStore();
        var accepted = store.SubmitJson(ValidJson("music", "controls", "widget"));
        var source = new StubDeclarationSource(string.Empty)
        {
            Error = new StructuredError("declaration_read_failed", "The local declaration file cannot be read.")
        };
        var loader = new HostDeclarationLoader(source, store);

        var result = loader.Load();

        Assert.True(accepted.IsSuccess);
        Assert.False(result.Accepted);
        Assert.Same(accepted.Value, result.Current);
        Assert.Equal("declaration_read_failed", result.Error!.Code);
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
        public StubDeclarationSource(string content)
        {
            Content = content;
        }

        public string Content { get; set; }

        public StructuredError? Error { get; set; }

        public CoreResult<string> Read() => Error is null
            ? CoreResult<string>.Success(Content)
            : CoreResult<string>.Failure(Error);
    }
}

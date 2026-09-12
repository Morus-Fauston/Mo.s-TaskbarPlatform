using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class TaskbarDockPreferenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-dock-" + Guid.NewGuid().ToString("N"));
    public TaskbarDockPreferenceTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void DisplayAndGapCommitsPreserveEachOtherAcrossIndependentStoreInstances()
    {
        var path = Path.Combine(directory, "dock.json");
        var first = new LocalTaskbarDockPreferenceStore(path);
        Assert.Equal(new TaskbarDockPreferences(), first.Load().Value);
        Assert.True(first.CommitDisplay("display-a").IsSuccess);
        Assert.True(new LocalTaskbarDockPreferenceStore(path).CommitGap(24).IsSuccess);
        Assert.Equal(new TaskbarDockPreferences("display-a", 24), first.Load().Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65)]
    public void InvalidGapDoesNotOverwriteSavedValues(int gap)
    {
        var path = Path.Combine(directory, "dock.json");
        var store = new LocalTaskbarDockPreferenceStore(path);
        Assert.True(store.CommitGap(16).IsSuccess);
        var original = File.ReadAllBytes(path);
        Assert.False(store.CommitGap(gap).IsSuccess);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidDisplayDoesNotCreatePreferenceFile(string display)
    {
        var path = Path.Combine(directory, "dock.json");
        Assert.False(new LocalTaskbarDockPreferenceStore(path).CommitDisplay(display).IsSuccess);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void LockedOldFileIsNotOverwrittenAndCanBeUpdatedAfterUnlock()
    {
        var path = Path.Combine(directory, "dock.json");
        var store = new LocalTaskbarDockPreferenceStore(path);
        Assert.True(store.CommitDisplay("display-a").IsSuccess);
        var original = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(store.Load().IsSuccess);
            Assert.False(store.CommitGap(24).IsSuccess);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(store.CommitGap(24).IsSuccess);
        Assert.Equal("display-a", store.Load().Value?.TargetDisplayId);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"RightGapDip\":65}")]
    [InlineData("{\"unexpected\":1}")]
    public void MalformedOldFileIsNotReplaced(string json)
    {
        var path = Path.Combine(directory, "dock.json");
        File.WriteAllText(path, json);
        var store = new LocalTaskbarDockPreferenceStore(path);
        Assert.False(store.CommitDisplay("display-a").IsSuccess);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void ReplacementFailureKeepsLastCompleteFile()
    {
        var path = Path.Combine(directory, "dock.json");
        var store = new LocalTaskbarDockPreferenceStore(path);
        Assert.True(store.CommitGap(16).IsSuccess);
        var original = File.ReadAllBytes(path);
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(store.CommitGap(32).IsSuccess);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
}

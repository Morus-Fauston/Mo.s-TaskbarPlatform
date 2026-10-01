using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class HostPreferenceStorageTests
{
    [Fact]
    public void MissingLocalDataRootReturnsUnavailableStoresWithoutWritingRelativePaths()
    {
        var storage = HostPreferenceStorage.Initialize(AppContext.BaseDirectory, "");
        Assert.Equal(2, storage.Errors.Count);
        Assert.Equal(ComponentDisplayPreferenceLoadState.Unavailable, storage.DisplayStore.Load().State);
        Assert.False(storage.DisplayStore.CommitVisibility(Identity, true).IsSuccess);
        Assert.False(storage.DockStore.CommitGap(12).IsSuccess);
    }

    private static StableIdentity Identity => new StableIdentity(new StableId("app")).CreateChild(new StableId("widget"));

    [Fact]
    public void ImportsBothPreferencesOnceAndKeepsOldFilesAndNewerUserState()
    {
        var directory = Directory.CreateTempSubdirectory("mtp-storage-");
        try
        {
            var application = directory.CreateSubdirectory("app").FullName;
            var local = directory.CreateSubdirectory("local").FullName;
            var oldDisplay = Path.Combine(application, "display-preferences.json");
            var oldDock = Path.Combine(application, "taskbar-dock-preferences.json");
            Assert.True(new LocalComponentDisplayPreferenceStore(oldDisplay).CommitVisibility(Identity, true).IsSuccess);
            Assert.True(new LocalTaskbarDockPreferenceStore(oldDock).CommitGap(24).IsSuccess);
            var oldBytes = File.ReadAllBytes(oldDisplay);
            var storage = HostPreferenceStorage.Initialize(application, local);
            Assert.Empty(storage.Errors);
            Assert.Equal(Path.Combine(local, "Mo.s-TaskbarPlatform"), storage.DirectoryPath);
            Assert.True(storage.DisplayStore.Load().Preferences.IsVisible(Identity));
            Assert.Equal(24, storage.DockStore.Load().Value!.RightGapDip);
            Assert.Equal(oldBytes, File.ReadAllBytes(oldDisplay));
            Assert.True(storage.DisplayStore.CommitVisibility(Identity, false).IsSuccess);
            Assert.True(storage.DockStore.CommitGap(12).IsSuccess);
            var restarted = HostPreferenceStorage.Initialize(application, local);
            Assert.Empty(restarted.Errors);
            Assert.False(restarted.DisplayStore.Load().Preferences.IsVisible(Identity));
            Assert.Equal(12, restarted.DockStore.Load().Value!.RightGapDip);
            Assert.Equal(oldBytes, File.ReadAllBytes(oldDisplay));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("oversize")]
    [InlineData("invalid-utf8")]
    public void InvalidLegacyStateBlocksWritesAndPreservesSource(string kind)
    {
        var directory = Directory.CreateTempSubdirectory("mtp-storage-");
        try
        {
            var application = directory.CreateSubdirectory("app").FullName;
            var local = directory.CreateSubdirectory("local").FullName;
            var source = Path.Combine(application, "display-preferences.json");
            var bytes = kind switch
            {
                "oversize" => new byte[1024 * 1024 + 1],
                "invalid-utf8" => new byte[] { 0xff },
                _ => System.Text.Encoding.UTF8.GetBytes(kind),
            };
            File.WriteAllBytes(source, bytes);
            var storage = HostPreferenceStorage.Initialize(application, local);
            Assert.Single(storage.Errors);
            Assert.Equal(ComponentDisplayPreferenceLoadState.Unavailable, storage.DisplayStore.Load().State);
            Assert.False(storage.DisplayStore.CommitVisibility(Identity, true).IsSuccess);
            Assert.False(File.Exists(storage.DisplayPath));
            Assert.Equal(bytes, File.ReadAllBytes(source));
            Assert.Empty(Directory.GetFiles(storage.DirectoryPath, "*.tmp"));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void FreshInstallAndUnavailableDataDirectoryHaveExplicitOutcomes()
    {
        var directory = Directory.CreateTempSubdirectory("mtp-storage-");
        try
        {
            var app = directory.CreateSubdirectory("app").FullName;
            var local = directory.CreateSubdirectory("local").FullName;
            var storage = HostPreferenceStorage.Initialize(app, local);
            Assert.Empty(storage.Errors);
            Assert.True(storage.DisplayStore.CommitVisibility(Identity, true).IsSuccess);
            Assert.True(storage.DockStore.CommitGap(16).IsSuccess);
            var blockedRoot = directory.CreateSubdirectory("blocked").FullName;
            File.WriteAllText(Path.Combine(blockedRoot, "Mo.s-TaskbarPlatform"), "occupied");
            var blocked = HostPreferenceStorage.Initialize(app, blockedRoot);
            Assert.Equal(2, blocked.Errors.Count);
            Assert.False(blocked.DisplayStore.CommitVisibility(Identity, false).IsSuccess);
            Assert.False(blocked.DockStore.CommitGap(16).IsSuccess);
            Assert.False(File.Exists(Path.Combine(app, "display-preferences.json")));
        }
        finally { directory.Delete(true); }
    }
}

using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class PreferenceLockTests
{
    [Fact]
    public void DisplayCommitHonorsTheFileLockAndCanRecoverAfterRelease()
    {
        var directory = Directory.CreateTempSubdirectory("mtp-lock-");
        var path = Path.Combine(directory.FullName, "preferences.json");
        try
        {
            var store = new LocalComponentDisplayPreferenceStore(path);
            var identity = new StableIdentity(new StableId("app")).CreateChild(new StableId("component"));
            using (var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(store.CommitVisibility(identity, true).IsSuccess);
                Assert.False(File.Exists(path));
            }

            Assert.True(store.CommitVisibility(identity, true).IsSuccess);
            Assert.True(store.Load().Preferences.IsVisible(identity));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}

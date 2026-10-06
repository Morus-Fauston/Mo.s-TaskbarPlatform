using System.Diagnostics;
using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class PreferenceReplacementTests
{
    [Theory]
    [InlineData("display")]
    [InlineData("settings")]
    [InlineData("dock")]
    public async Task TransientReplacementLockCanClearWithoutLosingCommittedValues(string kind)
    {
        var directory = Directory.CreateTempSubdirectory("mtp-replace-");
        try
        {
            string path = Path.Combine(directory.FullName, "preferences.json");
            Assert.True(Commit(kind, path, false));
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var started = new ManualResetEventSlim();
            var pending = Task.Run(() => { started.Set(); return Commit(kind, path, true); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(200);
            locked.Dispose();
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(Read(kind, path));
            Assert.Empty(directory.GetFiles("*.tmp"));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("display")]
    [InlineData("settings")]
    [InlineData("dock")]
    public void PersistentReplacementLockHasBoundedFailureAndPreservesPreviousFile(string kind)
    {
        var directory = Directory.CreateTempSubdirectory("mtp-replace-");
        try
        {
            string path = Path.Combine(directory.FullName, "preferences.json");
            Assert.True(Commit(kind, path, false));
            byte[] before = File.ReadAllBytes(path);
            var watch = Stopwatch.StartNew();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.False(Commit(kind, path, true));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(directory.GetFiles("*.tmp"));
        }
        finally { directory.Delete(true); }
    }

    private static readonly StableIdentity Identity = new StableIdentity(new StableId("app"))
        .CreateChild(new StableId("feature")).CreateChild(new StableId("component"));
    private static bool Commit(string kind, string path, bool value) => kind switch
    {
        "display" => new LocalComponentDisplayPreferenceStore(path).CommitVisibility(Identity, value).IsSuccess,
        "settings" => new LocalHostSettingsPreferenceStore(path).CommitAppearance(new(value ? HostTheme.Dark : HostTheme.Light)).IsSuccess,
        _ => new LocalTaskbarDockPreferenceStore(path).CommitGap(value ? 16 : 8).IsSuccess
    };
    private static bool Read(string kind, string path) => kind switch
    {
        "display" => new LocalComponentDisplayPreferenceStore(path).Load().Preferences.IsVisible(Identity),
        "settings" => new LocalHostSettingsPreferenceStore(path).Load().Value!.Appearance.Theme == HostTheme.Dark,
        _ => new LocalTaskbarDockPreferenceStore(path).Load().Value!.RightGapDip == 16
    };
}

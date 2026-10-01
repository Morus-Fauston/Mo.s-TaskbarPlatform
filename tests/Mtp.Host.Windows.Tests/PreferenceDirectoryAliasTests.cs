using System.Diagnostics;
using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class PreferenceDirectoryAliasTests
{
    [Fact]
    public void JunctionAliasSharesTheSamePreferenceLock()
    {
        var directory = Directory.CreateTempSubdirectory("mtp-alias-");
        var original = directory.CreateSubdirectory("original").FullName;
        var alias = Path.Combine(directory.FullName, "alias");
        try
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.Environment["MTP_TEST_LINK"] = alias;
            start.Environment["MTP_TEST_TARGET"] = original;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:MTP_TEST_LINK -Target $env:MTP_TEST_TARGET | Out-Null" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(15000));
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
            var path = Path.Combine(original, "preferences.json");
            var alternate = Path.Combine(alias, "preferences.json");
            var store = new LocalComponentDisplayPreferenceStore(alternate);
            var identity = new StableIdentity(new StableId("app"));
            using (var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.False(store.CommitVisibility(identity, true).IsSuccess);
            Assert.True(store.CommitVisibility(identity, true).IsSuccess);
            Assert.True(new LocalComponentDisplayPreferenceStore(path).Load().Preferences.IsVisible(identity));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
            directory.Delete(true);
        }
    }
}

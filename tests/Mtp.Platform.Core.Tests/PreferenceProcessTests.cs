using System.Diagnostics;
using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class PreferenceProcessTests
{
    [Fact]
    public async Task IndependentProcessesPreserveAllConcurrentCommits()
    {
        var directory = Directory.CreateTempSubdirectory("mtp-process-");
        var path = Path.Combine(directory.FullName, "preferences.json");
        var gate = Path.Combine(directory.FullName, "go");
        using var first = Start("commit", path, "a", directory.FullName, gate);
        using var second = Start("commit", path, "b", directory.FullName, gate);
        try
        {
            await Ready(Path.Combine(directory.FullName, "a.ready"), first);
            await Ready(Path.Combine(directory.FullName, "b.ready"), second);
            File.WriteAllText(gate, "go");
            await Finished(first);
            await Finished(second);
            var result = new LocalComponentDisplayPreferenceStore(path).Load();
            Assert.Equal(ComponentDisplayPreferenceLoadState.Loaded, result.State);
            foreach (var prefix in new[] { "a", "b" })
                for (var index = 0; index < 40; index++)
                    Assert.True(result.Preferences.IsVisible(Identity(prefix + index)), prefix + index);
        }
        finally
        {
            Stop(first);
            Stop(second);
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ProcessTerminationReleasesThePersistentLock()
    {
        var directory = Directory.CreateTempSubdirectory("mtp-crash-");
        var path = Path.Combine(directory.FullName, "preferences.json");
        using var child = Start("hold", path, "held", directory.FullName, Path.Combine(directory.FullName, "never"));
        try
        {
            await Ready(Path.Combine(directory.FullName, "held.ready"), child);
            Assert.Throws<IOException>(() => new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            child.Kill();
            await child.WaitForExitAsync();
            var store = new LocalComponentDisplayPreferenceStore(path);
            Assert.True(store.CommitVisibility(Identity("restored"), true).IsSuccess);
            Assert.True(store.Load().Preferences.IsVisible(Identity("restored")));
            Assert.True(File.Exists(path + ".lock"));
        }
        finally { Stop(child); directory.Delete(true); }
    }

    private static StableIdentity Identity(string component) =>
        new StableIdentity(new StableId("app")).CreateChild(new StableId(component));

    private static Process Start(string mode, string path, string prefix, string directory, string gate)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "PreferenceProbe", "Mtp.PreferenceStore.ProcessProbe.dll"), mode, path, prefix, Path.Combine(directory, prefix + ".ready"), gate })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task Ready(string path, Process child)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.False(child.HasExited, "Worker exited before reaching the start barrier.");
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "Worker readiness timeout.");
            await Task.Delay(20);
        }
    }

    private static async Task Finished(Process child)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await child.WaitForExitAsync(timeout.Token);
        var error = await child.StandardError.ReadToEndAsync();
        Assert.True(child.ExitCode == 0, error);
    }

    private static void Stop(Process child)
    {
        if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
    }
}

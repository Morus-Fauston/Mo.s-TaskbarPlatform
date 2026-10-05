using System.Diagnostics;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class InitialServiceRecoveryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_service_that_never_sends_Hello_enters_recovery_after_its_initial_connection_deadline()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-initial-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "force-unresponsive"), "hold initial handshake");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            await using var host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["subject"], deadline.Token);
            var elapsed = Stopwatch.StartNew();
            int pid = await host.StartServiceAsync("subject", Path.Combine(AppContext.BaseDirectory, "RecoveryProcessProbe/Mtp.Recovery.ProcessProbe.dll"), deadline.Token, ["stubborn", directory]);
            using var service = Process.GetProcessById(pid);
            _ = service.Handle;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(7) && host.GetRecovery("subject")?.State != RecoveryState.Recovering)
                await Task.Delay(20, deadline.Token);
            Assert.Equal(RecoveryState.Recovering, host.GetRecovery("subject")!.State);
            Assert.InRange(elapsed.Elapsed.TotalSeconds, 4.9, 7);
            Assert.Null(host.States.GetSnapshot("subject"));
            Assert.False(service.HasExited);
            Assert.Equal(0, host.GetRecovery("subject")!.Restarts);
            output.WriteLine($"initialHelloMissing=true; elapsedMs={elapsed.ElapsedMilliseconds}; pid={pid}; state=Recovering; liveProcessPreserved=true");
            await host.DisposeAsync();
            await service.WaitForExitAsync(deadline.Token);
            Assert.True(service.HasExited);
            Assert.Empty(host.ServiceProcessIds);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

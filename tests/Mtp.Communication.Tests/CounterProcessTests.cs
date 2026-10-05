using System.Diagnostics;
using Mtp.Host;

namespace Mtp.Communication.Tests;

public sealed class CounterProcessTests
{
    [Fact]
    public async Task Failed_startup_cleanup_returns_retryable_owner()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var error = await Assert.ThrowsAsync<BrokerStartCleanupException>(() => HostBrokerSession.StartAsync(
            Path.Combine(AppContext.BaseDirectory, "CounterService/Mtp.CounterService.dll"), ["counter"], cancel.Token, new FailOnceTermination()));
        await error.CleanupOwner.DisposeAsync();
        await error.CleanupOwner.DisposeAsync();
    }

    [Fact]
    public async Task Failed_termination_keeps_owned_handle_until_retry_confirms_exit()
    {
        var root = AppContext.BaseDirectory;
        var termination = new FailOnceTermination();
        var host = await HostBrokerSession.StartAsync(Path.Combine(root, "Broker/Mtp.Broker.dll"),
            ["counter"], processTermination: termination);
        try
        {
            await host.StartServiceAsync("counter", Path.Combine(root, "CounterService/Mtp.CounterService.dll"));
            await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask());
            Assert.Single(host.ServiceProcessIds);
            await host.DisposeAsync();
            Assert.Empty(host.ServiceProcessIds);
            await host.DisposeAsync();
        }
        finally { await host.DisposeAsync(); }
    }

    private sealed class FailOnceTermination : IProcessTermination
    {
        private bool failed;
        public Task TerminateAsync(Process process, CancellationToken cancellationToken)
        {
            if (!failed) { failed = true; throw new IOException("Injected termination failure"); }
            return new OwnedProcessTermination().TerminateAsync(process, cancellationToken);
        }
    }

    [Fact]
    public async Task Independent_counter_reaches_host_and_owned_processes_are_reaped()
    {
        var root = AppContext.BaseDirectory;
        var broker = Path.Combine(root, "Broker/Mtp.Broker.dll");
        var counter = Path.Combine(root, "CounterService/Mtp.CounterService.dll");
        int brokerId;
        int serviceId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using (var host = await HostBrokerSession.StartAsync(broker, ["counter"], timeout.Token))
        {
            brokerId = host.BrokerProcessId;
            serviceId = await host.StartServiceAsync("counter", counter, timeout.Token);
            Assert.NotEqual(Environment.ProcessId, brokerId);
            Assert.NotEqual(brokerId, serviceId);
            while ((host.States.GetSnapshot("counter")?.State?.Revision ?? -1) < 3)
                await Task.Delay(25, timeout.Token);
            var snapshot = host.States.GetSnapshot("counter")!;
            Assert.True(snapshot.IsInteractive);
            Assert.Equal("counter", snapshot.Declaration!.Identity.LocalId.Value);
            Assert.Single(snapshot.State!.Components);
            Assert.True(snapshot.State.Components[0].Number >= 3);
        }
        Assert.False(IsRunning(brokerId));
        Assert.False(IsRunning(serviceId));
    }

    private static bool IsRunning(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    internal static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mtp.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }
}

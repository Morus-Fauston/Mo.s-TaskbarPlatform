using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Mtp.Contracts;
using Mtp.Transport;

namespace Mtp.Host;

public sealed class BrokerStartCleanupException : IOException
{
    internal BrokerStartCleanupException(HostBrokerSession owner, Exception startError, Exception cleanupError)
        : base("Broker startup failed; owned resources still require cleanup.", new AggregateException(startError, cleanupError)) => CleanupOwner = owner;
    public HostBrokerSession CleanupOwner { get; }
}

public interface IProcessTermination
{
    Task TerminateAsync(Process process, CancellationToken cancellationToken);
}

public sealed class OwnedProcessTermination : IProcessTermination
{
    public async Task TerminateAsync(Process process, CancellationToken cancellationToken)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Owns only the Broker and services explicitly launched for this Host session.</summary>
public sealed class HostBrokerSession : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly NamedPipeServerStream control;
    private readonly Dictionary<string, LaunchRegistration> registrations;
    private readonly Dictionary<string, Process> services = new(StringComparer.Ordinal);
    private readonly List<Task> drains = [];
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly string servicePipe;
    private Process? broker;
    private Task? receive;
    private int disposed;
    private string? lastError;
    private int peakPendingRequests;
    private readonly IProcessTermination processTermination;

    private HostBrokerSession(IReadOnlyCollection<string> applications, IProcessTermination processTermination)
    {
        this.processTermination = processTermination;
        States = new BrokerStateStore(applications);
        var run = Guid.NewGuid().ToString("N");
        servicePipe = "mtp-services-" + run;
        control = new NamedPipeServerStream("mtp-host-" + run, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        registrations = applications.ToDictionary(id => id, id => new LaunchRegistration(id,
            Guid.NewGuid().ToString("N"), Secret(), DateTimeOffset.UtcNow.AddSeconds(ProtocolLimits.TicketLifetimeSeconds)), StringComparer.Ordinal);
        HostPipeName = "mtp-host-" + run;
    }

    private string HostPipeName { get; }
    public BrokerStateStore States { get; }
    public int BrokerProcessId => broker?.Id ?? throw new InvalidOperationException("Broker not started.");
    public string? LastError => Volatile.Read(ref lastError);
    public int PeakPendingRequests => Volatile.Read(ref peakPendingRequests);
    public IReadOnlyList<int> ServiceProcessIds
    {
        get { lock (services) return services.Values.Select(p => p.Id).ToArray(); }
    }

    public static async Task<HostBrokerSession> StartAsync(string brokerPath,
        IReadOnlyCollection<string> applications, CancellationToken cancellationToken = default,
        IProcessTermination? processTermination = null)
    {
        var session = new HostBrokerSession(applications, processTermination ?? new OwnedProcessTermination());
        try
        {
            var ticket = Secret();
            session.broker = session.StartOwned(brokerPath);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await LengthPrefixedJson.WriteAsync(session.broker.StandardInput.BaseStream,
                new BrokerLaunch(session.HostPipeName, ticket, session.servicePipe, session.registrations.Values.ToArray()), deadline.Token).ConfigureAwait(false);
            session.broker.StandardInput.Close();
            await session.control.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            var hello = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(session.control, deadline.Token).ConfigureAwait(false);
            if (hello.Kind != MessageKind.Hello || hello.Version != ProtocolLimits.Version ||
                !SecretEquals(ticket, hello.Ticket)) throw new IOException("BrokerAuthenticationFailed");
            await LengthPrefixedJson.WriteAsync(session.control, new ProtocolMessage { Kind = MessageKind.Welcome }, deadline.Token).ConfigureAwait(false);
            var ready = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(session.control, deadline.Token).ConfigureAwait(false);
            if (ready.Kind != MessageKind.Welcome || ready.RequestId != "broker-ready") throw new IOException("BrokerNotReady");
            session.receive = session.ReceiveAsync();
            return session;
        }
        catch (Exception startError)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError) { throw new BrokerStartCleanupException(session, startError, cleanupError); }
            throw;
        }
    }

    public async Task<int> StartServiceAsync(string applicationId, string executablePath, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? arguments = null)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!registrations.TryGetValue(applicationId, out var registration)) throw new InvalidOperationException("ApplicationNotRegistered");
            lock (services)
                if (services.ContainsKey(applicationId)) throw new InvalidOperationException("ApplicationAlreadyStarted");
            if (DateTimeOffset.UtcNow >= registration.ExpiresAt) throw new InvalidOperationException("TicketExpired");
            var process = StartOwned(executablePath, arguments);
            lock (services) services.Add(applicationId, process);
            try
            {
                await LengthPrefixedJson.WriteAsync(process.StandardInput.BaseStream,
                    new ServiceLaunch(servicePipe, applicationId, registration.StartRequestId, registration.Ticket), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
                return process.Id;
            }
            catch
            {
                await StopOwnedAsync(process).ConfigureAwait(false);
                lock (services) services.Remove(applicationId);
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(control, lifetime.Token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (message.BrokerLoad is { } load)
                {
                    if (load.PendingRequests < 0 || load.PeakPendingRequests < load.PendingRequests ||
                        load.PeakPendingRequests > ProtocolLimits.MaximumPendingRequests || load.PeakPendingRequests < PeakPendingRequests)
                        throw new IOException("InvalidBrokerLoad");
                    Volatile.Write(ref peakPendingRequests, load.PeakPendingRequests);
                }
                var response = States.Handle(message);
                if (message.Kind != MessageKind.Disconnected)
                    await LengthPrefixedJson.WriteAsync(control, response, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!lifetime.IsCancellationRequested) Volatile.Write(ref lastError, "BrokerUnavailable");
        }
        finally
        {
            foreach (var snapshot in States.Snapshots)
                States.Handle(new ProtocolMessage { Kind = MessageKind.Disconnected, ApplicationId = snapshot.ApplicationId, SessionId = snapshot.SessionId });
        }
    }

    private Process StartOwned(string path, IReadOnlyList<string>? arguments = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Registered runtime executable missing.", path);
        if (arguments is not null && (arguments.Count > 16 || arguments.Any(value => value is null || value.Length > 1024)))
            throw new ArgumentException("Registered process arguments exceed the launch budget.", nameof(arguments));
        var info = new ProcessStartInfo
        {
            FileName = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Path.GetFullPath(path));
        if (arguments is not null)
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new IOException("ProcessStartFailed");
        drains.Add(DrainAsync(process.StandardOutput));
        drains.Add(DrainAsync(process.StandardError));
        return process;
    }

    private async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static bool SecretEquals(string expected, string actual) => actual is not null && actual.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));

    private async Task StopOwnedAsync(Process process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processTermination.TerminateAsync(process, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        if (!process.HasExited) throw new IOException("ProcessExitNotConfirmed");
        process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed == 2) return;
            Interlocked.Exchange(ref disposed, 1);
            await lifetime.CancelAsync().ConfigureAwait(false);
            control.Dispose();
            var failures = new List<Exception>();
            if (receive is not null)
                try { await receive.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            KeyValuePair<string, Process>[] owned;
            lock (services) owned = services.ToArray();
            foreach (var pair in owned)
                try
                {
                    await StopOwnedAsync(pair.Value).ConfigureAwait(false);
                    lock (services) services.Remove(pair.Key);
                }
                catch (Exception error) { failures.Add(error); }
            if (broker is not null)
                try { await StopOwnedAsync(broker).ConfigureAwait(false); broker = null; } catch (Exception error) { failures.Add(error); }
            await Task.WhenAll(drains).ConfigureAwait(false);
            if (failures.Count != 0) throw new AggregateException("Owned process cleanup failed.", failures);
            lifetime.Dispose();
            Interlocked.Exchange(ref disposed, 2);
        }
        finally { lifecycle.Release(); }
    }
}

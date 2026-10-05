using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading.Channels;
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
    private readonly Dictionary<string, (Process Process, string SessionId)> serviceSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedServiceExit> serviceProcessExits = new(StringComparer.Ordinal);
    private readonly List<Task> drains = [];
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly string servicePipe;
    private Process? broker;
    private Task? receive;
    private Task? actionClock;
    private Task? actionDispatcher;
    private readonly object actionDispatchGate = new();
    private readonly Channel<ProtocolMessage> actionDispatch = Channel.CreateBounded<ProtocolMessage>(
        new BoundedChannelOptions(ProtocolLimits.MaximumPendingRequests) { SingleReader = true });
    private readonly SemaphoreSlim controlWriter = new(1, 1);
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
    public ActionRequestTracker Actions { get; } = new();
    public int BrokerProcessId => broker?.Id ?? throw new InvalidOperationException("Broker not started.");
    public string? LastError => Volatile.Read(ref lastError);
    public int PeakPendingRequests => Volatile.Read(ref peakPendingRequests);
    public IReadOnlyList<OwnedServiceExit> ServiceProcessExits
    {
        get { lock (services) return Array.AsReadOnly(serviceProcessExits.Values.ToArray()); }
    }
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
            session.actionClock = session.RunActionClockAsync();
            session.actionDispatcher = session.DispatchActionsAsync();
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

    public async Task<ProtocolResult> SendActionAsync(ActionSlotReference slot, ActionParameter parameter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(parameter);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (cancellationToken.IsCancellationRequested) return ProtocolResult.Reject("ActionCancelled", "操作已取消");
        BrokerApplicationSnapshot current;
        ActionReservation reserved;
        lock (actionDispatchGate)
        {
            current = States.GetSnapshot(slot.ApplicationId)!;
            if (current is null) return ProtocolResult.Reject("ActionNotAvailable", "应用未连接");
            reserved = Actions.Begin(current, slot, parameter, parameter);
            if (!reserved.Accepted) return reserved.Result;
            var request = reserved.Invocation!;
            if (!actionDispatch.Writer.TryWrite(new ProtocolMessage
            {
                Kind = MessageKind.ActionRequest,
                ApplicationId = current.ApplicationId,
                SessionId = current.SessionId,
                RequestId = request.RequestId,
                Action = request
            }))
                Actions.FailDispatch(current.ApplicationId, current.SessionId, request.RequestId, ProtocolResult.Reject("Busy", "动作派发队列不可用"));
        }
        var invocation = reserved.Invocation!;
        using var cancelled = cancellationToken.Register(() => Actions.Cancel(current.ApplicationId, current.SessionId, invocation.RequestId));
        return await reserved.Completion.ConfigureAwait(false);
    }

    private async Task DispatchActionsAsync()
    {
        try
        {
            await foreach (var message in actionDispatch.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                // Reservation and enqueue share one lock, so wire order follows sequence order.
                // User cancellation ends waiting without truncating a frame or replaying an action.
                using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                dispatch.CancelAfter(TimeSpan.FromSeconds(5));
                await WriteControlAsync(message, dispatch.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!lifetime.IsCancellationRequested) Volatile.Write(ref lastError, "BrokerUnavailable");
            control.Dispose();
            DisconnectApplications();
        }
        finally
        {
            actionDispatch.Writer.TryComplete();
            while (actionDispatch.Reader.TryRead(out _)) { }
        }
    }

    private async Task WriteControlAsync(ProtocolMessage message, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await controlWriter.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { await LengthPrefixedJson.WriteAsync(control, message, deadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A partial frame must never be followed by another write on this connection.
            control.Dispose();
            throw;
        }
        finally { controlWriter.Release(); }
    }

    private async Task RunActionClockAsync()
    {
        var exitCheckTicks = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(250, lifetime.Token).ConfigureAwait(false);
                Actions.Tick();
                if (++exitCheckTicks == 4)
                {
                    exitCheckTicks = 0;
                    ObserveOwnedServiceExits();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(control, lifetime.Token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (message.Kind == MessageKind.ActionCompleted)
                {
                    if (message.Version != ProtocolLimits.Version || message.ActionCompletion is not { } completion ||
                        message.RequestId != completion.RequestId || string.IsNullOrWhiteSpace(message.ApplicationId) ||
                        message.ApplicationId.Length > DeclarationValidator.MaximumIdLength || string.IsNullOrWhiteSpace(message.SessionId) ||
                        message.SessionId.Length > DeclarationValidator.MaximumIdLength || message.Action is not null ||
                        message.Result is not null || message.Declaration is not null || message.State is not null ||
                        message.Flyout is not null || message.BrokerLoad is not null || message.Heartbeat is not null || !string.IsNullOrEmpty(message.Ticket) ||
                        !string.IsNullOrEmpty(message.StartRequestId))
                        throw new IOException("InvalidActionCompletionEnvelope");
                    Actions.Receive(message.ApplicationId, message.SessionId, completion, States.GetSnapshot(message.ApplicationId),
                        state => States.ApplyActionState(message.ApplicationId, message.SessionId, state));
                    continue;
                }
                if (message.BrokerLoad is { } load)
                {
                    if (load.PendingRequests < 0 || load.PeakPendingRequests < load.PendingRequests ||
                        load.PeakPendingRequests > ProtocolLimits.MaximumPendingRequests || load.PeakPendingRequests < PeakPendingRequests)
                        throw new IOException("InvalidBrokerLoad");
                    Volatile.Write(ref peakPendingRequests, load.PeakPendingRequests);
                }
                ProtocolMessage response;
                lock (services)
                {
                    var previous = States.GetSnapshot(message.ApplicationId);
                    if (message.Kind == MessageKind.Disconnected && TryRecordOwnedServiceExit(message.ApplicationId, message.SessionId) is not null)
                        message = message with { Result = ProtocolResult.Reject("ProcessExited", "托管服务进程已退出") };
                    response = States.Handle(message);
                    if (response.Result?.Accepted == true)
                    {
                        if (message.Kind == MessageKind.Welcome && Volatile.Read(ref disposed) == 0 && services.TryGetValue(message.ApplicationId, out var process))
                            serviceSessions[message.ApplicationId] = (process, message.SessionId);
                        if (message.Kind == MessageKind.Disconnected || (message.Kind == MessageKind.Welcome && previous is not null))
                            Actions.EndSession(message.ApplicationId, previous?.SessionId ?? message.SessionId);
                    }
                }
                if (message.Kind != MessageKind.Disconnected)
                    await WriteControlAsync(response, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!lifetime.IsCancellationRequested) Volatile.Write(ref lastError, "BrokerUnavailable");
        }
        finally
        {
            DisconnectApplications();
            actionDispatch.Writer.TryComplete();
        }
    }

    private void DisconnectApplications()
    {
        foreach (var snapshot in States.Snapshots)
        {
            States.Handle(new ProtocolMessage { Kind = MessageKind.Disconnected, ApplicationId = snapshot.ApplicationId, SessionId = snapshot.SessionId });
            Actions.EndSession(snapshot.ApplicationId, snapshot.SessionId);
        }
    }

    private void ObserveOwnedServiceExits()
    {
        lock (services)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            foreach (var pair in serviceSessions)
            {
                if (serviceProcessExits.TryGetValue(pair.Key, out var observed) && observed.SessionId == pair.Value.SessionId) continue;
                var exit = TryRecordOwnedServiceExit(pair.Key, pair.Value.SessionId);
                if (exit is null) continue;
                // A later observed process exit adds a fact; the Store retains any earlier disconnect reason.
                States.Handle(new ProtocolMessage
                {
                    Kind = MessageKind.Disconnected,
                    ApplicationId = exit.ApplicationId,
                    SessionId = exit.SessionId,
                    Result = ProtocolResult.Reject("ProcessExited", "托管服务进程已退出")
                });
                Actions.EndSession(exit.ApplicationId, exit.SessionId);
            }
        }
    }

    // Caller holds services: process identity, session binding and shutdown transition are one observation.
    private OwnedServiceExit? TryRecordOwnedServiceExit(string applicationId, string sessionId)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            !services.TryGetValue(applicationId, out var process) ||
            !serviceSessions.TryGetValue(applicationId, out var bound) ||
            !ReferenceEquals(bound.Process, process) || bound.SessionId != sessionId ||
            States.GetSnapshot(applicationId)?.SessionId != sessionId) return null;
        try
        {
            if (!process.HasExited) return null;
            if (serviceProcessExits.TryGetValue(applicationId, out var existing) &&
                existing.SessionId == sessionId && existing.ProcessId == process.Id) return existing;
            var exit = new OwnedServiceExit(applicationId, sessionId, process.Id, process.ExitCode);
            serviceProcessExits[applicationId] = exit;
            return exit;
        }
        catch (InvalidOperationException)
        {
            // Failed startup may already have disposed this owned process before removing its registration.
            return null;
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
        drains.Add(DrainOutput(process.StandardOutput));
        drains.Add(DrainOutput(process.StandardError));
        return process;
    }

    private static Task DrainOutput(StreamReader reader) => Task.Factory.StartNew(() =>
    {
        // Windows inherited anonymous pipes use synchronous handles. Async-over-sync
        // reads would occupy shared pool workers for the entire child lifetime.
        // At most two dedicated readers per owned process; EOF releases each reader.
        var buffer = new char[1024];
        try { while (reader.Read(buffer, 0, buffer.Length) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

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
            lock (services) Interlocked.Exchange(ref disposed, 1);
            await lifetime.CancelAsync().ConfigureAwait(false);
            Actions.Shutdown();
            actionDispatch.Writer.TryComplete();
            control.Dispose();
            var failures = new List<Exception>();
            if (receive is not null)
                try { await receive.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (actionClock is not null)
                try { await actionClock.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (actionDispatcher is not null)
                try { await actionDispatcher.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
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
            if (failures.Count != 0) throw new AggregateException("Owned process cleanup failed.", failures);
            await Task.WhenAll(drains).ConfigureAwait(false);
            lifetime.Dispose();
            Interlocked.Exchange(ref disposed, 2);
        }
        finally { lifecycle.Release(); }
    }
}

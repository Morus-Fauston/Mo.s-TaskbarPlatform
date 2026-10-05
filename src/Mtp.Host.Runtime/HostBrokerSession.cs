using System.Diagnostics;
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
public interface IProcessTermination { Task TerminateAsync(Process process, CancellationToken cancellationToken); }
public sealed record BrokerFaultSnapshot(long Generation, string Source, string ExceptionType, string Code, int HResult);
public sealed class OwnedProcessTermination : IProcessTermination
{
    public async Task TerminateAsync(Process process, CancellationToken cancellationToken)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Owns Broker generations and explicitly launched services. Recovery never replays actions.</summary>
public sealed class HostBrokerSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly string brokerPath;
    private readonly string[] applications;
    private readonly IProcessTermination processTermination;
    private readonly Dictionary<string, OwnedServiceRuntime> services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecoveryOperation> recovery = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedServiceExit> exits = new(StringComparer.Ordinal);
    private readonly RecoveryOperation brokerRecovery = new();
    private BrokerGeneration? generation;
    private long nextGeneration;
    private Task clock = Task.CompletedTask;
    private int disposed;
    private string? lastError;
    private int peakPendingRequests;
    private int lastBrokerProcessId;
    private BrokerFaultSnapshot? lastBrokerFault;
    private HostBrokerSession(string brokerPath, IReadOnlyCollection<string> applications, IProcessTermination processTermination)
    {
        this.brokerPath = brokerPath; this.applications = applications.ToArray(); this.processTermination = processTermination;
        States = new BrokerStateStore(applications);
    }
    public BrokerStateStore States { get; }
    public ActionRequestTracker Actions { get; } = new();
    public int BrokerProcessId { get { lock (gate) return lastBrokerProcessId != 0 ? lastBrokerProcessId : throw new InvalidOperationException("Broker not started."); } }
    public string? LastError { get { lock (gate) return lastError; } }
    public BrokerFaultSnapshot? LastBrokerFault { get { lock (gate) return lastBrokerFault; } }
    public int PeakPendingRequests { get { lock (gate) return peakPendingRequests; } }
    public RecoverySnapshot BrokerRecovery { get { lock (gate) return brokerRecovery.Snapshot; } }
    public RecoverySnapshot? GetRecovery(string applicationId) { lock (gate) return recovery.GetValueOrDefault(applicationId)?.Snapshot; }
    public IReadOnlyList<OwnedServiceExit> ServiceProcessExits { get { lock (gate) return Array.AsReadOnly(exits.Values.ToArray()); } }
    public IReadOnlyList<int> ServiceProcessIds { get { lock (gate) return services.Values.Where(value => !value.Stopped).Select(value => value.ProcessId).ToArray(); } }
    public static async Task<HostBrokerSession> StartAsync(string brokerPath, IReadOnlyCollection<string> applications,
        CancellationToken cancellationToken = default, IProcessTermination? processTermination = null)
    {
        var host = new HostBrokerSession(brokerPath, applications, processTermination ?? new OwnedProcessTermination());
        try { await host.StartGenerationAsync(cancellationToken).ConfigureAwait(false); host.clock = host.RunClockAsync(); return host; }
        catch (Exception startError)
        {
            try { await host.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError) { throw new BrokerStartCleanupException(host, startError, cleanupError); }
            throw;
        }
    }
    private async Task<BrokerGeneration> StartGenerationAsync(CancellationToken cancellationToken)
    {
        BrokerGeneration current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (generation is not null) throw new InvalidOperationException("PreviousBrokerStillOwned");
            current = new BrokerGeneration(++nextGeneration, applications, lifetime.Token);
            generation = current; current.Broker = StartOwned(brokerPath, []); lastBrokerProcessId = current.Broker.ProcessId;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(RecoveryLimits.ConnectionTimeoutSeconds));
        string ticket = Secret();
        await LengthPrefixedJson.WriteAsync(current.Broker.Process.StandardInput.BaseStream,
            new BrokerLaunch(current.HostPipeName, ticket, current.ServicePipeName, current.Registrations.Values.ToArray()), deadline.Token).ConfigureAwait(false);
        current.Broker.Process.StandardInput.Close();
        await current.Control.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        var hello = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(current.Control, deadline.Token).ConfigureAwait(false);
        if (hello.Kind != MessageKind.Hello || hello.Version != ProtocolLimits.Version || !SecretEquals(ticket, hello.Ticket)) throw new IOException("BrokerAuthenticationFailed");
        await LengthPrefixedJson.WriteAsync(current.Control, new ProtocolMessage { Kind = MessageKind.Welcome }, deadline.Token).ConfigureAwait(false);
        var ready = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(current.Control, deadline.Token).ConfigureAwait(false);
        if (ready.Kind != MessageKind.Welcome || ready.RequestId != "broker-ready") throw new IOException("BrokerNotReady");
        lock (gate)
        {
            if (!IsCurrent(current)) throw new OperationCanceledException(deadline.Token);
            current.Ready = true; current.Receiver = ReceiveAsync(current); current.Dispatcher = DispatchActionsAsync(current);
            current.PermissionPublisher = PublishDisplayPermissionsAsync(current);
        }
        return current;
    }
    public async Task<int> StartServiceAsync(string applicationId, string executablePath, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? arguments = null)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrokerGeneration current; LaunchRegistration registration;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                if (!applications.Contains(applicationId, StringComparer.Ordinal)) throw new InvalidOperationException("ApplicationNotRegistered");
                if (services.ContainsKey(applicationId)) throw new InvalidOperationException("ApplicationAlreadyStarted");
                current = ReadyGeneration(); registration = current.Registrations[applicationId];
            }
            if (registration.ExpiresAt <= DateTimeOffset.UtcNow) registration = await RegisterLaunchAsync(current, applicationId, cancellationToken).ConfigureAwait(false);
            OwnedServiceRuntime service;
            lock (gate)
            {
                if (!IsCurrent(current) || !current.Ready) throw new IOException("BrokerUnavailable");
                service = StartOwned(executablePath, arguments ?? []); service.BrokerGeneration = current.Id;
                services.Add(applicationId, service); recovery.Add(applicationId, new());
            }
            try { await SendLaunchAsync(service, current, registration, cancellationToken).ConfigureAwait(false); return service.Process.Id; }
            catch
            {
                await StopOwnedAsync(service).ConfigureAwait(false);
                lock (gate) { services.Remove(applicationId); recovery.Remove(applicationId); }
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }
    public async Task<ProtocolResult> SendActionAsync(ActionSlotReference slot, ActionParameter parameter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot); ArgumentNullException.ThrowIfNull(parameter);
        if (cancellationToken.IsCancellationRequested) return ProtocolResult.Reject("ActionCancelled", "操作已取消");
        ActionReservation reserved; BrokerApplicationSnapshot current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (generation is not { Ready: true, Faulted: false } connection) return ProtocolResult.Reject("ActionNotAvailable", "Broker尚未恢复");
            current = States.GetSnapshot(slot.ApplicationId)!;
            if (current is null) return ProtocolResult.Reject("ActionNotAvailable", "应用未连接");
            reserved = Actions.Begin(current, slot, parameter, parameter);
            if (!reserved.Accepted) return reserved.Result;
            var action = reserved.Invocation!;
            if (!connection.Actions.Writer.TryWrite(new ProtocolMessage
            {
                Kind = MessageKind.ActionRequest,
                ApplicationId = current.ApplicationId,
                SessionId = current.SessionId,
                RequestId = action.RequestId,
                Action = action
            }))
                Actions.FailDispatch(current.ApplicationId, current.SessionId, action.RequestId, ProtocolResult.Reject("Busy", "动作派发队列不可用"));
        }
        using var cancel = cancellationToken.Register(() => Actions.Cancel(current.ApplicationId, current.SessionId, reserved.Invocation!.RequestId));
        return await reserved.Completion.ConfigureAwait(false);
    }
    private void QueueDisplayPermissions(BrokerGeneration current, string applicationId)
    {
        if (!IsCurrent(current)) return;
        var state = States.GetSnapshot(applicationId);
        var snapshot = States.GetDisplayPermissions(applicationId);
        if (state is not { IsConnected: true } || snapshot is null || snapshot.Entries.Count == 0 ||
            current.PermissionRevisions.TryGetValue(applicationId, out var previous) && previous.Session == state.SessionId && previous.Revision >= snapshot.Revision) return;
        if (!current.Permissions.TryPublish(new ProtocolMessage
        {
            Kind = MessageKind.DisplayPermissions,
            ApplicationId = applicationId,
            SessionId = state.SessionId,
            RequestId = Guid.NewGuid().ToString("N"),
            Permissions = snapshot
        })) throw new IOException("DisplayPermissionQueueFull");
        current.PermissionRevisions[applicationId] = (state.SessionId, snapshot.Revision);
    }

    private async Task PublishDisplayPermissionsAsync(BrokerGeneration current)
    {
        try
        {
            await foreach (var message in current.Permissions.ReadAllAsync(current.Lifetime.Token).ConfigureAwait(false))
            {
                lock (gate)
                    if (!IsCurrent(current) || States.GetSnapshot(message.ApplicationId) is not { IsConnected: true } snapshot || snapshot.SessionId != message.SessionId) continue;
                await WriteControlAsync(current, message, current.Lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (IsTransportError(error)) { FaultBroker(current, "PermissionPublisher", error); }
        finally { current.Permissions.Complete(); }
    }
    private async Task DispatchActionsAsync(BrokerGeneration current)
    {
        try
        {
            await foreach (var message in current.Actions.Reader.ReadAllAsync(current.Lifetime.Token).ConfigureAwait(false))
            {
                lock (gate) if (!IsCurrent(current) || States.GetSnapshot(message.ApplicationId)?.SessionId != message.SessionId) continue;
                await WriteControlAsync(current, message, current.Lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (IsTransportError(error)) { FaultBroker(current, "Dispatcher", error); }
        finally { current.Actions.Writer.TryComplete(); while (current.Actions.Reader.TryRead(out _)) { } }
    }
    private async Task WriteControlAsync(BrokerGeneration current, ProtocolMessage message, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, current.Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await current.Writer.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { await LengthPrefixedJson.WriteAsync(current.Control, message, deadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (IsTransportError(error)) { FaultBroker(current, "ControlWrite", error); throw; }
        finally { current.Writer.Release(); }
    }
    private async Task ReceiveAsync(BrokerGeneration current)
    {
        try
        {
            while (!current.Lifetime.IsCancellationRequested)
            {
                var message = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(current.Control, current.Lifetime.Token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                ProtocolMessage? response = null;
                lock (gate)
                {
                    if (!IsCurrent(current)) return;
                    if (message.Permissions is not null) throw new IOException("UnexpectedDisplayPermissions");
                    if (message.Kind == MessageKind.Result && current.PendingRegistrations.TryGetValue(message.RequestId, out var pending))
                    {
                        if (message.Version != ProtocolLimits.Version || message.ApplicationId != pending.Application || message.SessionId != "" ||
                            message.Result is null || message.Result.ActivityRejections is not null || message.Result.Code is null || message.Result.Code.Length > 256 ||
                            message.Result.Message is null || message.Result.Message.Length > 1024 || message.Result.Path?.Length > 1024 || !EmptyPayload(message, true)) throw new IOException("InvalidRegistrationResult");
                        pending.Completion.TrySetResult(message.Result); continue;
                    }
                    // A registration ACK can arrive after its waiter timed out; never echo it
                    // back as a service response or attach it to a later registration.
                    if (message.Kind == MessageKind.Result) continue;
                    if (message.Kind == MessageKind.ActionCompleted)
                    {
                        if (message.Version != ProtocolLimits.Version || message.ActionCompletion is not { } completion ||
                            message.RequestId != completion.RequestId || !ValidId(message.ApplicationId) || !ValidId(message.SessionId) ||
                            completion.Result?.ActivityRejections is not null ||
                            message.Action is not null || message.Result is not null || message.Declaration is not null || message.State is not null ||
                            message.Flyout is not null || message.BrokerLoad is not null || message.Heartbeat is not null || message.Registration is not null ||
                            !string.IsNullOrEmpty(message.Ticket) || !string.IsNullOrEmpty(message.StartRequestId)) throw new IOException("InvalidActionCompletionEnvelope");
                        Actions.Receive(message.ApplicationId, message.SessionId, completion, States.GetSnapshot(message.ApplicationId),
                            state => States.ApplyActionState(message.ApplicationId, message.SessionId, state)); continue;
                    }
                    if (message.BrokerLoad is { } load)
                    {
                        if (load.PendingRequests < 0 || load.PeakPendingRequests < load.PendingRequests ||
                            load.PeakPendingRequests > ProtocolLimits.MaximumPendingRequests || load.PeakPendingRequests < current.PeakPending) throw new IOException("InvalidBrokerLoad");
                        current.PeakPending = load.PeakPendingRequests; peakPendingRequests = Math.Max(peakPendingRequests, current.PeakPending);
                    }
                    var previous = States.GetSnapshot(message.ApplicationId);
                    if (message.Kind == MessageKind.Disconnected && TryRecordExit(message.ApplicationId, message.SessionId) is not null)
                        message = message with { Result = ProtocolResult.Reject("ProcessExited", "托管服务进程已退出") };
                    response = States.Handle(message);
                    if (response.Result?.Accepted == true)
                    {
                        if (message.Kind == MessageKind.Welcome && services.TryGetValue(message.ApplicationId, out var service) && service.BrokerGeneration == current.Id) service.SessionId = message.SessionId;
                        if (message.Kind == MessageKind.Declare && services.TryGetValue(message.ApplicationId, out var declared) &&
                            declared.BrokerGeneration == current.Id && declared.SessionId == message.SessionId) declared.InitialConnectionConfirmed = true;
                        if (message.Kind == MessageKind.Disconnected || (message.Kind == MessageKind.Welcome && previous is not null)) Actions.EndSession(message.ApplicationId, previous?.SessionId ?? message.SessionId);
                        if (message.Kind == MessageKind.Disconnected) ScheduleServiceRecovery(message.ApplicationId, current);
                        if (message.Kind == MessageKind.Declare) QueueDisplayPermissions(current, message.ApplicationId);
                    }
                    if (message.Kind is MessageKind.Disconnected or MessageKind.SessionReady) response = null;
                }
                if (response is not null) await WriteControlAsync(current, response, current.Lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (IsTransportError(error)) { FaultBroker(current, "Receive", error); }
        finally { FaultBroker(current, "ReceiveEnded", null); }
    }
    private void FaultBroker(BrokerGeneration current, string source, Exception? error)
    {
        lock (gate)
        {
            if (disposed != 0 || !ReferenceEquals(generation, current) || current.Faulted) return;
            lastBrokerFault = new(current.Id, source, error?.GetType().Name ?? "None", SafeFaultCode(error), error?.HResult ?? 0);
            current.Faulted = true; current.Stop(); lastError = "BrokerUnavailable";
            foreach (var snapshot in States.Snapshots)
            {
                States.Handle(new ProtocolMessage { Kind = MessageKind.Disconnected, ApplicationId = snapshot.ApplicationId, SessionId = snapshot.SessionId });
                Actions.EndSession(snapshot.ApplicationId, snapshot.SessionId);
            }
            ScheduleBrokerRecovery();
        }
    }
    private void ScheduleBrokerRecovery()
    {
        if (disposed != 0 || brokerRecovery.State == RecoveryState.Exhausted || brokerRecovery.Task is { IsCompleted: false }) return;
        brokerRecovery.State = RecoveryState.Recovering;
        brokerRecovery.Task = Task.Run(RecoverBrokerAsync);
    }
    private async Task<ProtocolResult> RecoverBrokerAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(RecoveryLimits.BrokerWindowSeconds));
        try
        {
            BrokerGeneration? old; Task[] serviceTasks;
            lock (gate)
            {
                old = generation; old?.Stop();
                serviceTasks = recovery.Values.Select(value => value.Task).OfType<Task>().ToArray();
            }
            await Task.WhenAll(serviceTasks).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (old is not null) await RetireGenerationAsync(old, deadline.Token).ConfigureAwait(false);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                int attempt;
                lock (gate)
                {
                    if (brokerRecovery.Attempts >= RecoveryLimits.MaximumBrokerAttempts) break;
                    attempt = ++brokerRecovery.Attempts;
                }
                if (attempt > 1) await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt - 1, 2)), deadline.Token).ConfigureAwait(false);
                try
                {
                    var current = await StartGenerationAsync(deadline.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!IsCurrent(current) || current.Faulted) throw new IOException("BrokerUnavailable");
                        lastError = null; brokerRecovery.State = RecoveryState.Available; brokerRecovery.Error = null;
                        foreach (string app in services.Keys) ScheduleServiceRecovery(app, current);
                    }
                    return ProtocolResult.Success("BrokerRecovered");
                }
                catch (Exception error) when (IsTransportError(error))
                {
                    BrokerGeneration? failed; lock (gate) failed = generation;
                    if (failed is not null) await RetireGenerationAsync(failed, deadline.Token).ConfigureAwait(false);
                }
            }
            return ExhaustBroker("BrokerRecoveryExhausted");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return ProtocolResult.Reject("RecoveryCancelled", "恢复已取消"); }
        catch (Exception error) when (IsTransportError(error) || error is AggregateException)
        { return ExhaustBroker(error is OperationCanceledException ? "BrokerRecoveryExhausted" : "BrokerCleanupFailed"); }
    }
    private ProtocolResult ExhaustBroker(string code)
    {
        lock (gate)
        {
            if (disposed == 0) { brokerRecovery.State = RecoveryState.Exhausted; brokerRecovery.Error = code; lastError = code; }
            return ProtocolResult.Reject(code, "Broker恢复未完成，请手动重试");
        }
    }
    private void ScheduleServiceRecovery(string app, BrokerGeneration current)
    {
        if (!IsCurrent(current) || !current.Ready || current.Faulted || !services.ContainsKey(app) ||
            !recovery.TryGetValue(app, out var operation) || operation.State == RecoveryState.Exhausted || operation.Task is { IsCompleted: false }) return;
        States.RequireSessionReady(app); operation.State = RecoveryState.Recovering;
        operation.Task = Task.Run(() => RecoverServiceAsync(app, current, operation));
    }
    private async Task<ProtocolResult> RecoverServiceAsync(string app, BrokerGeneration current, RecoveryOperation operation)
    {
        string? previousSession; lock (gate) previousSession = States.GetSnapshot(app)?.SessionId;
        long started = Stopwatch.GetTimestamp();
        try
        {
            for (int index = 0; index < RecoveryLimits.MaximumTicketAttempts; index++)
            {
                if (await WaitForReadyUntilAsync(app, current, previousSession, started, RecoveryLimits.TicketAttemptSeconds[index]).ConfigureAwait(false)) return ServiceRecovered(operation);
                OwnedServiceRuntime service; bool canSend;
                lock (gate)
                {
                    if (!IsCurrent(current)) throw new OperationCanceledException(current.Lifetime.Token);
                    service = services[app]; canSend = operation.Attempts < RecoveryLimits.MaximumTicketAttempts;
                    if (canSend) operation.Attempts++;
                }
                if (canSend && !service.Stopped && !service.Process.HasExited && !service.InputFailed)
                {
                    try
                    {
                        var registration = await RegisterLaunchAsync(current, app, current.Lifetime.Token).ConfigureAwait(false);
                        await SendLaunchAsync(service, current, registration, current.Lifetime.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (IsTransportError(error))
                    {
                        lock (gate) if (IsCurrent(current)) operation.Error = error is ProtocolException protocol ? protocol.Code : "ServiceReconnectFailed";
                    }
                }
            }
            if (await WaitForReadyUntilAsync(app, current, previousSession, started, RecoveryLimits.ServiceWindowSeconds).ConfigureAwait(false)) return ServiceRecovered(operation);
            OwnedServiceRuntime previous;
            lock (gate)
            {
                if (!IsCurrent(current)) throw new OperationCanceledException(current.Lifetime.Token);
                if (operation.Restarts >= RecoveryLimits.MaximumServiceRestarts) return ServiceExhausted(operation, "ServiceRecoveryExhausted");
                previous = services[app]; operation.Restarts++;
            }
            await StopOwnedAsync(previous).ConfigureAwait(false);
            // Retain launch configuration even if registration fails after process cleanup.
            using var restartDeadline = CancellationTokenSource.CreateLinkedTokenSource(current.Lifetime.Token);
            restartDeadline.CancelAfter(TimeSpan.FromSeconds(RecoveryLimits.ConnectionTimeoutSeconds));
            long restarted = Stopwatch.GetTimestamp();
            var replacementRegistration = await RegisterAfterExitAsync(current, app, restartDeadline.Token).ConfigureAwait(false);
            OwnedServiceRuntime replacement;
            lock (gate)
            {
                if (!IsCurrent(current)) throw new OperationCanceledException(current.Lifetime.Token);
                replacement = StartOwned(previous.Path, previous.Arguments); replacement.BrokerGeneration = current.Id;
                services[app] = replacement; exits.Remove(app);
            }
            await SendLaunchAsync(replacement, current, replacementRegistration, restartDeadline.Token).ConfigureAwait(false);
            if (await WaitForReadyUntilAsync(app, current, previousSession, restarted, RecoveryLimits.ConnectionTimeoutSeconds).ConfigureAwait(false)) return ServiceRecovered(operation);
            return ServiceExhausted(operation, "ServiceRecoveryExhausted");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || current.Lifetime.IsCancellationRequested)
        { return ProtocolResult.Reject("RecoveryCancelled", "恢复已取消"); }
        catch (Exception error) when (IsTransportError(error) || error is AggregateException)
        { return ServiceExhausted(operation, error is ProtocolException protocol ? protocol.Code : "ServiceRecoveryFailed"); }
    }
    private async Task<bool> WaitForReadyUntilAsync(string app, BrokerGeneration current, string? previousSession, long started, int seconds)
    {
        while (true)
        {
            current.Lifetime.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!IsCurrent(current)) throw new OperationCanceledException(current.Lifetime.Token);
                var snapshot = States.GetSnapshot(app);
                if (snapshot is { IsInteractive: true } && snapshot.SessionId != previousSession) return true;
            }
            if (Stopwatch.GetElapsedTime(started).TotalSeconds >= seconds) return false;
            await Task.Delay(50, current.Lifetime.Token).ConfigureAwait(false);
        }
    }
    private ProtocolResult ServiceRecovered(RecoveryOperation operation)
    {
        lock (gate) { operation.State = RecoveryState.Available; operation.Error = null; }
        return ProtocolResult.Success("ServiceRecovered");
    }
    private ProtocolResult ServiceExhausted(RecoveryOperation operation, string code)
    {
        lock (gate) if (disposed == 0) { operation.State = RecoveryState.Exhausted; operation.Error = code; }
        return ProtocolResult.Reject(code, "服务恢复未完成，请手动重试");
    }
    public async Task<ProtocolResult> RetryBrokerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Task<ProtocolResult> task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (brokerRecovery.Task is { IsCompleted: false }) return ProtocolResult.Reject("RecoveryInProgress", "Broker正在恢复");
            if (generation is { Ready: true, Faulted: false }) return ProtocolResult.Success("AlreadyAvailable");
            brokerRecovery.Reset(); ScheduleBrokerRecovery(); task = brokerRecovery.Task!;
        }
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<ProtocolResult> RetryApplicationAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Task<ProtocolResult> task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!recovery.TryGetValue(applicationId, out var operation) || !services.ContainsKey(applicationId)) return ProtocolResult.Reject("ApplicationNotOwned", "应用没有托管服务");
            if (operation.Task is { IsCompleted: false }) return ProtocolResult.Reject("RecoveryInProgress", "服务正在恢复");
            if (States.GetSnapshot(applicationId)?.IsInteractive == true) return ProtocolResult.Success("AlreadyAvailable");
            if (generation is not { Ready: true, Faulted: false } current) return ProtocolResult.Reject("BrokerUnavailable", "请先恢复Broker");
            operation.Reset(); ScheduleServiceRecovery(applicationId, current); task = operation.Task!;
        }
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private async Task<LaunchRegistration> RegisterAfterExitAsync(BrokerGeneration current, string app, CancellationToken token)
    {
        // Exit confirmation can precede the Broker reader's final session removal.
        // Retry only that explicit rejection within the replacement connection deadline.
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await RegisterLaunchAsync(current, app, token).ConfigureAwait(false); }
            catch (ProtocolException error) when (error.Code == "AlreadyConnected")
            { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }
    private async Task<LaunchRegistration> RegisterLaunchAsync(BrokerGeneration current, string app, CancellationToken token)
    {
        var registration = NewRegistration(app); string request = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!IsCurrent(current) || !current.Ready) throw new IOException("BrokerUnavailable");
            if (current.PendingRegistrations.Count >= ProtocolLimits.MaximumApplications) throw new IOException("RegistrationQueueFull");
            current.PendingRegistrations.Add(request, (app, completion));
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, current.Lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await WriteControlAsync(current, new ProtocolMessage { Kind = MessageKind.RegisterLaunch, ApplicationId = app, RequestId = request, Registration = registration }, deadline.Token).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!result.Accepted) throw new ProtocolException(result.Code);
            lock (gate)
            {
                if (!IsCurrent(current)) throw new OperationCanceledException(current.Lifetime.Token);
                current.Registrations[app] = registration;
            }
            return registration;
        }
        finally { lock (gate) current.PendingRegistrations.Remove(request); }
    }
    private async Task SendLaunchAsync(OwnedServiceRuntime service, BrokerGeneration current, LaunchRegistration registration, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, current.Lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await service.InputWriter.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (!IsCurrent(current) || service.InputFailed) throw new IOException("ServiceLaunchChannelUnavailable");
                service.BrokerGeneration = current.Id;
            }
            await LengthPrefixedJson.WriteAsync(service.Process.StandardInput.BaseStream,
                new ServiceLaunch(current.ServicePipeName, registration.ApplicationId, registration.StartRequestId, registration.Ticket), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsTransportError(error)) { service.InputFailed = true; service.Process.StandardInput.Close(); throw; }
        finally { service.InputWriter.Release(); }
    }
    private async Task RunClockAsync()
    {
        int ticks = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(250, lifetime.Token).ConfigureAwait(false); Actions.Tick();
                if (++ticks < 4) continue; ticks = 0;
                States.TickActivities();
                lock (gate)
                {
                    if (disposed != 0) continue;
                    if (generation is { Ready: true, Faulted: false } permissionsGeneration)
                        foreach (var snapshot in States.Snapshots) QueueDisplayPermissions(permissionsGeneration, snapshot.ApplicationId);
                    if (generation is { Faulted: true }) ScheduleBrokerRecovery();
                    foreach (var pair in services)
                    {
                        string? session = pair.Value.SessionId;
                        if (!pair.Value.InitialConnectionConfirmed &&
                            Stopwatch.GetElapsedTime(pair.Value.StartedAt).TotalSeconds >= RecoveryLimits.ConnectionTimeoutSeconds &&
                            recovery.TryGetValue(pair.Key, out var initial) && initial.State == RecoveryState.Available &&
                            generation is { Ready: true, Faulted: false } connected)
                        {
                            initial.Error = "InitialConnectionTimedOut";
                            if (session is not null)
                            {
                                States.Handle(new ProtocolMessage { Kind = MessageKind.Disconnected, ApplicationId = pair.Key, SessionId = session });
                                Actions.EndSession(pair.Key, session);
                            }
                            ScheduleServiceRecovery(pair.Key, connected);
                        }
                        if (session is null || (exits.TryGetValue(pair.Key, out var observed) && observed.SessionId == session)) continue;
                        var exit = TryRecordExit(pair.Key, session);
                        if (exit is null) continue;
                        States.Handle(new ProtocolMessage
                        {
                            Kind = MessageKind.Disconnected,
                            ApplicationId = pair.Key,
                            SessionId = session,
                            Result = ProtocolResult.Reject("ProcessExited", "托管服务进程已退出")
                        });
                        Actions.EndSession(pair.Key, session);
                        if (generation is { Ready: true } current) ScheduleServiceRecovery(pair.Key, current);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private OwnedServiceExit? TryRecordExit(string app, string session)
    {
        if (disposed != 0 || !services.TryGetValue(app, out var service) || service.SessionId != session ||
            States.GetSnapshot(app)?.SessionId != session || service.Stopped) return null;
        try
        {
            if (!service.Process.HasExited) return null;
            if (exits.TryGetValue(app, out var existing) && existing.SessionId == session && existing.ProcessId == service.ProcessId) return existing;
            var exit = new OwnedServiceExit(app, session, service.ProcessId, service.Process.ExitCode); exits[app] = exit; return exit;
        }
        catch (InvalidOperationException) { return null; }
    }
    private OwnedServiceRuntime StartOwned(string path, IReadOnlyList<string> arguments)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Registered runtime executable missing.", path);
        if (arguments.Count > 16 || arguments.Any(value => value is null || value.Length > 1024)) throw new ArgumentException("Registered process arguments exceed the launch budget.", nameof(arguments));
        var info = new ProcessStartInfo
        {
            FileName = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Path.GetFullPath(path));
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return new(Process.Start(info) ?? throw new IOException("ProcessStartFailed"), path, arguments);
    }
    private async Task StopOwnedAsync(OwnedServiceRuntime owned, CancellationToken cancellationToken = default)
    {
        if (owned.Stopped) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        deadline.Token.ThrowIfCancellationRequested();
        await processTermination.TerminateAsync(owned.Process, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        if (!owned.Process.HasExited) throw new IOException("ProcessExitNotConfirmed");
        owned.Process.StandardInput.Close();
        await Task.WhenAll(owned.OutputDrain, owned.ErrorDrain).WaitAsync(deadline.Token).ConfigureAwait(false);
        lock (gate) { owned.Stopped = true; owned.Process.Dispose(); }
    }
    private async Task RetireGenerationAsync(BrokerGeneration current, CancellationToken cancellationToken = default)
    {
        lock (gate) current.Stop();
        await Task.WhenAll(current.Receiver, current.Dispatcher, current.PermissionPublisher).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (current.Broker is not null) { await StopOwnedAsync(current.Broker, cancellationToken).ConfigureAwait(false); current.Broker = null; }
        lock (gate) if (ReferenceEquals(generation, current)) generation = null;
        current.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Task[] recovering;
            lock (gate)
            {
                if (disposed == 2) return;
                disposed = 1; lifetime.Cancel(); generation?.Stop(); Actions.Shutdown();
                recovering = recovery.Values.Select(value => value.Task).Append(brokerRecovery.Task).OfType<Task>().ToArray();
            }
            var failures = new List<Exception>();
            try { await Task.WhenAll(recovering).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { await clock.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            KeyValuePair<string, OwnedServiceRuntime>[] owned; lock (gate) owned = services.ToArray();
            foreach (var pair in owned)
                try { await StopOwnedAsync(pair.Value).ConfigureAwait(false); lock (gate) services.Remove(pair.Key); }
                catch (Exception error) { failures.Add(error); }
            BrokerGeneration? remaining; lock (gate) remaining = generation;
            if (remaining is not null)
                try { await RetireGenerationAsync(remaining).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException("Owned process cleanup failed.", failures);
            lifetime.Dispose(); lock (gate) disposed = 2;
        }
        finally { lifecycle.Release(); }
    }
    private bool IsCurrent(BrokerGeneration current) => disposed == 0 && ReferenceEquals(generation, current) && !current.Lifetime.IsCancellationRequested;
    private BrokerGeneration ReadyGeneration() => generation is { Ready: true, Faulted: false } current ? current : throw new IOException("BrokerUnavailable");
    internal static LaunchRegistration NewRegistration(string app) => new(app, Guid.NewGuid().ToString("N"), Secret(), DateTimeOffset.UtcNow.AddSeconds(ProtocolLimits.TicketLifetimeSeconds));
    private static bool IsTransportError(Exception error) => error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException;
    private static string SafeFaultCode(Exception? error)
    {
        // Never retain arbitrary exception messages, paths, envelopes or credentials.
        string? code = error is ProtocolException protocol ? protocol.Code : error?.Message;
        return code is "end_of_stream" or "invalid_length" or "frame_timeout" or "truncated_frame" or "invalid_utf8" or "invalid_json" or
            "InvalidBrokerLoad" or "InvalidRegistrationResult" or "InvalidActionCompletionEnvelope" ? code : error?.GetType().Name ?? "ConnectionEnded";
    }
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static bool EmptyPayload(ProtocolMessage message, bool allowResult = false) => message.Declaration is null && message.State is null &&
        (allowResult || message.Result is null) && message.BrokerLoad is null && message.Flyout is null && message.Action is null &&
        message.ActionCompletion is null && message.Heartbeat is null && message.Registration is null && message.Permissions is null && message.Ticket == "" && message.StartRequestId == "";
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static bool SecretEquals(string expected, string actual) => actual is not null && actual.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));
}

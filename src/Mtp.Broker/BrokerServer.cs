using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Mtp.Contracts;
using Mtp.Transport;

namespace Mtp.Broker;

/// <summary>Owns transport sessions only. Host remains the authority for declarations and state.</summary>
public sealed class BrokerServer : IAsyncDisposable
{
    private readonly BrokerLaunch launch;
    private readonly Dictionary<string, LaunchRegistration> registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<ProtocolMessage>> pending = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Dictionary<string, ServiceConnection> connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly NamedPipeClientStream control;
    private int disposed;
    private int started;
    private int peakPendingRequests;
    private readonly HeartbeatMonitor heartbeats = new();
    private readonly HashSet<string> allowedApplications;
    private readonly Dictionary<string, string> lastLaunchIds = new(StringComparer.Ordinal);
    private readonly Channel<ProtocolMessage> registrationReplies = Channel.CreateBounded<ProtocolMessage>(
        new BoundedChannelOptions(ProtocolLimits.MaximumApplications) { SingleReader = true, SingleWriter = true });

    public BrokerServer(BrokerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.Registrations is null || launch.Registrations.Count > ProtocolLimits.MaximumApplications ||
            string.IsNullOrWhiteSpace(launch.HostTicket) || launch.HostTicket.Length > 256)
            throw new ProtocolException("InvalidLaunch");
        var latestExpiry = DateTimeOffset.UtcNow.AddSeconds(ProtocolLimits.TicketLifetimeSeconds);
        foreach (var registration in launch.Registrations)
        {
            if (registration is null || !ValidIdentity(registration.ApplicationId) || !ValidIdentity(registration.StartRequestId) ||
                string.IsNullOrWhiteSpace(registration.Ticket) || registration.Ticket.Length > 256 ||
                !registrations.TryAdd(registration.ApplicationId, registration with { ExpiresAt = registration.ExpiresAt < latestExpiry ? registration.ExpiresAt : latestExpiry }))
                throw new ProtocolException("InvalidLaunch");
        }
        allowedApplications = registrations.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var registration in registrations.Values) lastLaunchIds.Add(registration.ApplicationId, registration.StartRequestId);
        this.launch = launch;
        control = new NamedPipeClientStream(".", launch.HostPipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("BrokerAlreadyStarted");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        await control.ConnectAsync(5000, token).ConfigureAwait(false);
        await LengthPrefixedJson.WriteAsync(control, new ProtocolMessage { Kind = MessageKind.Hello, Ticket = launch.HostTicket }, token).ConfigureAwait(false);
        var accepted = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(control, token).ConfigureAwait(false);
        if (accepted.Version != ProtocolLimits.Version || accepted.Kind != MessageKind.Welcome ||
            accepted.ApplicationId != "" || accepted.SessionId != "" || accepted.RequestId != "" ||
            accepted.Ticket != "" || accepted.StartRequestId != "" || accepted.Declaration is not null || accepted.State is not null || accepted.Result is not null || accepted.BrokerLoad is not null || accepted.Flyout is not null || accepted.Action is not null || accepted.ActionCompletion is not null || accepted.Heartbeat is not null || accepted.Registration is not null)
            throw new ProtocolException("HostHandshakeRejected");

        var listeners = new List<NamedPipeServerStream>();
        var workers = new List<Task>();
        Task? receiver = null;
        Task? heartbeatClock = null;
        try
        {
            // Create the listeners before ready, so Host cannot race pipe creation.
            for (var i = 0; i < ProtocolLimits.MaximumApplications; i++) listeners.Add(CreateListener());
            workers.Add(SendRegistrationRepliesAsync(token));
            receiver = ReceiveHostAsync(token);
            await WriteHostAsync(new ProtocolMessage { Kind = MessageKind.Welcome, RequestId = "broker-ready" }, token).ConfigureAwait(false);
            foreach (var listener in listeners) workers.Add(ServeSlotAsync(listener, token));
            heartbeatClock = RunHeartbeatClockAsync(token);
            workers.Add(heartbeatClock);
            var first = await Task.WhenAny(workers.Append(receiver)).ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            registrationReplies.Writer.TryComplete();
            CancelPending();
            foreach (var listener in listeners) await listener.DisposeAsync().ConfigureAwait(false);
            if (receiver is not null) workers.Add(receiver);
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
            heartbeats.Shutdown();
        }
    }

    private async Task RunHeartbeatClockAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(HeartbeatMonitor.DetectionInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                foreach (var fault in heartbeats.Tick())
                    lock (gate)
                        if (connections.TryGetValue(fault.ApplicationId, out var current) && current.SessionId == fault.SessionId)
                            current.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private NamedPipeServerStream CreateListener() => new(launch.ServicePipeName, PipeDirection.InOut,
        ProtocolLimits.MaximumApplications, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ServeSlotAsync(NamedPipeServerStream initial, CancellationToken token)
    {
        var pipe = initial;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                await ServeConnectionAsync(pipe, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // A malformed or stalled connection consumes one bounded slot until its deadline.
            }
            finally { await pipe.DisposeAsync().ConfigureAwait(false); }
            if (token.IsCancellationRequested) break;
            pipe = CreateListener();
        }
    }

    private async Task ServeConnectionAsync(Stream pipe, CancellationToken token)
    {
        string? application = null;
        string? session = null;
        ServiceConnection? connection = null;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, handshake.Token).ConfigureAwait(false);
            var rejection = ValidateHello(hello, out session);
            if (rejection is not null)
            {
                await ReplyAsync(pipe, hello, rejection, handshake.Token).ConfigureAwait(false);
                return;
            }

            application = hello.ApplicationId;
            var welcome = hello with { Kind = MessageKind.Welcome, SessionId = session!, Ticket = "", StartRequestId = "" };
            var confirmation = await ForwardAsync(welcome, handshake.Token).ConfigureAwait(false);
            if (confirmation.Result?.Accepted != true)
            {
                await ReplyAsync(pipe, hello, confirmation.Result ?? ProtocolResult.Reject("HostUnavailable", "平台暂不可用"), handshake.Token).ConfigureAwait(false);
                return;
            }
            connection = new ServiceConnection(application, session!, pipe, token);
            lock (gate)
            {
                connections.Add(application, connection);
                if (!heartbeats.StartSession(application, session!).Accepted) throw new ProtocolException("InvalidHeartbeatSession");
            }
            connection.OrdinaryWorker = ProcessOrdinaryRequestsAsync(connection);
            await connection.WriteAsync(welcome, handshake.Token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                var message = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (message.Version != ProtocolLimits.Version)
                {
                    await connection.ReplyAsync(message, ProtocolResult.Reject("UnsupportedVersion", "协议版本不支持"), token).ConfigureAwait(false);
                    return;
                }
                if (message.ApplicationId != application || message.SessionId != session ||
                    !sessions.TryGetValue(application, out var current) || current != session)
                {
                    await connection.ReplyAsync(message, ProtocolResult.Reject("SessionMismatch", "会话身份不匹配"), token).ConfigureAwait(false);
                    continue;
                }
                if (message.Kind == MessageKind.Heartbeat)
                {
                    if (!ValidServicePayload(message)) throw new ProtocolException("InvalidHeartbeat");
                    var pulse = message.Heartbeat!;
                    // Duplicate pulses and ordinary business traffic never refresh liveness.
                    if (heartbeats.Receive(application, session!, pulse.Sequence, pulse.SentAt).Accepted)
                    {
                        lock (gate) connection.HasHeartbeat = true;
                        await NotifySessionReadyAsync(connection).ConfigureAwait(false);
                    }
                    continue;
                }
                if (message.Kind == MessageKind.ActionCompleted)
                {
                    if (!ValidServicePayload(message)) throw new ProtocolException("InvalidActionResult");
                    await CompleteActionAsync(connection, message, token).ConfigureAwait(false);
                    continue;
                }
                if (!ValidServicePayload(message))
                {
                    await connection.ReplyAsync(message, ProtocolResult.Reject("InvalidEnvelope", "消息字段与类型不匹配"), token).ConfigureAwait(false);
                    continue;
                }
                if (message.Kind == MessageKind.Declare) lock (gate) connection.Declaration = null;
                if (!connection.TrySubmit(message))
                {
                    await connection.ReplyAsync(message, ProtocolResult.Reject("Busy", "上一条请求仍在等待确认"), token).ConfigureAwait(false);
                    continue;
                }

            }
        }
        finally
        {
            HeartbeatFailureReason failure = HeartbeatFailureReason.PipeDisconnected;
            if (application is not null && session is not null)
            {
                heartbeats.ReportFault(application, session, HeartbeatFailureReason.PipeDisconnected);
                failure = heartbeats.GetSnapshot(application)?.Failure ?? failure;
            }
            if (connection is not null)
            {
                lock (gate)
                {
                    if (connections.TryGetValue(connection.ApplicationId, out var current) && ReferenceEquals(current, connection))
                        connections.Remove(connection.ApplicationId);
                    connection.Declaration = null;
                    connection.Outstanding.Clear();
                }
                connection.Stop();
                await connection.OrdinaryWorker.ConfigureAwait(false);
                Task[] sends;
                lock (gate) sends = connection.ActionSends.ToArray();
                await Task.WhenAll(sends).ConfigureAwait(false);
                connection.DisposeLifetime();
            }
            if (application is not null && session is not null &&
                sessions.TryRemove(new KeyValuePair<string, string>(application, session)))
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    await WriteHostAsync(new ProtocolMessage
                    {
                        Kind = MessageKind.Disconnected,
                        ApplicationId = application,
                        SessionId = session,
                        Result = ProtocolResult.Reject(failure.ToString(), failure == HeartbeatFailureReason.HeartbeatTimedOut ? "应用心跳超时" : "应用管道已断开")
                    }, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
                finally { heartbeats.EndSession(application, session); }
            }
        }
    }

    private async Task ProcessOrdinaryRequestsAsync(ServiceConnection connection)
    {
        try
        {
            await foreach (var message in connection.Requests.Reader.ReadAllAsync(connection.Token).ConfigureAwait(false))
                await ForwardServiceRequestAsync(connection, message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connection.Token.IsCancellationRequested) { }
        finally { while (connection.Requests.Reader.TryRead(out _)) { } }
    }

    private async Task ForwardServiceRequestAsync(ServiceConnection connection, ProtocolMessage message)
    {
        try
        {
            if (message.Kind == MessageKind.Declare) lock (gate) connection.Declaration = null;
            var result = await ForwardAsync(message, connection.Token).ConfigureAwait(false);
            if (message.Kind == MessageKind.Declare)
            {
                lock (gate)
                    if (IsCurrent(connection)) connection.Declaration = result.Result?.Accepted == true ? message.Declaration : null;
            }
            if (message.Kind == MessageKind.Declare && result.Result?.Accepted == true)
                await NotifySessionReadyAsync(connection).ConfigureAwait(false);
            await connection.WriteAsync(result, connection.Token, releaseOrdinary: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { connection.Stop(); }
    }

    private async Task NotifySessionReadyAsync(ServiceConnection connection)
    {
        lock (gate)
        {
            if (!IsCurrent(connection) || connection.ReadySent || connection.Declaration is null || !connection.HasHeartbeat) return;
            connection.ReadySent = true;
        }
        await WriteHostAsync(new ProtocolMessage
        {
            Kind = MessageKind.SessionReady,
            ApplicationId = connection.ApplicationId,
            SessionId = connection.SessionId,
            RequestId = Guid.NewGuid().ToString("N")
        }, connection.Token).ConfigureAwait(false);
    }

    private bool IsCurrent(ServiceConnection connection) => connections.TryGetValue(connection.ApplicationId, out var current) && ReferenceEquals(current, connection);

    private async Task DispatchActionAsync(ProtocolMessage message, CancellationToken token)
    {
        if (message.Version != ProtocolLimits.Version || !ValidIdentity(message.RequestId) ||
            message.Ticket != "" || message.StartRequestId != "" || message.Result is not null || message.Declaration is not null ||
            message.State is not null || message.BrokerLoad is not null || message.Flyout is not null || message.ActionCompletion is not null || message.Heartbeat is not null || message.Registration is not null ||
            message.Action is not { } action || action.RequestId != message.RequestId || action.Sequence <= 0)
            throw new ProtocolException("InvalidHostAction");
        ProtocolResult? rejection = null;
        ServiceConnection? connection;
        lock (gate)
        {
            connections.TryGetValue(message.ApplicationId, out connection);
            if (connection is null || connection.SessionId != message.SessionId || !ValidSlot(connection.Declaration, action))
                rejection = ProtocolResult.Reject("ActionNotAvailable", "动作槽位当前不可用");
            else if (action.Sequence <= connection.LastSequence)
            {
                // An in-flight duplicate must not terminate the original request or execute it again.
                if (connection.Outstanding.TryGetValue(action.RequestId, out var original) && original.Sequence == action.Sequence) return;
                rejection = ProtocolResult.Reject("DuplicateRequest", "动作序号已处理");
            }
            else
            {
                connection.LastSequence = action.Sequence;
                if (connection.Outstanding.Count >= ActionLimits.MaximumOutstandingPerApplication || connection.Outstanding.ContainsKey(action.RequestId))
                    rejection = ProtocolResult.Reject("Busy", "动作队列已满或请求标识重复");
                else
                {
                    connection.Outstanding.Add(action.RequestId, action);
                    // Start bounded sends without holding the Host reader behind a slow service.
                    var send = SendActionAsync(connection, message);
                    connection.ActionSends.RemoveAll(task => task.IsCompleted);
                    connection.ActionSends.Add(send);
                }
            }
        }
        if (rejection is not null)
            await WriteHostAsync(message with
            {
                Kind = MessageKind.ActionCompleted,
                Action = null,
                ActionCompletion = new(action.RequestId, action.Sequence, rejection)
            }, token).ConfigureAwait(false);
    }

    private async Task SendActionAsync(ServiceConnection connection, ProtocolMessage message)
    {
        try { await connection.WriteAsync(message, connection.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { connection.Stop(); }
    }

    private async Task CompleteActionAsync(ServiceConnection connection, ProtocolMessage message, CancellationToken token)
    {
        var completion = message.ActionCompletion!;
        lock (gate)
        {
            if (!IsCurrent(connection) || !connection.Outstanding.TryGetValue(message.RequestId, out var request) || request.Sequence != completion.Sequence) return;
            connection.Outstanding.Remove(message.RequestId);
            connection.LastCompletion = completion;
        }
        // Preserve the Host request identity; ordinary forwarding correlations do not apply to actions.
        await WriteHostAsync(message, token).ConfigureAwait(false);
    }

    private static bool ValidSlot(ApplicationDeclaration? declaration, ActionInvocation action)
    {
        var slot = action.Slot;
        var parameter = action.Parameter;
        if (declaration is null || slot is null || parameter is null || slot.ApplicationId != declaration.ApplicationId ||
            !ValidIdentity(slot.ApplicationId) || !ValidIdentity(slot.FeatureGroupId) || !ValidIdentity(slot.EntryId) || !ValidIdentity(slot.ActionSlotId)) return false;
        var group = declaration.FeatureGroups?.FirstOrDefault(group => group.FeatureGroupId == slot.FeatureGroupId);
        var slots = slot.EntryKind switch
        {
            ActionEntryKind.Component => group?.Components?.FirstOrDefault(entry => entry.ComponentId == slot.EntryId)?.ActionSlots,
            ActionEntryKind.TaskbarFlyout => group?.TaskbarFlyouts?.FirstOrDefault(entry => entry.TaskbarFlyoutId == slot.EntryId)?.ActionSlots,
            _ => null
        };
        var declared = slots?.FirstOrDefault(candidate => candidate.ActionSlotId == slot.ActionSlotId);
        if (declared is null || parameter.Kind != declared.ParameterKind) return false;
        return parameter.Kind switch
        {
            ActionParameterKind.None => parameter.Boolean is null && parameter.Number is null && parameter.Text is null,
            ActionParameterKind.Boolean => parameter.Boolean is not null && parameter.Number is null && parameter.Text is null,
            ActionParameterKind.Number => parameter.Boolean is null && parameter.Number is { } number && double.IsFinite(number) && parameter.Text is null,
            ActionParameterKind.Text => parameter.Boolean is null && parameter.Number is null && parameter.Text is { Length: <= ProtocolLimits.MaximumTextLength },
            _ => false
        };
    }

    private static bool ValidResult(ProtocolResult? result) => result is not null && ValidIdentity(result.Code) &&
        result.Message is not null && result.Message.Length <= ProtocolLimits.MaximumTextLength &&
        (result.Path is null || result.Path.Length <= ProtocolLimits.MaximumTextLength);

    private sealed class ServiceConnection(string applicationId, string sessionId, Stream pipe, CancellationToken token)
    {
        private readonly SemaphoreSlim writer = new(1, 1);
        private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        private int stopped;
        public string ApplicationId { get; } = applicationId;
        public string SessionId { get; } = sessionId;
        public CancellationToken Token => lifetime.Token;
        public ApplicationDeclaration? Declaration { get; set; }
        public bool HasHeartbeat { get; set; }
        public bool ReadySent { get; set; }
        public long LastSequence { get; set; }
        public ActionCompletion? LastCompletion { get; set; }
        public Dictionary<string, ActionInvocation> Outstanding { get; } = new(StringComparer.Ordinal);
        public List<Task> ActionSends { get; } = [];
        private int ordinaryBusy;
        public Channel<ProtocolMessage> Requests { get; } = Channel.CreateBounded<ProtocolMessage>(
            new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        public Task OrdinaryWorker { get; set; } = Task.CompletedTask;
        public bool TrySubmit(ProtocolMessage message)
        {
            if (Interlocked.CompareExchange(ref ordinaryBusy, 1, 0) != 0) return false;
            if (Requests.Writer.TryWrite(message)) return true;
            Volatile.Write(ref ordinaryBusy, 0);
            return false;
        }
        public async Task WriteAsync(ProtocolMessage message, CancellationToken token, bool releaseOrdinary = false)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await writer.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                // A peer may submit immediately after receiving this response. Release admission
                // before bytes become visible; the one-slot channel and worker preserve ordering.
                if (releaseOrdinary) Volatile.Write(ref ordinaryBusy, 0);
                await LengthPrefixedJson.WriteAsync(pipe, message, deadline.Token).ConfigureAwait(false);
            }
            finally { writer.Release(); }
        }
        public Task ReplyAsync(ProtocolMessage message, ProtocolResult result, CancellationToken token) => WriteAsync(new ProtocolMessage
        {
            Kind = MessageKind.Result,
            ApplicationId = message.ApplicationId,
            SessionId = message.SessionId,
            RequestId = message.RequestId,
            Result = result
        }, token);
        public void Stop()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            lifetime.Cancel();
            Requests.Writer.TryComplete();
            pipe.Dispose();
        }
        public void DisposeLifetime() => lifetime.Dispose();
    }

    private ProtocolResult? ValidateHello(ProtocolMessage hello, out string? session)
    {
        session = null;
        if (hello.Version != ProtocolLimits.Version) return ProtocolResult.Reject("UnsupportedVersion", "协议版本不支持");
        if (hello.Kind != MessageKind.Hello || !ValidIdentity(hello.ApplicationId) || !ValidIdentity(hello.StartRequestId) || !ValidIdentity(hello.RequestId) ||
            string.IsNullOrWhiteSpace(hello.Ticket) || hello.Ticket.Length > 256 || hello.SessionId != "" ||
            hello.Declaration is not null || hello.State is not null || hello.Result is not null || hello.BrokerLoad is not null || hello.Flyout is not null || hello.Action is not null || hello.ActionCompletion is not null || hello.Heartbeat is not null || hello.Registration is not null)
            return ProtocolResult.Reject("InvalidHandshake", "连接声明无效");
        lock (gate)
        {
            if (!registrations.TryGetValue(hello.ApplicationId, out var registration))
                return ProtocolResult.Reject("InvalidTicket", "没有匹配的有效启动登记");
            if (registration.ExpiresAt <= DateTimeOffset.UtcNow)
                return ProtocolResult.Reject("TicketExpired", "启动票据已过期");
            if (registration.StartRequestId != hello.StartRequestId ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(registration.Ticket), Encoding.UTF8.GetBytes(hello.Ticket)))
                return ProtocolResult.Reject("InvalidTicket", "启动身份或票据无效");
            var issuedSession = Guid.NewGuid().ToString("N");
            if (!sessions.TryAdd(hello.ApplicationId, issuedSession))
                return ProtocolResult.Reject("AlreadyConnected", "应用已有当前连接");
            session = issuedSession;
            registrations.Remove(hello.ApplicationId);
        }
        return null;
    }

    private async Task<ProtocolMessage> ForwardAsync(ProtocolMessage message, CancellationToken token)
    {
        var correlation = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ProtocolMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (pending.Count >= ProtocolLimits.MaximumPendingRequests)
                return new ProtocolMessage
                {
                    Kind = MessageKind.Result,
                    ApplicationId = message.ApplicationId,
                    SessionId = message.SessionId,
                    RequestId = message.RequestId,
                    Result = ProtocolResult.Reject("Busy", "平台通信繁忙")
                };
            pending.Add(correlation, completion);
            peakPendingRequests = Math.Max(peakPendingRequests, pending.Count);
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await WriteHostAsync(message with { RequestId = correlation }, deadline.Token, includeBrokerLoad: true).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (result.ApplicationId != message.ApplicationId || result.SessionId != message.SessionId)
                throw new ProtocolException("InvalidHostResponse");
            return new ProtocolMessage { Kind = MessageKind.Result, ApplicationId = message.ApplicationId, SessionId = message.SessionId, RequestId = message.RequestId, Result = result.Result };
        }
        finally { lock (gate) pending.Remove(correlation); }
    }

    private async Task WriteHostAsync(ProtocolMessage message, CancellationToken token, bool includeBrokerLoad = false)
    {
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Sample after acquiring the writer so cross-connection wire order cannot regress the peak.
            if (includeBrokerLoad)
                lock (gate) message = message with { BrokerLoad = new BrokerLoad(pending.Count, peakPendingRequests) };
            await LengthPrefixedJson.WriteAsync(control, message, token).ConfigureAwait(false);
        }
        finally { writer.Release(); }
    }

    private async Task ReceiveHostAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(control, token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (message.Kind == MessageKind.RegisterLaunch)
                {
                    // Keep the sole control reader available for forwarded service results.
                    // Awaiting an ACK write here can deadlock with Host's response writer.
                    if (!registrationReplies.Writer.TryWrite(RegisterLaunch(message)))
                        throw new ProtocolException("RegistrationReplyQueueFull");
                    continue;
                }
                if (message.Kind == MessageKind.ActionRequest)
                {
                    await DispatchActionAsync(message, token).ConfigureAwait(false);
                    continue;
                }
                if (message.Version != ProtocolLimits.Version || message.Kind != MessageKind.Result || message.Result is null || !ValidIdentity(message.RequestId) ||
                    message.Ticket != "" || message.StartRequestId != "" || message.Declaration is not null || message.State is not null || message.BrokerLoad is not null || message.Flyout is not null || message.Action is not null || message.ActionCompletion is not null || message.Heartbeat is not null || message.Registration is not null)
                    throw new ProtocolException("InvalidHostResponse");
                TaskCompletionSource<ProtocolMessage>? completion;
                lock (gate) pending.TryGetValue(message.RequestId, out completion);
                completion?.TrySetResult(message);
            }
        }
        finally { registrationReplies.Writer.TryComplete(); CancelPending(); }
    }

    private async Task SendRegistrationRepliesAsync(CancellationToken token)
    {
        try
        {
            await foreach (var response in registrationReplies.Reader.ReadAllAsync(token).ConfigureAwait(false))
                await WriteHostAsync(response, token).ConfigureAwait(false);
        }
        finally { while (registrationReplies.Reader.TryRead(out _)) { } }
    }

    private ProtocolMessage RegisterLaunch(ProtocolMessage message)
    {
        ProtocolResult result;
        var now = DateTimeOffset.UtcNow;
        var registration = message.Registration;
        if (message.Version != ProtocolLimits.Version || !ValidIdentity(message.RequestId) || !ValidIdentity(message.ApplicationId) ||
            message.SessionId != "" || message.Ticket != "" || message.StartRequestId != "" || message.Declaration is not null ||
            message.State is not null || message.Result is not null || message.BrokerLoad is not null || message.Flyout is not null ||
            message.Action is not null || message.ActionCompletion is not null || message.Heartbeat is not null || registration is null ||
            registration.ApplicationId != message.ApplicationId || !ValidIdentity(registration.StartRequestId) || !ValidIdentity(registration.Ticket))
            result = ProtocolResult.Reject("InvalidRegistration", "恢复登记字段无效");
        else if (registration.ExpiresAt <= now || registration.ExpiresAt > now.AddSeconds(ProtocolLimits.TicketLifetimeSeconds))
            result = ProtocolResult.Reject("TicketExpired", "恢复票据有效期无效");
        else
        {
            lock (gate)
            {
                if (!allowedApplications.Contains(message.ApplicationId))
                    result = ProtocolResult.Reject("UnknownApplication", "应用不在启动白名单内");
                else if (sessions.ContainsKey(message.ApplicationId))
                    result = ProtocolResult.Reject("AlreadyConnected", "应用仍有当前会话");
                else if (lastLaunchIds.GetValueOrDefault(message.ApplicationId) == registration.StartRequestId)
                    result = ProtocolResult.Reject("InvalidTicket", "启动请求不可重复登记");
                else
                {
                    registrations[message.ApplicationId] = registration;
                    lastLaunchIds[message.ApplicationId] = registration.StartRequestId;
                    result = ProtocolResult.Success();
                }
            }
        }
        return new ProtocolMessage
        {
            Kind = MessageKind.Result,
            ApplicationId = message.ApplicationId,
            SessionId = "",
            RequestId = message.RequestId,
            Result = result
        };
    }

    private void CancelPending()
    {
        lock (gate)
        {
            foreach (var completion in pending.Values) completion.TrySetCanceled();
            pending.Clear();
        }
    }

    private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim();
    private static bool ValidServicePayload(ProtocolMessage message)
    {
        if (!ValidIdentity(message.RequestId) || message.Ticket != "" || message.StartRequestId != "" || message.Result is not null || message.BrokerLoad is not null || message.Registration is not null)
            return false;
        if (message.Kind == MessageKind.Heartbeat)
            return message.Declaration is null && message.State is null && message.Flyout is null && message.Action is null && message.ActionCompletion is null &&
                message.Heartbeat is { Sequence: > 0 } pulse && pulse.SentAt != default;
        if (message.Heartbeat is not null || message.Registration is not null) return false;
        if (message.Kind == MessageKind.ActionCompleted)
            return message.Action is null && message.Declaration is null && message.State is null && message.Flyout is null &&
                message.ActionCompletion is { } completion && completion.RequestId == message.RequestId && completion.Sequence > 0 && ValidResult(completion.Result);
        if (message.Action is not null || message.ActionCompletion is not null || message.Heartbeat is not null || message.Registration is not null) return false;
        return message.Kind switch
        {
            MessageKind.Declare => message.Declaration is not null && message.State is not null && message.Flyout is null,
            MessageKind.State => message.Declaration is null && message.State is not null && message.Flyout is null,
            MessageKind.FlyoutRequest => message.Declaration is null && message.State is null && message.Flyout is not null,
            _ => false,
        };
    }
    private static Task ReplyAsync(Stream stream, ProtocolMessage message, ProtocolResult result, CancellationToken token) =>
        LengthPrefixedJson.WriteAsync(stream, new ProtocolMessage { Kind = MessageKind.Result, ApplicationId = message.ApplicationId, SessionId = message.SessionId, RequestId = message.RequestId, Result = result }, token);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        CancelPending();
        await control.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}

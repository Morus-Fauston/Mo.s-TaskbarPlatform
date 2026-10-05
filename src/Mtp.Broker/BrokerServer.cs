using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
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
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly NamedPipeClientStream control;
    private int disposed;
    private int started;

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
            accepted.Ticket != "" || accepted.StartRequestId != "" || accepted.Declaration is not null || accepted.State is not null || accepted.Result is not null)
            throw new ProtocolException("HostHandshakeRejected");

        var listeners = new List<NamedPipeServerStream>();
        var workers = new List<Task>();
        Task? receiver = null;
        try
        {
            // Create the listeners before ready, so Host cannot race pipe creation.
            for (var i = 0; i < ProtocolLimits.MaximumApplications; i++) listeners.Add(CreateListener());
            receiver = ReceiveHostAsync(token);
            await WriteHostAsync(new ProtocolMessage { Kind = MessageKind.Welcome, RequestId = "broker-ready" }, token).ConfigureAwait(false);
            foreach (var listener in listeners) workers.Add(ServeSlotAsync(listener, token));
            var first = await Task.WhenAny(workers.Append(receiver)).ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            CancelPending();
            foreach (var listener in listeners) await listener.DisposeAsync().ConfigureAwait(false);
            if (receiver is not null) workers.Add(receiver);
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
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
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, handshake.Token).ConfigureAwait(false);
            var rejection = ValidateHello(hello);
            if (rejection is not null)
            {
                await ReplyAsync(pipe, hello, rejection, handshake.Token).ConfigureAwait(false);
                return;
            }

            application = hello.ApplicationId;
            session = Guid.NewGuid().ToString("N");
            if (!sessions.TryAdd(application, session))
            {
                await ReplyAsync(pipe, hello, ProtocolResult.Reject("AlreadyConnected", "应用已有当前连接"), handshake.Token).ConfigureAwait(false);
                session = null;
                return;
            }
            var welcome = hello with { Kind = MessageKind.Welcome, SessionId = session, Ticket = "", StartRequestId = "" };
            var confirmation = await ForwardAsync(welcome, handshake.Token).ConfigureAwait(false);
            if (confirmation.Result?.Accepted != true)
            {
                await ReplyAsync(pipe, hello, confirmation.Result ?? ProtocolResult.Reject("HostUnavailable", "平台暂不可用"), handshake.Token).ConfigureAwait(false);
                return;
            }
            await LengthPrefixedJson.WriteAsync(pipe, welcome, handshake.Token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                var message = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (message.Version != ProtocolLimits.Version)
                {
                    await ReplyAsync(pipe, message, ProtocolResult.Reject("UnsupportedVersion", "协议版本不支持"), token).ConfigureAwait(false);
                    return;
                }
                if (message.ApplicationId != application || message.SessionId != session ||
                    !sessions.TryGetValue(application, out var current) || current != session)
                {
                    await ReplyAsync(pipe, message, ProtocolResult.Reject("SessionMismatch", "会话身份不匹配"), token).ConfigureAwait(false);
                    continue;
                }
                if (!ValidServicePayload(message))
                {
                    await ReplyAsync(pipe, message, ProtocolResult.Reject("InvalidEnvelope", "消息字段与类型不匹配"), token).ConfigureAwait(false);
                    continue;
                }
                var result = await ForwardAsync(message, token).ConfigureAwait(false);
                await LengthPrefixedJson.WriteAsync(pipe, result, token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (application is not null && session is not null &&
                sessions.TryRemove(new KeyValuePair<string, string>(application, session)))
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    await WriteHostAsync(new ProtocolMessage { Kind = MessageKind.Disconnected, ApplicationId = application, SessionId = session }, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    private ProtocolResult? ValidateHello(ProtocolMessage hello)
    {
        if (hello.Version != ProtocolLimits.Version) return ProtocolResult.Reject("UnsupportedVersion", "协议版本不支持");
        if (hello.Kind != MessageKind.Hello || !ValidIdentity(hello.ApplicationId) || !ValidIdentity(hello.StartRequestId) || !ValidIdentity(hello.RequestId) ||
            string.IsNullOrWhiteSpace(hello.Ticket) || hello.Ticket.Length > 256 || hello.SessionId != "" ||
            hello.Declaration is not null || hello.State is not null || hello.Result is not null)
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
                return message with { Kind = MessageKind.Result, Result = ProtocolResult.Reject("Busy", "平台通信繁忙") };
            pending.Add(correlation, completion);
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await WriteHostAsync(message with { RequestId = correlation }, deadline.Token).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            return new ProtocolMessage { Kind = MessageKind.Result, ApplicationId = message.ApplicationId, SessionId = message.SessionId, RequestId = message.RequestId, Result = result.Result };
        }
        finally { lock (gate) pending.Remove(correlation); }
    }

    private async Task WriteHostAsync(ProtocolMessage message, CancellationToken token)
    {
        await writer.WaitAsync(token).ConfigureAwait(false);
        try { await LengthPrefixedJson.WriteAsync(control, message, token).ConfigureAwait(false); }
        finally { writer.Release(); }
    }

    private async Task ReceiveHostAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(control, token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (message.Version != ProtocolLimits.Version || message.Kind != MessageKind.Result || message.Result is null || !ValidIdentity(message.RequestId) ||
                    message.Ticket != "" || message.StartRequestId != "" || message.Declaration is not null || message.State is not null)
                    throw new ProtocolException("InvalidHostResponse");
                TaskCompletionSource<ProtocolMessage>? completion;
                lock (gate) pending.TryGetValue(message.RequestId, out completion);
                completion?.TrySetResult(message);
            }
        }
        finally { CancelPending(); }
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
        if (!ValidIdentity(message.RequestId) || message.Ticket != "" || message.StartRequestId != "" || message.Result is not null)
            return false;
        return message.Kind switch
        {
            MessageKind.Declare => message.Declaration is not null && message.State is not null,
            MessageKind.State => message.Declaration is null && message.State is not null,
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

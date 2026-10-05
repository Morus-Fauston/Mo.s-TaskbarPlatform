using System.IO.Pipes;
using System.Threading.Channels;
using Mtp.Contracts;
using Mtp.Transport;

namespace Mtp.Sdk;

/// <summary>Hides framing and credentials from application business logic.</summary>
internal sealed class SdkConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly string applicationId;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    private long flyoutSequence;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly object responseGate = new();
    private readonly IActionHandler? actionHandler;
    private readonly Channel<ActionInvocation> actions = Channel.CreateBounded<ActionInvocation>(
        new BoundedChannelOptions(ActionLimits.MaximumOutstandingPerApplication) { SingleReader = true, SingleWriter = true });
    private readonly Task reader;
    private readonly Task actionWorker;
    private readonly Task heartbeatWorker;
    private TaskCompletionSource<ProtocolResult>? pendingResponse;
    private string? pendingRequestId;
    private long lastActionSequence;
    private int outstandingActions;

    private SdkConnection(NamedPipeClientStream pipe, string applicationId, string sessionId, IActionHandler? actionHandler)
    {
        this.pipe = pipe;
        this.applicationId = applicationId;
        SessionId = sessionId;
        this.actionHandler = actionHandler;
        reader = ReadMessagesAsync();
        actionWorker = RunActionsAsync();
        heartbeatWorker = RunHeartbeatsAsync();
    }

    public string SessionId { get; }
    public bool IsConnected => Volatile.Read(ref disposed) == 0;

    public static async Task<SdkConnection> ConnectAsync(ServiceLaunch launch, IDeclarationProvider provider, CancellationToken cancellationToken = default, IActionHandler? actionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(provider);
        var pipe = new NamedPipeClientStream(".", launch.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        SdkConnection? client = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var request = Guid.NewGuid().ToString("N");
            await LengthPrefixedJson.WriteAsync(pipe, new ProtocolMessage
            {
                Kind = MessageKind.Hello,
                ApplicationId = launch.ApplicationId,
                StartRequestId = launch.StartRequestId,
                Ticket = launch.Ticket,
                RequestId = request,
            }, deadline.Token).ConfigureAwait(false);
            var welcome = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, deadline.Token).ConfigureAwait(false);
            if (welcome.BrokerLoad is not null || welcome.Flyout is not null || welcome.Action is not null || welcome.ActionCompletion is not null || welcome.Heartbeat is not null || welcome.Registration is not null) throw new ProtocolException("InvalidWelcome");
            if (welcome.Kind == MessageKind.Result && welcome.Result?.Accepted == false)
                throw new ProtocolException(welcome.Result.Code);
            if (welcome.Version != ProtocolLimits.Version || welcome.Kind != MessageKind.Welcome || welcome.RequestId != request ||
                welcome.ApplicationId != launch.ApplicationId || string.IsNullOrWhiteSpace(welcome.SessionId) || welcome.SessionId.Length > 256 ||
                welcome.Ticket != "" || welcome.StartRequestId != "" || welcome.Declaration is not null || welcome.State is not null || welcome.Result is not null)
                throw new ProtocolException("InvalidWelcome");
            client = new SdkConnection(pipe, launch.ApplicationId, welcome.SessionId, actionHandler ?? provider as IActionHandler);
            var snapshot = await provider.GetSnapshotAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            var declaration = await client.SendAsync(new ProtocolMessage { Kind = MessageKind.Declare, Declaration = snapshot.Declaration, State = snapshot.State }, deadline.Token).ConfigureAwait(false);
            if (!declaration.Accepted) throw new ProtocolException(declaration.Code);
            return client;
        }
        catch
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            else await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<ProtocolResult> PublishAsync(ApplicationState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendAsync(new ProtocolMessage { Kind = MessageKind.State, State = state }, cancellationToken);
    }

    public Task<ProtocolResult> RequestFlyoutAsync(FlyoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(new ProtocolMessage { Kind = MessageKind.FlyoutRequest, Flyout = request }, cancellationToken);
    }

    private async Task<ProtocolResult> SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        // Reject excess publishers immediately; never accumulate an unbounded semaphore wait queue.
        if (!await requestGate.WaitAsync(0, deadline.Token).ConfigureAwait(false)) return ProtocolResult.Reject("Busy", "上一条状态仍在等待确认");
        try
        {
            message = message with { ApplicationId = applicationId, SessionId = SessionId, RequestId = Guid.NewGuid().ToString("N") };
            if (message.Flyout is { } request)
            {
                if (flyoutSequence == long.MaxValue) throw new ProtocolException("RequestSequenceExhausted");
                message = message with { Flyout = request with { RequestId = message.RequestId, RequestSequence = ++flyoutSequence } };
            }
            var completion = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (responseGate) { pendingResponse = completion; pendingRequestId = message.RequestId; }
            await WriteAsync(message, deadline.Token).ConfigureAwait(false);
            try { return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && completion.Task.IsFaulted)
            {
                // The reader records its failure before closing the connection. Preserve that
                // cause when lifetime cancellation wins the WaitAsync continuation race.
                return await completion.Task.ConfigureAwait(false);
            }
        }
        catch
        {
            // A cancelled or partial request cannot safely reuse the byte stream.
            Stop();
            throw;
        }
        finally
        {
            lock (responseGate) { pendingResponse = null; pendingRequestId = null; }
            requestGate.Release();
        }
    }

    private async Task WriteAsync(ProtocolMessage message, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await writer.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { await LengthPrefixedJson.WriteAsync(pipe, message, deadline.Token).ConfigureAwait(false); }
        finally { writer.Release(); }
    }

    private async Task ReadMessagesAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, lifetime.Token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (message.Version != ProtocolLimits.Version || message.ApplicationId != applicationId || message.SessionId != SessionId ||
                    !ValidIdentity(message.RequestId) || message.Ticket != "" || message.StartRequestId != "" ||
                    message.Declaration is not null || message.State is not null || message.BrokerLoad is not null || message.Flyout is not null || message.ActionCompletion is not null || message.Heartbeat is not null || message.Registration is not null)
                    throw new ProtocolException("InvalidResponse");
                if (message.Kind == MessageKind.Result && message.Action is null && ValidResult(message.Result))
                {
                    lock (responseGate)
                    {
                        if (message.RequestId != pendingRequestId || pendingResponse is null) throw new ProtocolException("InvalidResponse");
                        pendingResponse.TrySetResult(message.Result!);
                    }
                }
                else if (message.Kind == MessageKind.ActionRequest && message.Result is null && message.Action is { } action &&
                    action.RequestId == message.RequestId && action.Sequence > 0 && action.Slot is not null && action.Slot.ApplicationId == applicationId && action.Parameter is not null)
                {
                    // Replayed sequences never execute twice, even after a result has left the queue.
                    if (action.Sequence <= lastActionSequence) continue;
                    lastActionSequence = action.Sequence;
                    if (Interlocked.Increment(ref outstandingActions) > ActionLimits.MaximumOutstandingPerApplication)
                    {
                        Interlocked.Decrement(ref outstandingActions);
                        await CompleteActionAsync(new(action.RequestId, action.Sequence, ProtocolResult.Reject("Busy", "动作队列已满"))).ConfigureAwait(false);
                    }
                    else if (!actions.Writer.TryWrite(action))
                    {
                        Interlocked.Decrement(ref outstandingActions);
                        throw new ProtocolException("ActionQueueClosed");
                    }
                }
                else throw new ProtocolException("InvalidResponse");
            }
        }
        catch (Exception exception)
        {
            lock (responseGate) pendingResponse?.TrySetException(exception);
        }
        finally { Stop(); }
    }

    private async Task RunActionsAsync()
    {
        try
        {
            await foreach (var action in actions.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                ActionCompletion completion;
                try
                {
                    completion = actionHandler is null
                        ? new(action.RequestId, action.Sequence, ProtocolResult.Reject("ActionNotAvailable", "应用未提供动作处理器"))
                        : await actionHandler.HandleAsync(action, lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);
                    if (completion is null || completion.RequestId != action.RequestId || completion.Sequence != action.Sequence || !ValidResult(completion.Result))
                        completion = new(action.RequestId, action.Sequence, ProtocolResult.Reject("InvalidActionResult", "动作返回无效结果"));
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    completion = new(action.RequestId, action.Sequence, ProtocolResult.Reject("ActionFailed", "动作执行失败"));
                }
                lifetime.Token.ThrowIfCancellationRequested();
                await CompleteActionAsync(completion).ConfigureAwait(false);
                Interlocked.Decrement(ref outstandingActions);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            Stop();
            while (actions.Reader.TryRead(out _)) { }
        }
    }

    private async Task RunHeartbeatsAsync()
    {
        using var timer = new PeriodicTimer(HeartbeatMonitor.HeartbeatInterval);
        long sequence = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (sequence == long.MaxValue) throw new ProtocolException("HeartbeatSequenceExhausted");
                await WriteAsync(new ProtocolMessage
                {
                    Kind = MessageKind.Heartbeat,
                    ApplicationId = applicationId,
                    SessionId = SessionId,
                    RequestId = Guid.NewGuid().ToString("N"),
                    Heartbeat = new(++sequence, DateTimeOffset.UtcNow)
                }, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { Stop(); }
    }

    private Task CompleteActionAsync(ActionCompletion completion) => WriteAsync(new ProtocolMessage
    {
        Kind = MessageKind.ActionCompleted,
        ApplicationId = applicationId,
        SessionId = SessionId,
        RequestId = completion.RequestId,
        ActionCompletion = completion
    }, lifetime.Token);

    private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim();
    private static bool ValidResult(ProtocolResult? result) => result is not null && ValidIdentity(result.Code) &&
        result.Message is not null && result.Message.Length <= ProtocolLimits.MaximumTextLength &&
        (result.Path is null || result.Path.Length <= ProtocolLimits.MaximumTextLength);

    private void Stop()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        actions.Writer.TryComplete();
        pipe.Dispose();
        lock (responseGate) pendingResponse?.TrySetCanceled();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.WhenAll(reader, actionWorker, heartbeatWorker).ConfigureAwait(false);
        lifetime.Dispose();
    }
}

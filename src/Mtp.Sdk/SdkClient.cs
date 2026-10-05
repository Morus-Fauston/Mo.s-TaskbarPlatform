using System.IO.Pipes;
using Mtp.Contracts;
using Mtp.Transport;

namespace Mtp.Sdk;

public interface IDeclarationProvider
{
    Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Hides framing and credentials from application business logic.</summary>
public sealed class SdkClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly string applicationId;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    private SdkClient(NamedPipeClientStream pipe, string applicationId, string sessionId)
    {
        this.pipe = pipe;
        this.applicationId = applicationId;
        SessionId = sessionId;
    }

    public string SessionId { get; }

    /// <summary>Reads Host-issued startup credentials without exposing framing to business code.</summary>
    public static Task<SdkClient> ConnectFromStandardInputAsync(IDeclarationProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return ConnectFromStandardInputAsync(_ => provider, cancellationToken);
    }

    /// <summary>The factory constructs a provider for the application identity issued by Host.</summary>
    public static async Task<SdkClient> ConnectFromStandardInputAsync(Func<string, IDeclarationProvider> providerFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var input = Console.OpenStandardInput();
        var launch = await LengthPrefixedJson.ReadAsync<ServiceLaunch>(input, deadline.Token).ConfigureAwait(false);
        var provider = providerFactory(launch.ApplicationId) ?? throw new ArgumentException("DeclarationProviderRequired", nameof(providerFactory));
        deadline.Token.ThrowIfCancellationRequested();
        return await ConnectAsync(launch, provider, deadline.Token).ConfigureAwait(false);
    }

    public static async Task<SdkClient> ConnectAsync(ServiceLaunch launch, IDeclarationProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(provider);
        var pipe = new NamedPipeClientStream(".", launch.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        SdkClient? client = null;
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
            if (welcome.Kind == MessageKind.Result && welcome.Result?.Accepted == false)
                throw new ProtocolException(welcome.Result.Code);
            if (welcome.Version != ProtocolLimits.Version || welcome.Kind != MessageKind.Welcome || welcome.RequestId != request ||
                welcome.ApplicationId != launch.ApplicationId || string.IsNullOrWhiteSpace(welcome.SessionId) || welcome.SessionId.Length > 256 ||
                welcome.Ticket != "" || welcome.StartRequestId != "" || welcome.Declaration is not null || welcome.State is not null || welcome.Result is not null)
                throw new ProtocolException("InvalidWelcome");
            client = new SdkClient(pipe, launch.ApplicationId, welcome.SessionId);
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
            await LengthPrefixedJson.WriteAsync(pipe, message, deadline.Token).ConfigureAwait(false);
            var result = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, deadline.Token).ConfigureAwait(false);
            if (result.Version != ProtocolLimits.Version || result.Kind != MessageKind.Result || result.RequestId != message.RequestId ||
                result.ApplicationId != applicationId || result.SessionId != SessionId || result.Result is null ||
                result.Ticket != "" || result.StartRequestId != "" || result.Declaration is not null || result.State is not null)
                throw new ProtocolException("InvalidResponse");
            return result.Result;
        }
        catch
        {
            // A cancelled or partial request cannot safely reuse the byte stream.
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { requestGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await pipe.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}

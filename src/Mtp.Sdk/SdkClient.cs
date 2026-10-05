using System.Threading.Channels;
using Mtp.Contracts;
using Mtp.Transport;

namespace Mtp.Sdk;

public interface IDeclarationProvider
{
    Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface IActionHandler
{
    Task<ActionCompletion> HandleAsync(ActionInvocation action, CancellationToken cancellationToken);
}

public interface IDisplayPermissionObserver
{
    Task OnDisplayPermissionsChangedAsync(DisplayPermissionSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Owns credential delivery and connection generations; business requests are never replayed.</summary>
public sealed class SdkClient : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly string applicationId;
    private readonly IDeclarationProvider provider;
    private readonly IActionHandler? handler;
    private readonly Stream? credentialsInput;
    private readonly Channel<Credential> credentials = Channel.CreateBounded<Credential>(
        new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private SdkConnection? connection;
    private CancellationTokenSource? connecting;
    private Task credentialReader = Task.CompletedTask;
    private Task recoveryWorker = Task.CompletedTask;
    private Task? disposal;
    private string lastSessionId;
    private string lastLaunchId;
    private long latestGeneration;
    private bool inputAvailable;
    private bool disposed;
    private ProtocolResult? lastError;
    private ProtocolResult initialPublicationResult;

    private SdkClient(ServiceLaunch launch, IDeclarationProvider provider, IActionHandler? handler, SdkConnection connection, Stream? input)
    {
        applicationId = launch.ApplicationId;
        this.provider = provider;
        this.handler = handler ?? provider as IActionHandler;
        this.connection = connection;
        credentialsInput = input;
        inputAvailable = input is not null;
        lastSessionId = connection.SessionId;
        lastLaunchId = launch.StartRequestId;
        initialPublicationResult = connection.InitialPublicationResult;
    }

    public string SessionId { get { lock (gate) return lastSessionId; } }
    public ProtocolResult InitialPublicationResult { get { lock (gate) return initialPublicationResult; } }
    public bool IsConnected { get { lock (gate) return !disposed && connection?.IsConnected == true; } }
    public ProtocolResult? LastError
    {
        get { lock (gate) return connection?.IsConnected == true ? lastError : Unavailable(); }
    }

    public static Task<SdkClient> ConnectFromStandardInputAsync(IDeclarationProvider provider,
        CancellationToken cancellationToken = default, IActionHandler? actionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return ConnectFromStandardInputAsync(_ => provider, cancellationToken, actionHandler);
    }

    public static async Task<SdkClient> ConnectFromStandardInputAsync(Func<string, IDeclarationProvider> providerFactory,
        CancellationToken cancellationToken = default, IActionHandler? actionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        var input = StandardInputCredentials.Open();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(RecoveryLimits.ConnectionTimeoutSeconds));
            var launch = await LengthPrefixedJson.ReadAsync<ServiceLaunch>(input, deadline.Token).ConfigureAwait(false);
            ValidateLaunch(launch);
            var provider = providerFactory(launch.ApplicationId) ?? throw new ArgumentException("DeclarationProviderRequired", nameof(providerFactory));
            var connection = await SdkConnection.ConnectAsync(launch, provider, deadline.Token, actionHandler).ConfigureAwait(false);
            var client = new SdkClient(launch, provider, actionHandler, connection, input);
            client.credentialReader = client.ReadCredentialsAsync();
            client.recoveryWorker = client.RecoverAsync();
            connection.ActivatePermissionCallbacks();
            return client;
        }
        catch { input.Dispose(); throw; }
    }

    public static async Task<SdkClient> ConnectAsync(ServiceLaunch launch, IDeclarationProvider provider,
        CancellationToken cancellationToken = default, IActionHandler? actionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateLaunch(launch);
        var connection = await SdkConnection.ConnectAsync(launch, provider, cancellationToken, actionHandler).ConfigureAwait(false);
        var client = new SdkClient(launch, provider, actionHandler, connection, null);
        connection.ActivatePermissionCallbacks();
        return client;
    }

    public Task<ProtocolResult> PublishAsync(ApplicationState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendAsync(current => current.PublishAsync(state, cancellationToken), cancellationToken);
    }

    public Task<ProtocolResult> RequestFlyoutAsync(FlyoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(current => current.RequestFlyoutAsync(request, cancellationToken), cancellationToken);
    }

    private async Task<ProtocolResult> SendAsync(Func<SdkConnection, Task<ProtocolResult>> send, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SdkConnection current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (SdkConnection.CurrentPermissionCallback is { } callback && !ReferenceEquals(callback, connection))
                return ProtocolResult.Reject("StaleSession", "旧会话许可回调不能提交到当前连接");
            if (connection?.IsConnected != true) return Unavailable();
            current = connection;
        }
        try { return await send(current).ConfigureAwait(false); }
        catch (Exception error) when (
            error is IOException and not ProtocolException or ObjectDisposedException ||
            error is ProtocolException { Code: "end_of_stream" or "truncated_frame" } ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        {
            // Framing, payload and envelope violations retain their exact cause for callers.
            // Only a closed transport or connection lifetime/deadline becomes an offline result.
            token.ThrowIfCancellationRequested();
            lock (gate) return Unavailable();
        }
    }

    private async Task ReadCredentialsAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var launch = await LengthPrefixedJson.ReadAsync<ServiceLaunch>(credentialsInput!, lifetime.Token, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                ValidateLaunch(launch);
                lock (gate)
                {
                    if (launch.ApplicationId != applicationId) throw new ProtocolException("CredentialIdentityMismatch");
                    if (launch.StartRequestId == lastLaunchId) continue;
                    lastLaunchId = launch.StartRequestId;
                    connecting?.Cancel();
                    if (!credentials.Writer.TryWrite(new(++latestGeneration, launch))) break;
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or ArgumentException)
        {
            lock (gate) lastError = ProtocolResult.Reject("CredentialChannelClosed", "恢复凭据通道已关闭");
        }
        finally
        {
            lock (gate) inputAvailable = false;
            credentials.Writer.TryComplete();
        }
    }

    private async Task RecoverAsync()
    {
        try
        {
            await foreach (var credential in credentials.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                SdkConnection? previous;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(RecoveryLimits.ConnectionTimeoutSeconds));
                lock (gate)
                {
                    if (credential.Generation != latestGeneration) continue;
                    previous = connection;
                    connection = null;
                    connecting = attempt;
                    lastError = ProtocolResult.Reject("Reconnecting", "正在恢复平台连接");
                }
                SdkConnection? candidate = null;
                try
                {
                    if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
                    // Host owns retry budgets and backoff. Every delivered ticket is tried exactly once.
                    candidate = await SdkConnection.ConnectAsync(credential.Launch, provider, attempt.Token, handler).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!disposed && credential.Generation == latestGeneration)
                        {
                            connection = candidate;
                            lastSessionId = candidate.SessionId;
                            initialPublicationResult = candidate.InitialPublicationResult;
                            lastError = null;
                            candidate.ActivatePermissionCallbacks();
                            candidate = null;
                        }
                    }
                }
                catch (Exception)
                {
                    lock (gate)
                        if (credential.Generation == latestGeneration) lastError = ProtocolResult.Reject("Reconnecting", "本次连接未完成，等待新的恢复凭据");
                }
                finally
                {
                    lock (gate) if (ReferenceEquals(connecting, attempt)) connecting = null;
                    if (candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { while (credentials.Reader.TryRead(out _)) { } }
    }

    private ProtocolResult Unavailable() => ProtocolResult.Reject(inputAvailable && !disposed ? "Reconnecting" : "Unavailable",
        inputAvailable && !disposed ? "平台连接暂不可用，等待恢复" : "平台连接不可用");

    private static void ValidateLaunch(ServiceLaunch? launch)
    {
        if (launch is null || !ValidId(launch.PipeName) || !ValidId(launch.ApplicationId) || !ValidId(launch.StartRequestId) || !ValidId(launch.Ticket))
            throw new ProtocolException("InvalidServiceLaunch");
    }
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim();

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            disposal = DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        credentialsInput?.Dispose();
        credentials.Writer.TryComplete();
        await Task.WhenAll(credentialReader, recoveryWorker).ConfigureAwait(false);
        SdkConnection? current;
        lock (gate) { current = connection; connection = null; }
        if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    private sealed record Credential(long Generation, ServiceLaunch Launch);
}

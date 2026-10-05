using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Sdk;
using Mtp.Transport;

if (args.Length != 2 || args[0] is not ("sdk-static" or "raw-silent" or "raw-duplicate" or "pipe-close")) return 2;
string mode = args[0];
string directory = Path.GetFullPath(args[1]);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
try
{
    if (mode == "sdk-static") await RunSdkAsync();
    else await RunRawAsync();
    return 0;
}
catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ArgumentException)
{
    string code = error is ProtocolException protocol ? protocol.Code : error.GetType().Name;
    Console.Error.WriteLine("HeartbeatProbeFailed:" + code);
    try { await MarkerAsync("failed", new { mode, pid = Environment.ProcessId, code }); }
    catch (IOException) { }
    return 1;
}

async Task RunSdkAsync()
{
    await using var client = await SdkClient.ConnectFromStandardInputAsync(id => new StaticProvider(id), lifetime.Token);
    await MarkerAsync("ready", new { mode, pid = Environment.ProcessId, sessionId = client.SessionId });
    await Task.Delay(TimeSpan.FromMilliseconds(6500), lifetime.Token);
    await MarkerAsync("static-window-complete", new { mode, pid = Environment.ProcessId, elapsedMs = 6500, businessPublications = 0 });
    await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
}

async Task RunRawAsync()
{
    using var input = Console.OpenStandardInput();
    var launch = await LengthPrefixedJson.ReadAsync<ServiceLaunch>(input, lifetime.Token);
    await using var pipe = new NamedPipeClientStream(".", launch.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(lifetime.Token);
    var welcome = await ExchangeAsync(new ProtocolMessage
    {
        Kind = MessageKind.Hello,
        ApplicationId = launch.ApplicationId,
        StartRequestId = launch.StartRequestId,
        Ticket = launch.Ticket,
        RequestId = Guid.NewGuid().ToString("N"),
    });
    Require(welcome.Kind == MessageKind.Welcome && !string.IsNullOrWhiteSpace(welcome.SessionId), "WelcomeRejected");
    string session = welcome.SessionId;
    var snapshot = await new StaticProvider(launch.ApplicationId).GetSnapshotAsync(lifetime.Token);
    var declared = await ExchangeAsync(Envelope(MessageKind.Declare) with { Declaration = snapshot.Declaration, State = snapshot.State });
    Require(declared.Result?.Accepted == true, "DeclarationRejected");
    if (mode == "raw-duplicate") await SendDuplicateAsync();
    await MarkerAsync("ready", new { mode, pid = Environment.ProcessId, sessionId = session });
    if (mode == "pipe-close")
    {
        while (!File.Exists(Path.Combine(directory, "close-pipe"))) await Task.Delay(10, lifetime.Token);
        await pipe.DisposeAsync();
        await MarkerAsync("pipe-closed", new { mode, pid = Environment.ProcessId });
    }
    else
    {
        // Each acknowledged State proves live business traffic, never a substitute heartbeat.
        try
        {
            for (long revision = 1; revision <= 9; revision++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
                if (mode == "raw-duplicate") await SendDuplicateAsync();
                var result = await ExchangeAsync(Envelope(MessageKind.State) with { State = StaticProvider.State(revision, revision + 7) });
                if (result.Result?.Accepted != true)
                {
                    await MarkerAsync("channel-ended", new { mode, pid = Environment.ProcessId, code = result.Result?.Code ?? "MissingResult" });
                    break;
                }
                await MarkerAsync("state-" + revision, new { mode, pid = Environment.ProcessId, revision, number = revision + 7 });
            }
        }
        catch (IOException error)
        {
            await MarkerAsync("channel-ended", new { mode, pid = Environment.ProcessId, code = error is ProtocolException protocol ? protocol.Code : error.GetType().Name });
        }
    }
    // The supervised process remains alive after losing its protocol connection.
    await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);

    ProtocolMessage Envelope(MessageKind kind) => new()
    {
        Kind = kind,
        ApplicationId = launch.ApplicationId,
        SessionId = session,
        RequestId = Guid.NewGuid().ToString("N"),
    };
    Task SendDuplicateAsync() => LengthPrefixedJson.WriteAsync(pipe,
        Envelope(MessageKind.Heartbeat) with { Heartbeat = new HeartbeatPulse(1, DateTimeOffset.UtcNow) }, lifetime.Token);
    async Task<ProtocolMessage> ExchangeAsync(ProtocolMessage message)
    {
        await LengthPrefixedJson.WriteAsync(pipe, message, lifetime.Token);
        var response = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, lifetime.Token);
        Require(response.RequestId == message.RequestId && response.Version == ProtocolLimits.Version, "InvalidResponse");
        return response;
    }
}

async Task MarkerAsync(string name, object value)
{
    string path = Path.Combine(directory, name + ".json");
    await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value, LengthPrefixedJson.Options));
    File.Move(path + ".tmp", path, overwrite: true);
}

static void Require(bool condition, string code)
{
    if (!condition) throw new ProtocolException(code);
}

sealed class StaticProvider(string applicationId) : IDeclarationProvider, IActionHandler
{
    private long revision;
    private long count = 7;

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionSlotDeclaration[] actions = [new("activate")];
        var declaration = new ApplicationDeclaration(applicationId,
            [new FeatureGroupDeclaration("main", [new ComponentDeclaration("counter", actions)], [new TaskbarFlyoutDeclaration("details", actions)])]);
        return Task.FromResult(new ApplicationSnapshot(declaration, State(revision, count)));
    }

    public Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        count += 10;
        revision++;
        return Task.FromResult(new ActionCompletion(invocation.RequestId, invocation.Sequence, ProtocolResult.Success("ActionSucceeded"), State(revision, count)));
    }

    public static ApplicationState State(long revision, long count) => new(revision,
        [new ComponentReading("main", "counter", count.ToString(CultureInfo.InvariantCulture), count)]);
}

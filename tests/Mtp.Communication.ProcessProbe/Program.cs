using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Sdk;
using Mtp.Transport;

if (args.Length != 2 || args[0] is not ("normal" or "high-frequency" or "bad-state" or "slow-reader"))
{
    Console.Error.WriteLine("ProbeFailed:InvalidArguments");
    return 2;
}

string mode = args[0];
string coordinationDirectory = Path.GetFullPath(args[1]);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    if (mode is "normal" or "high-frequency") await RunSdkAsync();
    else await RunRawAsync();
    return 0;
}
catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ArgumentException)
{
    // No credential, protocol payload or raw exception message goes into diagnostics.
    string code = error is ProtocolException protocol ? protocol.Code : error.GetType().Name;
    Console.Error.WriteLine($"ProbeFailed:{mode}:{code}");
    try { await MarkerAsync("failed", new { mode, pid = Environment.ProcessId, code }, CancellationToken.None); }
    catch (IOException) { }
    return 1;
}

async Task RunSdkAsync()
{
    await using var client = await SdkClient.ConnectFromStandardInputAsync(id => new ProbeProvider(id), lifetime.Token);
    await MarkerAsync("ready", new { mode, pid = Environment.ProcessId }, lifetime.Token);
    await WaitForSignalAsync("run", lifetime.Token);
    int finalRevision = mode == "normal" ? 500 : 1000;
    for (int revision = 1; revision <= finalRevision; revision++)
    {
        string text = revision == finalRevision ? "complete:" + mode : revision.ToString(CultureInfo.InvariantCulture);
        var result = await client.PublishAsync(ProbeProvider.State(revision, text), lifetime.Token);
        Require(result.Accepted, result.Code);
    }
    await MarkerAsync("complete", new { mode, pid = Environment.ProcessId, revision = finalRevision, accepted = finalRevision }, lifetime.Token);
    Console.WriteLine($"ProbeComplete:{mode}:revision={finalRevision}:accepted={finalRevision}");
    await Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token);
}

async Task RunRawAsync()
{
    using var input = Console.OpenStandardInput();
    var launch = await LengthPrefixedJson.ReadAsync<ServiceLaunch>(input, lifetime.Token);
    await using var pipe = new NamedPipeClientStream(".", launch.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(lifetime.Token);
    var hello = new ProtocolMessage
    {
        Kind = MessageKind.Hello,
        ApplicationId = launch.ApplicationId,
        StartRequestId = launch.StartRequestId,
        Ticket = launch.Ticket,
        RequestId = Guid.NewGuid().ToString("N"),
    };
    var welcome = await ExchangeAsync(pipe, hello);
    Require(welcome.Kind == MessageKind.Welcome && !string.IsNullOrWhiteSpace(welcome.SessionId) && welcome.BrokerLoad is null, "WelcomeRejected");
    string session = welcome.SessionId;
    var provider = await new ProbeProvider(launch.ApplicationId).GetSnapshotAsync(lifetime.Token);
    var declaration = await ExchangeAsync(pipe, Envelope(MessageKind.Declare) with { Declaration = provider.Declaration, State = provider.State });
    Require(declaration.Result?.Accepted == true, declaration.Result?.Code ?? "MissingDeclarationResult");
    await MarkerAsync("ready", new { mode, pid = Environment.ProcessId }, lifetime.Token);
    await WaitForSignalAsync("run", lifetime.Token);

    if (mode == "bad-state")
    {
        var accepted = await ExchangeAsync(pipe, Envelope(MessageKind.State) with { State = ProbeProvider.State(500) });
        Require(accepted.Result?.Accepted == true, "BaselineRejected");
        var rejections = new Dictionary<string, string>();
        await RejectAsync("unknown-component", Envelope(MessageKind.State) with
        {
            State = new ApplicationState(600, [new ComponentReading("main", "not-declared", "poison", 600)]),
        }, "InvalidState");
        await RejectAsync("duplicate-component", Envelope(MessageKind.State) with
        {
            State = new ApplicationState(601, [new ComponentReading("main", "counter", "poison", 601), new ComponentReading("main", "counter", "duplicate", 601)]),
        }, "InvalidState");
        await RejectAsync("duplicate-revision", Envelope(MessageKind.State) with { State = ProbeProvider.State(500, "poison") }, "StaleRevision");
        await RejectAsync("old-revision", Envelope(MessageKind.State) with { State = ProbeProvider.State(499, "poison") }, "StaleRevision");
        await RejectAsync("old-session", Envelope(MessageKind.State) with { SessionId = "previous-session", State = ProbeProvider.State(900, "poison") }, "SessionMismatch");
        await RejectAsync("forged-load", Envelope(MessageKind.State) with { State = ProbeProvider.State(901, "poison"), BrokerLoad = new BrokerLoad(64, 64) }, "InvalidEnvelope");
        await MarkerAsync("checkpoint", new { mode, pid = Environment.ProcessId, revision = 500, rejections }, lifetime.Token);
        await WaitForSignalAsync("continue", lifetime.Token);
        var final = await ExchangeAsync(pipe, Envelope(MessageKind.State) with { State = ProbeProvider.State(1000, "complete:bad-state") });
        Require(final.Result?.Accepted == true, "FinalStateRejected");
        await MarkerAsync("complete", new { mode, pid = Environment.ProcessId, revision = 1000, rejections }, lifetime.Token);
        Console.WriteLine("ProbeComplete:bad-state:revision=1000:rejections=6");

        async Task RejectAsync(string name, ProtocolMessage message, string expectedCode)
        {
            var response = await ExchangeAsync(pipe, message);
            Require(response.Kind == MessageKind.Result && response.Result?.Accepted == false && response.Result.Code == expectedCode && response.BrokerLoad is null, "UnexpectedRejection:" + name);
            rejections.Add(name, response.Result!.Code);
        }
    }
    else
    {
        int written = 0;
        string outcome = "frame-limit";
        using var producerDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        producerDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            // Intentionally never read a response after the accepted declaration.
            for (int revision = 1; revision <= 10_000; revision++)
            {
                await LengthPrefixedJson.WriteAsync(pipe, Envelope(MessageKind.State) with { State = ProbeProvider.State(revision) }, producerDeadline.Token);
                written++;
            }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { outcome = "bounded-cancellation"; }
        catch (IOException) { outcome = "connection-closed"; }
        Require(written > 0, "NoSlowReaderStateWritten");
        await MarkerAsync("complete", new { mode, pid = Environment.ProcessId, writtenFrames = written, outcome }, lifetime.Token);
        Console.WriteLine($"ProbeComplete:slow-reader:writtenFrames={written}:outcome={outcome}");
    }
    await Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token);

    ProtocolMessage Envelope(MessageKind kind) => new()
    {
        Kind = kind,
        ApplicationId = launch.ApplicationId,
        SessionId = session,
        RequestId = Guid.NewGuid().ToString("N"),
    };
}

async Task<ProtocolMessage> ExchangeAsync(Stream pipe, ProtocolMessage message)
{
    await LengthPrefixedJson.WriteAsync(pipe, message, lifetime.Token);
    var result = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, lifetime.Token);
    Require(result.Version == ProtocolLimits.Version && result.RequestId == message.RequestId && result.ApplicationId == message.ApplicationId && result.BrokerLoad is null, "InvalidResponse");
    return result;
}

async Task WaitForSignalAsync(string signal, CancellationToken cancellationToken)
{
    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    wait.CancelAfter(TimeSpan.FromSeconds(5));
    while (!File.Exists(Path.Combine(coordinationDirectory, signal))) await Task.Delay(10, wait.Token);
}

async Task MarkerAsync(string name, object value, CancellationToken cancellationToken)
{
    string path = Path.Combine(coordinationDirectory, name + ".json");
    string temporary = path + ".tmp";
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, LengthPrefixedJson.Options), cancellationToken);
    File.Move(temporary, path, overwrite: true);
}

static void Require(bool condition, string code)
{
    if (!condition) throw new ProtocolException(code);
}

sealed class ProbeProvider(string applicationId) : IDeclarationProvider
{
    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionSlotDeclaration[] actions = [new("activate")];
        var declaration = new ApplicationDeclaration(applicationId,
            [new FeatureGroupDeclaration("main", [new ComponentDeclaration("counter", actions)], [new TaskbarFlyoutDeclaration("details", actions)])]);
        return Task.FromResult(new ApplicationSnapshot(declaration, State(0)));
    }

    public static ApplicationState State(long revision, string? text = null) => new(revision,
        [new ComponentReading("main", "counter", text ?? revision.ToString(CultureInfo.InvariantCulture), revision)]);
}

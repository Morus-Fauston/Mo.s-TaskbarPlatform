using System.IO.Pipes;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Sdk;
using Mtp.Transport;

if (args.Length != 2 || args[0] is not ("sdk" or "raw")) return 2;
string mode = args[0];
string directory = Path.GetFullPath(args[1]);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    if (mode == "sdk") await RunSdkAsync();
    else await RunRawAsync();
    return 0;
}
catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ArgumentException)
{
    string code = error is ProtocolException protocol ? protocol.Code : error.GetType().Name;
    Console.Error.WriteLine("ProbeFailed:" + mode + ":" + code);
    try { await MarkerAsync("failed", new { mode, pid = Environment.ProcessId, code }); }
    catch (IOException) { }
    return 1;
}

async Task RunSdkAsync()
{
    await using var client = await SdkClient.ConnectFromStandardInputAsync(id => new FlyoutProvider(id), lifetime.Token);
    await MarkerAsync("ready", new { mode, pid = Environment.ProcessId, sessionId = client.SessionId });
    await WaitAsync("run");
    var results = new Dictionary<string, string>();
    await SendAsync("disabled", "blocked", FlyoutKind.ShortHint, "EntryDisabled");
    await SendAsync("reenabled", "blocked", FlyoutKind.ShortHint, "Received");
    await SendAsync("taskbar", "details", FlyoutKind.TaskbarGroup, "Received");
    await SendAsync("short", "notice", FlyoutKind.ShortHint, "Received");
    await SendAsync("interactive", "adjust", FlyoutKind.InteractiveHint, "Received", screen: FlyoutScreen.Primary);
    await SendAsync("event", "events", FlyoutKind.EventGroup, "Received");
    await SendAsync("unknown", "missing", FlyoutKind.EventGroup, "UnknownEntry");
    await SendAsync("kind", "notice", FlyoutKind.EventGroup, "KindMismatch");
    await SendAsync("revision", "notice", FlyoutKind.ShortHint, "StaleState", revision: 1);
    await SendAsync("budget", new string('x', 257), FlyoutKind.ShortHint, "InvalidRequest");
    var state = await client.PublishAsync(FlyoutProvider.State(123), lifetime.Token);
    Require(state.Accepted, "FinalStateRejected");
    await MarkerAsync("complete", new { mode, pid = Environment.ProcessId, sessionId = client.SessionId, revision = 123, results });
    Console.WriteLine("ProbeComplete:sdk:fourKindsReceived=true:revision=123:requests=10");
    await Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token);

    async Task SendAsync(string scenario, string entry, FlyoutKind kind, string expected, long revision = 0, FlyoutScreen screen = FlyoutScreen.Trigger)
    {
        // The SDK owns wire request identity and sequence; these caller values must not escape.
        var request = new FlyoutRequest("caller-ignored", 777, "main", entry, kind, revision, screen);
        var result = await client.RequestFlyoutAsync(request, lifetime.Token);
        Require(result.Code == expected && result.Accepted == (expected == "Received"), "UnexpectedFlyoutResult:" + scenario + ":" + result.Code);
        results.Add(scenario, result.Code);
        await MarkerAsync(scenario, new { mode, pid = Environment.ProcessId, scenario, result.Code, result.Accepted });
        await WaitAsync("after-" + scenario);
    }
}

async Task RunRawAsync()
{
    using var input = Console.OpenStandardInput();
    var launch = await LengthPrefixedJson.ReadAsync<ServiceLaunch>(input, lifetime.Token);
    await using var pipe = new NamedPipeClientStream(".", launch.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(lifetime.Token);
    var welcome = await ExchangeAsync(pipe, new ProtocolMessage
    {
        Kind = MessageKind.Hello,
        ApplicationId = launch.ApplicationId,
        StartRequestId = launch.StartRequestId,
        Ticket = launch.Ticket,
        RequestId = Guid.NewGuid().ToString("N"),
    });
    Require(welcome.Kind == MessageKind.Welcome && !string.IsNullOrWhiteSpace(welcome.SessionId), "WelcomeRejected");
    string session = welcome.SessionId;
    var declaration = await new FlyoutProvider(launch.ApplicationId).GetSnapshotAsync(lifetime.Token);
    var declared = await ExchangeAsync(pipe, Envelope(MessageKind.Declare) with { Declaration = declaration.Declaration, State = declaration.State });
    Require(declared.Result?.Accepted == true, "DeclarationRejected");
    await MarkerAsync("ready", new { mode, pid = Environment.ProcessId, sessionId = session });
    await WaitAsync("run");
    var results = new Dictionary<string, string>();
    await SendAsync("first", Request(5), "Received");
    await SendAsync("duplicate", Request(5), "DuplicateRequest");
    await SendAsync("older", Request(4), "DuplicateRequest");
    await SendAsync("old-session", Request(6) with { SessionId = "previous-session" }, "SessionMismatch");
    await SendAsync("wrong-application", Request(6) with { ApplicationId = "healthy" }, "SessionMismatch");
    await SendAsync("current-after-old", Request(6), "Received");
    await SendAsync("missing-payload", Envelope(MessageKind.FlyoutRequest), "InvalidEnvelope");
    await SendAsync("state-with-flyout", Envelope(MessageKind.State) with
    {
        State = FlyoutProvider.State(999),
        Flyout = Payload(7),
    }, "InvalidEnvelope");
    await SendAsync("declare-with-flyout", Envelope(MessageKind.Declare) with
    {
        Declaration = declaration.Declaration,
        State = FlyoutProvider.State(999),
        Flyout = Payload(7),
    }, "InvalidEnvelope");
    // Receiving the old revision still requires the original interactive declaration and state.
    await SendAsync("current-after-mixed", Request(7), "Received");
    await SendAsync("unknown-channel", Request(8) with { Flyout = Payload(8) with { EntryId = "missing-event", Kind = FlyoutKind.EventGroup } }, "UnknownEntry");
    var final = await ExchangeAsync(pipe, Envelope(MessageKind.State) with { State = FlyoutProvider.State(123) });
    Require(final.Result?.Accepted == true, "FinalStateRejected");
    await pipe.DisposeAsync();
    await MarkerAsync("complete", new { mode, pid = Environment.ProcessId, sessionId = session, revision = 123, results, connectionClosed = true });
    Console.WriteLine("ProbeComplete:raw:revision=123:connectionClosed=true");
    await Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token);

    ProtocolMessage Envelope(MessageKind kind) => new()
    {
        Kind = kind,
        ApplicationId = launch.ApplicationId,
        SessionId = session,
        RequestId = Guid.NewGuid().ToString("N"),
    };
    FlyoutRequest Payload(long sequence) => new(Guid.NewGuid().ToString("N"), sequence, "main", "details", FlyoutKind.TaskbarGroup, 0);
    ProtocolMessage Request(long sequence) => Envelope(MessageKind.FlyoutRequest) with { Flyout = Payload(sequence) };
    async Task SendAsync(string scenario, ProtocolMessage request, string expected)
    {
        var response = await ExchangeAsync(pipe, request);
        Require(response.Result?.Code == expected && response.Result.Accepted == (expected == "Received"), "UnexpectedRawResult:" + scenario + ":" + response.Result?.Code);
        results.Add(scenario, response.Result!.Code);
    }
}

async Task<ProtocolMessage> ExchangeAsync(Stream pipe, ProtocolMessage message)
{
    await LengthPrefixedJson.WriteAsync(pipe, message, lifetime.Token);
    var result = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, lifetime.Token);
    Require(result.Version == ProtocolLimits.Version && result.RequestId == message.RequestId && result.BrokerLoad is null, "InvalidResponse");
    return result;
}

async Task MarkerAsync(string name, object marker)
{
    string path = Path.Combine(directory, name + ".json");
    await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(marker, LengthPrefixedJson.Options));
    File.Move(path + ".tmp", path, overwrite: true);
}

async Task WaitAsync(string signal)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    deadline.CancelAfter(TimeSpan.FromSeconds(5));
    while (!File.Exists(Path.Combine(directory, signal))) await Task.Delay(10, deadline.Token);
}

static void Require(bool condition, string code)
{
    if (!condition) throw new ProtocolException(code);
}

sealed class FlyoutProvider(string applicationId) : IDeclarationProvider
{
    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionSlotDeclaration[] actions = [new("activate")];
        var group = new FeatureGroupDeclaration("main", [new ComponentDeclaration("counter", actions)], [new TaskbarFlyoutDeclaration("details", actions)])
        {
            Hints = [new HintEntryDeclaration("notice", FlyoutKind.ShortHint), new HintEntryDeclaration("adjust", FlyoutKind.InteractiveHint), new HintEntryDeclaration("blocked", FlyoutKind.ShortHint)],
            EventChannels = [new EventChannelDeclaration("events", EventClosePolicy.AutoClose)],
        };
        return Task.FromResult(new ApplicationSnapshot(new ApplicationDeclaration(applicationId, [group]), State(0)));
    }

    public static ApplicationState State(long revision) => new(revision, [new ComponentReading("main", "counter", "flyout-probe:" + revision, revision)]);
}

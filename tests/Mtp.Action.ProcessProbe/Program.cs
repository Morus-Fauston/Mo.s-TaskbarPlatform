using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Sdk;
using Mtp.Transport;

if (args.Length != 2 || args[0] is not ("normal" or "fail" or "slow" or "raw-results")) return 2;
string mode = args[0];
string directory = Path.GetFullPath(args[1]);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    if (mode == "raw-results")
    {
        await RunRawResultsAsync();
        return 0;
    }
    await using var client = await SdkClient.ConnectFromStandardInputAsync(id => new ActionProvider(id, mode, directory), lifetime.Token);
    await Marker.WriteAsync(directory, "ready", new { mode, pid = Environment.ProcessId, sessionId = client.SessionId });
    Console.WriteLine("ActionProbeReady:" + mode);
    await Task.Delay(TimeSpan.FromSeconds(25), lifetime.Token);
    return 0;
}
catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ArgumentException)
{
    string code = error is ProtocolException protocol ? protocol.Code : error.GetType().Name;
    Console.Error.WriteLine("ActionProbeFailed:" + code);
    try { await Marker.WriteAsync(directory, "failed", new { mode, pid = Environment.ProcessId, code }); }
    catch (IOException) { }
    return 1;
}

async Task RunRawResultsAsync()
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
    var snapshot = await new ActionProvider(launch.ApplicationId, "normal", directory).GetSnapshotAsync(lifetime.Token);
    var declared = await ExchangeAsync(Envelope(MessageKind.Declare) with { Declaration = snapshot.Declaration, State = snapshot.State });
    Require(declared.Result?.Accepted == true, "DeclarationRejected");
    await Marker.WriteAsync(directory, "ready", new { mode, pid = Environment.ProcessId, sessionId = session });

    var request = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, lifetime.Token);
    Require(request.Kind == MessageKind.ActionRequest && request.Action is not null, "ActionExpected");
    var invocation = request.Action!;
    var valid = new ActionCompletion(invocation.RequestId, invocation.Sequence, ProtocolResult.Success("ActionSucceeded"), State(1, 10));
    var envelope = Envelope(MessageKind.ActionCompleted) with { RequestId = invocation.RequestId, ActionCompletion = valid };
    var oldSession = await ExchangeAsync(envelope with { SessionId = "previous-session", ActionCompletion = valid with { State = State(900, 900) } });
    Require(oldSession.Result?.Code == "SessionMismatch", "OldSessionNotRejected");
    await LengthPrefixedJson.WriteAsync(pipe, envelope with { ActionCompletion = valid with { Sequence = invocation.Sequence + 1, State = State(901, 901) } }, lifetime.Token);
    var invalidBarrier = await ExchangeAsync(Envelope(MessageKind.State) with { State = State(0, 0) });
    Require(invalidBarrier.Result?.Code == "StaleRevision", "InvalidResultBarrierFailed");
    await Marker.WriteAsync(directory, "invalid-results-checked", new
    {
        pid = Environment.ProcessId,
        invocation.RequestId,
        invocation.Sequence,
        oldSession = oldSession.Result!.Code,
        wrongSequenceBarrier = invalidBarrier.Result!.Code,
    });
    await WaitAsync("release-1");
    await LengthPrefixedJson.WriteAsync(pipe, envelope, lifetime.Token);
    await Marker.WriteAsync(directory, "correct-result-sent", new { pid = Environment.ProcessId, invocation.RequestId, invocation.Sequence });
    await WaitAsync("release-2");
    await LengthPrefixedJson.WriteAsync(pipe, envelope with { ActionCompletion = valid with { State = State(999, 999) } }, lifetime.Token);
    var duplicateBarrier = await ExchangeAsync(Envelope(MessageKind.State) with { State = State(2, 10) });
    Require(duplicateBarrier.Result?.Accepted == true, "DuplicateResultApplied");
    await Marker.WriteAsync(directory, "complete", new
    {
        pid = Environment.ProcessId,
        invocation.RequestId,
        invocation.Sequence,
        duplicateBarrier = duplicateBarrier.Result!.Code,
        revision = 2,
        number = 10,
    });
    await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token);

    ProtocolMessage Envelope(MessageKind kind) => new()
    {
        Kind = kind,
        ApplicationId = launch.ApplicationId,
        SessionId = session,
        RequestId = Guid.NewGuid().ToString("N"),
    };
    async Task<ProtocolMessage> ExchangeAsync(ProtocolMessage message)
    {
        await LengthPrefixedJson.WriteAsync(pipe, message, lifetime.Token);
        var result = await LengthPrefixedJson.ReadAsync<ProtocolMessage>(pipe, lifetime.Token);
        Require(result.Version == ProtocolLimits.Version && result.RequestId == message.RequestId, "InvalidResponse");
        return result;
    }
    async Task WaitAsync(string signal)
    {
        while (!File.Exists(Path.Combine(directory, signal))) await Task.Delay(10, lifetime.Token);
    }
    static ApplicationState State(long revision, long value) => new(revision, [new ComponentReading("main", "counter", value.ToString(CultureInfo.InvariantCulture), value)]);
    static void Require(bool condition, string code)
    {
        if (!condition) throw new ProtocolException(code);
    }
}

sealed class ActionProvider(string applicationId, string mode, string directory) : IDeclarationProvider, IActionHandler
{
    private readonly object gate = new();
    private long revision;
    private long count;
    private long calls;

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionSlotDeclaration[] actions = [new("activate")];
        var declaration = new ApplicationDeclaration(applicationId,
            [new FeatureGroupDeclaration("main", [new ComponentDeclaration("counter", actions)], [new TaskbarFlyoutDeclaration("details", actions)])]);
        lock (gate) return Task.FromResult(new ApplicationSnapshot(declaration, State()));
    }

    public async Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Slot.ApplicationId != applicationId || invocation.Slot.FeatureGroupId != "main" ||
            invocation.Slot.EntryKind != ActionEntryKind.Component || invocation.Slot.EntryId != "counter" ||
            invocation.Slot.ActionSlotId != "activate" || invocation.Parameter.Kind != ActionParameterKind.None ||
            invocation.Parameter.Boolean is not null || invocation.Parameter.Number is not null || invocation.Parameter.Text is not null)
            return new(invocation.RequestId, invocation.Sequence, ProtocolResult.Reject("ActionNotAvailable", "动作不可用"));
        long call;
        lock (gate) call = ++calls;
        await Marker.WriteAsync(directory, "started-" + invocation.Sequence, new
        {
            pid = Environment.ProcessId,
            invocation.RequestId,
            invocation.Sequence,
            call,
            mode,
        });
        if (mode == "slow")
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(12));
            string release = Path.Combine(directory, "release-" + invocation.Sequence.ToString(CultureInfo.InvariantCulture));
            while (!File.Exists(release)) await Task.Delay(10, wait.Token);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ActionCompletion completion;
        lock (gate)
        {
            if (mode == "fail") completion = new(invocation.RequestId, invocation.Sequence, ProtocolResult.Reject("BusinessRejected", "夹具业务拒绝"));
            else
            {
                count += 10;
                revision++;
                completion = new(invocation.RequestId, invocation.Sequence, ProtocolResult.Success("ActionSucceeded"), State());
            }
        }
        await Marker.WriteAsync(directory, "finished-" + invocation.Sequence, new
        {
            pid = Environment.ProcessId,
            invocation.RequestId,
            invocation.Sequence,
            call,
            completion.Result.Code,
            completion.Result.Accepted,
            revision = completion.State?.Revision ?? 0,
            number = completion.State?.Components[0].Number ?? 0,
        });
        Console.WriteLine($"ActionProbeCompleted:sequence={invocation.Sequence}:call={call}:result={completion.Result.Code}");
        return completion;
    }

    private ApplicationState State() => new(revision,
        [new ComponentReading("main", "counter", count.ToString(CultureInfo.InvariantCulture), count)]);
}

static class Marker
{
    public static async Task WriteAsync(string directory, string name, object value)
    {
        string path = Path.Combine(directory, name + ".json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value, LengthPrefixedJson.Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

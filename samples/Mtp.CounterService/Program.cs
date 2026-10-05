using Mtp.Contracts;
using Mtp.Sdk;

try
{
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    var iterations = GetOption("--iterations", 10000, 0, 100000);
    var interval = GetOption("--interval-ms", 100, 1, 60000);
    CounterProvider? provider = null;
    await using var client = await SdkClient.ConnectFromStandardInputAsync(applicationId => provider = new CounterProvider(applicationId), shutdown.Token);
    for (var tick = 1; tick <= iterations; tick++)
    {
        await Task.Delay(interval, shutdown.Token);
        var result = await client.PublishAsync(provider!.Tick(), shutdown.Token);
        // A newer action confirmation can overtake an already captured automatic tick.
        if (!result.Accepted && result.Code != "StaleRevision") { Console.Error.WriteLine("CounterRejected:" + result.Code); return 2; }
    }
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine("CounterStopped:" + exception.GetType().Name);
    return 1;
}

int GetOption(string name, int fallback, int minimum, int maximum)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return fallback;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value < minimum || value > maximum)
        throw new ArgumentException("InvalidCounterOption");
    return value;
}

sealed class CounterProvider(string applicationId) : IDeclarationProvider, IActionHandler
{
    private readonly object gate = new();
    private long count;
    private long revision;

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionSlotDeclaration[] actions = [new("activate")];
        var declaration = new ApplicationDeclaration(applicationId,
            [new FeatureGroupDeclaration("main", [new ComponentDeclaration("counter", actions)], [new TaskbarFlyoutDeclaration("details", actions)])]);
        lock (gate) return Task.FromResult(new ApplicationSnapshot(declaration, CreateState()));
    }

    public ApplicationState Tick()
    {
        lock (gate)
        {
            count++;
            revision++;
            return CreateState();
        }
    }

    public Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.Slot.ApplicationId != applicationId || invocation.Slot.FeatureGroupId != "main" ||
            invocation.Slot.ActionSlotId != "activate" ||
            (invocation.Slot.EntryKind == ActionEntryKind.Component ? invocation.Slot.EntryId != "counter" :
             invocation.Slot.EntryKind != ActionEntryKind.TaskbarFlyout || invocation.Slot.EntryId != "details") ||
            invocation.Parameter.Kind != ActionParameterKind.None || invocation.Parameter.Boolean is not null ||
            invocation.Parameter.Number is not null || invocation.Parameter.Text is not null)
            return Task.FromResult(new ActionCompletion(invocation.RequestId, invocation.Sequence,
                ProtocolResult.Reject("ActionNotAvailable", "计数器动作不可用")));
        ApplicationState state;
        lock (gate)
        {
            count += 10;
            revision++;
            state = CreateState();
        }
        Console.WriteLine($"CounterActionConfirmed:increment=10:revision={state.Revision}");
        return Task.FromResult(new ActionCompletion(invocation.RequestId, invocation.Sequence,
            ProtocolResult.Success("ActionSucceeded"), state));
    }

    private ApplicationState CreateState() => new(revision,
        [new ComponentReading("main", "counter", count.ToString(System.Globalization.CultureInfo.InvariantCulture), count)]);
}

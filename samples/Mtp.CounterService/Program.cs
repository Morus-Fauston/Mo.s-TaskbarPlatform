using Mtp.Contracts;
using Mtp.Sdk;

try
{
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    var iterations = GetOption("--iterations", 10000, 0, 100000);
    var interval = GetOption("--interval-ms", 100, 1, 60000);
    bool dynamic = args.Contains("--dynamic", StringComparer.Ordinal);
    bool templates = args.Contains("--templates", StringComparer.Ordinal);
    bool timers = args.Contains("--timers", StringComparer.Ordinal);
    bool presets = args.Contains("--presets", StringComparer.Ordinal);
    if ((dynamic ? 1 : 0) + (templates ? 1 : 0) + (timers ? 1 : 0) + (presets ? 1 : 0) > 1) throw new ArgumentException("ConflictingDemoModes");
    Func<ApplicationState>? readTick = null;
    TimerDemoProvider? timerProvider = null;
    PresetDemoProvider? presetProvider = null;
    await using var client = await SdkClient.ConnectFromStandardInputAsync(applicationId =>
    {
        if (presets) return presetProvider = new PresetDemoProvider(applicationId);
        if (timers) return timerProvider = new TimerDemoProvider(applicationId);
        if (dynamic)
        {
            var provider = new DynamicDemoProvider(applicationId);
            readTick = provider.Tick;
            return provider;
        }
        var counter = new CounterProvider(applicationId, templates);
        readTick = counter.Tick;
        return (IDeclarationProvider)counter;
    }, shutdown.Token);
    timerProvider?.Bind(client);
    presetProvider?.Bind(client);
    for (var tick = 1; tick <= iterations; tick++)
    {
        await Task.Delay(interval, shutdown.Token);
        if (timerProvider is not null || presetProvider is not null) continue; // Local Host time never turns into SDK publication or renewal.
        var result = await client.PublishAsync(readTick!(), shutdown.Token);
        // A newer action confirmation can overtake an already captured automatic tick.
        if (!result.Accepted && result.Code is not ("StaleRevision" or "Reconnecting" or "Unavailable")) { Console.Error.WriteLine("CounterRejected:" + result.Code); return 2; }
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

sealed class CounterProvider(string applicationId, bool templates = false) : IDeclarationProvider, IActionHandler
{
    private readonly object gate = new();
    private long count;
    private long revision;
    private bool enabled = true;
    private double level = 25;

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionSlotDeclaration[] actions = templates
            ? [new("activate"), new("enabled", ActionParameterKind.Boolean), new("level", ActionParameterKind.Number)] : [new("activate")];
        var declaration = new ApplicationDeclaration(applicationId,
            [new FeatureGroupDeclaration("main", [new ComponentDeclaration("counter", actions, Template: templates ? TemplateCounterDeclaration.Create() : null)],
                [new TaskbarFlyoutDeclaration("details", actions)])],
            templates ? [new ImageResourceDeclaration("status", ImageResourceFormat.Png)] : null);
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

    public async Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (templates && invocation.Slot.ApplicationId == applicationId && invocation.Slot.FeatureGroupId == "main" &&
            invocation.Slot.EntryKind == ActionEntryKind.Component && invocation.Slot.EntryId == "counter" &&
            invocation.Slot.ActionSlotId is "enabled" or "level")
        {
            await Task.Delay(100, cancellationToken);
            lock (gate)
            {
                if (invocation.Slot.ActionSlotId == "enabled" && invocation.Parameter is { Kind: ActionParameterKind.Boolean, Boolean: { } flag })
                    enabled = flag;
                else if (invocation.Slot.ActionSlotId == "level" && invocation.Parameter is { Kind: ActionParameterKind.Number, Number: { } value } &&
                    double.IsFinite(value) && value >= 0 && value <= 80)
                    level = value;
                else return new(invocation.RequestId, invocation.Sequence, ProtocolResult.Reject("DemoValueRejected", "演示服务只接受0至80的读数"));
                revision++;
                return new(invocation.RequestId, invocation.Sequence, ProtocolResult.Success("ActionSucceeded"), CreateState());
            }
        }
        if (invocation.Slot.ApplicationId != applicationId || invocation.Slot.FeatureGroupId != "main" ||
            invocation.Slot.ActionSlotId != "activate" ||
            (invocation.Slot.EntryKind == ActionEntryKind.Component ? invocation.Slot.EntryId != "counter" :
             invocation.Slot.EntryKind != ActionEntryKind.TaskbarFlyout || invocation.Slot.EntryId != "details") ||
            invocation.Parameter.Kind != ActionParameterKind.None || invocation.Parameter.Boolean is not null ||
            invocation.Parameter.Number is not null || invocation.Parameter.Text is not null)
            return new ActionCompletion(invocation.RequestId, invocation.Sequence,
                ProtocolResult.Reject("ActionNotAvailable", "计数器动作不可用"));
        ApplicationState state;
        lock (gate)
        {
            count += 10;
            revision++;
            state = CreateState();
        }
        Console.WriteLine($"CounterActionConfirmed:increment=10:revision={state.Revision}");
        return new ActionCompletion(invocation.RequestId, invocation.Sequence,
            ProtocolResult.Success("ActionSucceeded"), state);
    }

    private ApplicationState CreateState() => new(revision,
        [new ComponentReading("main", "counter", count.ToString(System.Globalization.CultureInfo.InvariantCulture), count)],
        TemplateEntries: templates ? [new TemplateEntryState(new("main", TemplateEntryKind.Component, "counter"),
            [new("count", new(TemplateValueKind.Text, Text: count.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                new("enabled", new(TemplateValueKind.Boolean, Boolean: enabled)), new("level", new(TemplateValueKind.Number, Number: level))])] : null);
}

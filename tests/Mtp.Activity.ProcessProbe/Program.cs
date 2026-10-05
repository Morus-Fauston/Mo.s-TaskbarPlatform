using System.Text.Json;
using Mtp.Contracts;
using Mtp.Sdk;
using Mtp.Transport;

if (args.Length != 1) return 2;
string directory = Path.GetFullPath(args[0]);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(35));
try
{
    ActivityProvider? provider = null;
    await using var client = await SdkClient.ConnectFromStandardInputAsync(id => provider = new ActivityProvider(id, directory), lifetime.Token);
    provider!.Bind(client);
    await Marker.WriteAsync(directory, "ready", new
    {
        pid = Environment.ProcessId,
        sessionId = client.SessionId,
        connected = client.IsConnected,
        initialResult = client.InitialPublicationResult,
    });
    for (int command = 1; command <= 32; command++)
    {
        string commandPath = Path.Combine(directory, "command-" + command);
        while (!File.Exists(commandPath)) await Task.Delay(10, lifetime.Token);
        string scenario = await File.ReadAllTextAsync(commandPath, lifetime.Token);
        if (scenario == "report")
            await Marker.WriteAsync(directory, "command-" + command + "-result", new
            {
                pid = Environment.ProcessId,
                sessionId = client.SessionId,
                client.IsConnected,
                initialResult = client.InitialPublicationResult,
                publications = provider.Publications,
            });
        else
        {
            var result = await provider.PublishScenarioAsync(scenario, lifetime.Token);
            await Marker.WriteAsync(directory, "command-" + command + "-result", new { pid = Environment.ProcessId, sessionId = client.SessionId, scenario, result });
        }
    }
    return 0;
}
catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ArgumentException)
{
    string code = error is ProtocolException protocol ? protocol.Code : error.GetType().Name;
    try { await Marker.WriteAsync(directory, "failed", new { pid = Environment.ProcessId, code }); }
    catch (IOException) { }
    Console.Error.WriteLine("ActivityProbeFailed:" + code);
    return 1;
}

sealed class ActivityProvider(string applicationId, string directory) : IDeclarationProvider, IDisplayPermissionObserver
{
    private readonly TaskCompletionSource<SdkClient> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private ApplicationState state = CreateState(0);
    private int publications;
    private int snapshots;
    public int Publications { get { lock (gate) return publications; } }

    public void Bind(SdkClient client) => connected.TrySetResult(client);

    public async Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var presentation = new ItemPresentation(PresetTemplate.Status, ContentFields.Status, new ContentWidth(Tier: WidthTier.Small));
        var island = new DynamicContentDeclaration(DynamicContentKind.LiveIsland, [new ItemStructureDeclaration("activity", true, presentation)]);
        ActionSlotDeclaration[] actions = [new("activate")];
        var declaration = new ApplicationDeclaration(applicationId, [new FeatureGroupDeclaration("main",
            [new ComponentDeclaration("ordinary", actions), new ComponentDeclaration("island", actions) { DynamicContent = island }],
            [new TaskbarFlyoutDeclaration("details", actions)])]);
        ApplicationSnapshot snapshot;
        int call;
        lock (gate) { snapshot = new ApplicationSnapshot(declaration, state); call = ++snapshots; }
        await Marker.WriteAsync(directory, "snapshot-" + call, new { pid = Environment.ProcessId, call, revision = snapshot.State.Revision });
        return snapshot;
    }

    public async Task OnDisplayPermissionsChangedAsync(DisplayPermissionSnapshot snapshot, CancellationToken cancellationToken)
    {
        bool allowed = snapshot.Entries.Any(entry => entry.FeatureGroupId == "main" && entry.ComponentId == "island" && entry.Allowed);
        await Marker.WriteAsync(directory, "permission-" + snapshot.Revision, new { pid = Environment.ProcessId, snapshot.Revision, allowed });
        if (!allowed) return;
        if (File.Exists(Path.Combine(directory, "hold-permission")))
        {
            await Marker.WriteAsync(directory, "permission-held", new { pid = Environment.ProcessId, snapshot.Revision });
            try
            {
                while (!File.Exists(Path.Combine(directory, "release-permission"))) await Task.Delay(10, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                await Marker.WriteAsync(directory, "permission-cancelled", new { pid = Environment.ProcessId, snapshot.Revision });
                throw;
            }
        }
        var client = await connected.Task.WaitAsync(cancellationToken);
        ApplicationState next;
        int publication;
        lock (gate)
        {
            next = state = CreateState(state.Revision + 1);
            publication = ++publications;
        }
        var result = await client.PublishAsync(next, cancellationToken);
        await Marker.WriteAsync(directory, "permission-published", new
        {
            pid = Environment.ProcessId,
            sessionId = client.SessionId,
            permissionRevision = snapshot.Revision,
            publication,
            stateRevision = next.Revision,
            result,
        });
        await Marker.WriteAsync(directory, "publication-" + publication, new { pid = Environment.ProcessId, permissionRevision = snapshot.Revision, stateRevision = next.Revision, result });
    }

    public async Task<ProtocolResult> PublishScenarioAsync(string scenario, CancellationToken cancellationToken)
    {
        var client = await connected.Task.WaitAsync(cancellationToken);
        ApplicationState next;
        lock (gate)
        {
            long revision = state.Revision + 1;
            var basis = CreateState(revision);
            var entry = basis.DynamicEntries![0];
            var a = entry.Content.Activities[0];
            var item = entry.Content.Items[0];
            DynamicContentState content = scenario switch
            {
                "update-add" => new([a, new ActivityState("running-b", DateTimeOffset.UtcNow.AddHours(1))],
                    [item with { ItemId = "shared-item", ActivityIds = ["running-a", "running-b"] }, item with { ItemId = "new-only-item", ActivityIds = ["running-b"] }]),
                "end-a" => new([a with { Ended = true }], []),
                "expires-short" => new([a with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(2) }], [item]),
                _ => throw new ArgumentException("UnknownScenario"),
            };
            next = state = basis with { DynamicEntries = [entry with { Content = content }] };
        }
        return await client.PublishAsync(next, cancellationToken);
    }

    private static ApplicationState CreateState(long revision) => new(revision,
        [new ComponentReading("main", "ordinary", "ordinary remains available", 7), new ComponentReading("main", "island", "running", 1)],
        [new DynamicEntryState("main", "island", new DynamicContentState(
            [new ActivityState("running-a", DateTimeOffset.UtcNow.AddHours(1))],
            [new DynamicItemState("item-a", "activity", ["running-a"], new DynamicItemFields(Status: new StatusReading("still running")))]))]);
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

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Sdk;
using Mtp.Transport;

if (args.Length != 2 || args[0] is not ("sdk" or "stubborn")) return 2;
string mode = args[0];
string directory = Path.GetFullPath(args[1]);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(70));
try
{
    await Marker.WriteAsync(directory, "boot-" + Environment.ProcessId, new { mode, pid = Environment.ProcessId });
    if (mode == "stubborn" && File.Exists(Path.Combine(directory, "force-unresponsive")))
    {
        await Task.Delay(TimeSpan.FromSeconds(65), lifetime.Token);
        return 0;
    }
    SnapshotProvider? provider = null;
    await using var client = await SdkClient.ConnectFromStandardInputAsync(id => provider ??= new SnapshotProvider(id, directory), lifetime.Token);
    await Marker.WriteAsync(directory, "ready-" + Environment.ProcessId, new { mode, pid = Environment.ProcessId, sessionId = client.SessionId });
    while (!File.Exists(Path.Combine(directory, "dispose-sdk"))) await Task.Delay(10, lifetime.Token);
    var elapsed = Stopwatch.StartNew();
    await client.DisposeAsync();
    await client.DisposeAsync();
    await Marker.WriteAsync(directory, "disposed-" + Environment.ProcessId, new { mode, pid = Environment.ProcessId, elapsedMs = elapsed.ElapsedMilliseconds });
    // Deliberately keep the owned process alive and stop reading inherited credentials.
    await Task.Delay(TimeSpan.FromSeconds(60), lifetime.Token);
    return 0;
}
catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ArgumentException)
{
    string code = error is ProtocolException protocol ? protocol.Code : error.GetType().Name;
    Console.Error.WriteLine("RecoveryProbeFailed:" + code);
    try { await Marker.WriteAsync(directory, "failed-" + Environment.ProcessId, new { mode, pid = Environment.ProcessId, code }); }
    catch (IOException) { }
    return 1;
}

sealed class SnapshotProvider(string applicationId, string directory) : IDeclarationProvider, IActionHandler
{
    private readonly object gate = new();
    private int snapshots;
    private long revision;
    private long count = 7;

    public async Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string current = Path.Combine(directory, "current.json");
        CurrentReading? update = File.Exists(current)
            ? JsonSerializer.Deserialize<CurrentReading>(await File.ReadAllTextAsync(current, cancellationToken), LengthPrefixedJson.Options)
            : null;
        ApplicationSnapshot snapshot;
        int call;
        lock (gate)
        {
            if (update is not null && update.Revision > revision) { revision = update.Revision; count = update.Number; }
            call = ++snapshots;
            ActionSlotDeclaration[] actions = [new("activate")];
            var declaration = new ApplicationDeclaration(applicationId,
                [new FeatureGroupDeclaration("main", [new ComponentDeclaration("counter", actions)], [new TaskbarFlyoutDeclaration("details", actions)])]);
            snapshot = new ApplicationSnapshot(declaration, State());
        }
        await Marker.WriteAsync(directory, "snapshot-" + Environment.ProcessId + "-" + call, new
        {
            pid = Environment.ProcessId,
            call,
            snapshot.State.Revision,
            number = snapshot.State.Components[0].Number,
        });
        return snapshot;
    }

    public Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            count += 10;
            revision++;
            return Task.FromResult(new ActionCompletion(invocation.RequestId, invocation.Sequence, ProtocolResult.Success("ActionSucceeded"), State()));
        }
    }

    private ApplicationState State() => new(revision, [new ComponentReading("main", "counter", count.ToString(CultureInfo.InvariantCulture), count)]);
    private sealed record CurrentReading(long Revision, long Number);
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

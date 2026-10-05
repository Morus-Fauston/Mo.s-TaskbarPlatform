using System.Text.Json;
using Mtp.Contracts;
using Mtp.Sdk;
using Mtp.Transport;

if (args.Length != 2 || args[0] is not ("phases" or "invalid-template")) return 2;
string mode = args[0];
string directory = Path.GetFullPath(args[1]);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    if (mode == "invalid-template")
    {
        try
        {
            await using var unexpected = await SdkClient.ConnectFromStandardInputAsync(id => new DynamicProvider(id, invalidTemplate: true), lifetime.Token);
            throw new InvalidOperationException("Invalid template was accepted.");
        }
        catch (ProtocolException rejection) when (rejection.Code == "dynamic_structure_invalid")
        {
            await MarkerAsync("complete", new { mode, pid = Environment.ProcessId, result = rejection.Code });
            Console.WriteLine("ProbeComplete:invalid-template:dynamic_structure_invalid");
            return 0;
        }
    }

    await using var client = await SdkClient.ConnectFromStandardInputAsync(id => new DynamicProvider(id), lifetime.Token);
    string session = client.SessionId;
    await CheckpointAsync("ready", 0);

    var added = DynamicProvider.CreateState(1);
    await PublishAcceptedAsync(added);
    await CheckpointAsync("added", 1);

    var updated = DynamicProvider.CreateState(2);
    await PublishAcceptedAsync(updated);
    await CheckpointAsync("updated", 2);

    var errors = new Dictionary<string, string>();
    var ordinary = updated.DynamicEntries![0];
    var island = updated.DynamicEntries[1];
    var firstOrdinary = ordinary.Content.Items[0];
    var firstIsland = island.Content.Items[0];
    await RejectAsync("unknown-structure", ReplaceOrdinaryItems([firstOrdinary with { StructureId = "undeclared-structure" }]), "dynamic_structure_invalid");
    await RejectAsync("invalid-item-id", ReplaceOrdinaryItems([firstOrdinary with { ItemId = " " }]), "dynamic_identity_invalid");
    await RejectAsync("duplicate-item-id", ReplaceOrdinaryItems([firstOrdinary, firstOrdinary]), "dynamic_duplicate_identity");
    await RejectAsync("cross-activity-reference", ReplaceIsland(island.Content with { Items = [firstIsland with { ActivityIds = ["other-entry-activity"] }] }), "dynamic_reference_invalid");
    await RejectAsync("expired-activity", ReplaceIsland(island.Content with
    {
        Activities = [island.Content.Activities[0] with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }],
        Items = [firstIsland],
    }), "dynamic_expiry_invalid");
    await RejectAsync("excess-retention", ReplaceIsland(island.Content with
    {
        Activities = [island.Content.Activities[0] with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(25) }],
        Items = [firstIsland],
    }), "dynamic_expiry_invalid");
    await RejectAsync("item-budget", ReplaceOrdinaryItems(Enumerable.Range(0, 129)
        .Select(i => firstOrdinary with { ItemId = "budget-item-" + i }).ToArray()), "dynamic_budget_exceeded");
    await RejectAsync("activity-budget", ReplaceIsland(new DynamicContentState(Enumerable.Range(0, 65)
        .Select(i => new ActivityState("budget-activity-" + i, DateTimeOffset.UtcNow.AddHours(1))).ToArray(), [])), "dynamic_budget_exceeded");
    await RejectAsync("duplicate-activity-id", ReplaceIsland(new DynamicContentState(
        [island.Content.Activities[0], island.Content.Activities[0]], [firstIsland])), "dynamic_duplicate_identity");
    await RejectAsync("invalid-fields", ReplaceIsland(island.Content with
    {
        Items = [firstIsland with { Fields = firstIsland.Fields with { Counter = new CounterReading(CounterSemantics.CurrentIndex, -1, 10) } }],
    }), "dynamic_fields_invalid");
    Require(client.SessionId == session, "SessionChangedDuringUpdates");
    await MarkerAsync("rejected", new { mode, pid = Environment.ProcessId, sessionId = session, revision = 2, rejections = errors });
    await WaitAsync("after-rejected");

    var removed = DynamicProvider.CreateState(3);
    await PublishAcceptedAsync(removed);
    await CheckpointAsync("removed", 3);

    await PublishAcceptedAsync(DynamicProvider.CreateState(4));
    Require(client.SessionId == session, "SessionChangedDuringRemoval");
    await MarkerAsync("complete", new { mode, pid = Environment.ProcessId, sessionId = session, revision = 4, rejections = errors.Count });
    Console.WriteLine("ProbeComplete:phases:revision=4:rejections=10:sameSession=true");
    await Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token);
    return 0;

    ApplicationState ReplaceOrdinaryItems(IReadOnlyList<DynamicItemState> items) => updated with
    {
        Revision = 100,
        DynamicEntries = [ordinary with { Content = ordinary.Content with { Items = items } }, island],
    };
    ApplicationState ReplaceIsland(DynamicContentState content) => updated with
    {
        Revision = 100,
        DynamicEntries = [ordinary, island with { Content = content }],
    };
    async Task RejectAsync(string scenario, ApplicationState state, string expected)
    {
        var result = await client.PublishAsync(state, lifetime.Token);
        Require(!result.Accepted && result.Code == expected, "UnexpectedRejection:" + scenario + ":" + result.Code);
        errors.Add(scenario, result.Code);
    }
    async Task PublishAcceptedAsync(ApplicationState state)
    {
        var result = await client.PublishAsync(state, lifetime.Token);
        Require(result.Accepted, result.Code);
    }
    async Task CheckpointAsync(string name, long revision)
    {
        await MarkerAsync(name, new { mode, pid = Environment.ProcessId, sessionId = session, revision });
        await WaitAsync("after-" + name);
    }
}
catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ArgumentException)
{
    string code = error is ProtocolException protocol ? protocol.Code : error.GetType().Name;
    Console.Error.WriteLine("ProbeFailed:" + mode + ":" + code);
    try { await MarkerAsync("failed", new { mode, pid = Environment.ProcessId, code }); }
    catch (IOException) { }
    return 1;
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

sealed class DynamicProvider(string applicationId, bool invalidTemplate = false) : IDeclarationProvider
{
    private const ContentFields CompositeFields = ContentFields.Timer | ContentFields.Progress | ContentFields.Counter | ContentFields.Status;

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordinaryPresentation = new ItemPresentation(PresetTemplate.Progress,
            invalidTemplate ? ContentFields.Timer : ContentFields.Progress, new ContentWidth(Slots: 1));
        var ordinary = new DynamicContentDeclaration(DynamicContentKind.OrdinaryItems,
        [new ItemStructureDeclaration("ring", true, ordinaryPresentation,
            ordinaryPresentation with { Width = new ContentWidth(Slots: 3) },
            ExpandTargetStructureIds: ["ring"], Animation: SemanticAnimation.ContentChange)]);
        var islandPresentation = new ItemPresentation(PresetTemplate.Composite, CompositeFields, new ContentWidth(Tier: WidthTier.Medium));
        var island = new DynamicContentDeclaration(DynamicContentKind.LiveIsland,
        [new ItemStructureDeclaration("activity-item", true, islandPresentation,
            islandPresentation with { Width = new ContentWidth(Tier: WidthTier.Large) },
            ExpandTargetStructureIds: ["activity-item"], Animation: SemanticAnimation.EnterExit)], DynamicGrouping.UserChoice);
        ActionSlotDeclaration[] actions = [new("activate")];
        var declaration = new ApplicationDeclaration(applicationId,
        [new FeatureGroupDeclaration("main",
        [new ComponentDeclaration("ordinary", actions) { DynamicContent = ordinary },
         new ComponentDeclaration("island", actions) { DynamicContent = island }],
        [new TaskbarFlyoutDeclaration("details", actions)])]);
        return Task.FromResult(new ApplicationSnapshot(declaration, CreateState(0)));
    }

    public static ApplicationState CreateState(long revision)
    {
        DateTimeOffset reference = DateTimeOffset.UtcNow;
        var activities = new List<ActivityState>();
        var islandItems = new List<DynamicItemState>();
        var ordinaryItems = new List<DynamicItemState>();
        if (revision is 1 or 2 or 3)
        {
            activities.Add(new ActivityState("business-a", reference.AddHours(1), Order: 0));
            islandItems.Add(new DynamicItemState("visible-a", "activity-item", ["business-a"], new DynamicItemFields(
                Timer: new TimerBasis(TimerDirection.CountDown, reference, revision == 1 ? 90_000 : 45_000, IsPaused: revision >= 2, ShowOvertime: revision >= 2),
                Progress: new ProgressReading(ProgressMode.Determinate, revision == 1 ? 25 : 45, 100),
                Counter: new CounterReading(CounterSemantics.CurrentIndex, 7, 10),
                Status: new StatusReading(revision == 1 ? "运行" : "暂停后调整", StatusMarker.Attention))));
            ordinaryItems.Add(new DynamicItemState("ring-a", "ring", [], new DynamicItemFields(Progress: new ProgressReading(ProgressMode.Determinate, revision == 1 ? 20 : 45, 100))));
        }
        if (revision is 1 or 2)
        {
            activities.Add(new ActivityState("business-b", reference.AddHours(1), Order: 1));
            islandItems.Add(new DynamicItemState("visible-b", "activity-item", ["business-b"], new DynamicItemFields(
                Timer: new TimerBasis(TimerDirection.CountUp, reference, revision == 1 ? 12_000 : 15_000),
                Progress: new ProgressReading(ProgressMode.Indeterminate),
                Counter: new CounterReading(CounterSemantics.CompletedCount, 2, 8),
                Status: new StatusReading("等待", StatusMarker.Normal))));
            ordinaryItems.Add(new DynamicItemState("ring-b", "ring", [], new DynamicItemFields(Progress: new ProgressReading(ProgressMode.Indeterminate))));
        }
        if (revision == 2)
        {
            activities.Add(new ActivityState("business-c", reference.AddHours(1), Order: 2));
            islandItems.Add(new DynamicItemState("visible-c", "activity-item", ["business-c"], new DynamicItemFields(
                Timer: new TimerBasis(TimerDirection.CountDown, reference, 0),
                Progress: new ProgressReading(ProgressMode.Determinate, 100, 100),
                Counter: new CounterReading(CounterSemantics.CompletedCount, 8, 8),
                Status: new StatusReading("等待提供方结束", StatusMarker.Normal))));
            ordinaryItems.Add(new DynamicItemState("ring-c", "ring", [], new DynamicItemFields(Progress: new ProgressReading(ProgressMode.Determinate, 60, 100))));
        }
        return new ApplicationState(revision,
            [new ComponentReading("main", "ordinary", "ordinary:" + revision, revision), new ComponentReading("main", "island", "island:" + revision, revision)])
        {
            DynamicEntries =
            [new DynamicEntryState("main", "ordinary", new DynamicContentState([], ordinaryItems)),
             new DynamicEntryState("main", "island", new DynamicContentState(activities, islandItems))],
        };
    }
}

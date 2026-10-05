using Mtp.Contracts;
using Mtp.Sdk;

internal sealed class OrganizationDemoProvider : IDeclarationProvider, IDisplayPermissionObserver, IActionHandler
{
    private const int MaximumActivities = 8;
    private static readonly string[] Commands = ["add", "end", "update", "reset", "shared"];
    private readonly object gate = new();
    private readonly ApplicationDeclaration declaration;
    private readonly TimeProvider clock;
    private readonly TaskCompletionSource<Func<ApplicationState, CancellationToken, Task<ProtocolResult>>> publisher = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<Business> businesses = [];
    private IReadOnlyList<string> sharedIds = Array.Empty<string>();
    private ApplicationState state;
    private long revision;
    private long nextActivity = 1;
    private long epoch;
    private long permissionRevision;
    private long publicationOwner;
    private bool allowed;
    private bool confirmed;
    private bool initialized;
    private bool shared = true;
    private int updatePhase;

    public OrganizationDemoProvider(string applicationId, TimeProvider? timeProvider = null,
        Func<ApplicationState, CancellationToken, Task<ProtocolResult>>? publish = null)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 || applicationId != applicationId.Trim())
            throw new ArgumentException("Invalid organization demo application identity.", nameof(applicationId));
        declaration = CreateDeclaration(applicationId);
        clock = timeProvider ?? TimeProvider.System;
        if (publish is not null) publisher.TrySetResult(publish);
        state = CreateState([], sharedIds, clock.GetUtcNow());
    }

    public void Bind(SdkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        publisher.TrySetResult(client.PublishAsync);
    }

    /// <summary>Observation only; Host drives timers, organization and expiry without periodic SDK publications.</summary>
    public ApplicationState Tick() { lock (gate) return state; }

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            epoch = checked(epoch + 1);
            permissionRevision = publicationOwner = 0;
            allowed = confirmed = false;
            state = CreateState([], sharedIds, clock.GetUtcNow());
            return Task.FromResult(new ApplicationSnapshot(declaration, state));
        }
    }

    public async Task OnDisplayPermissionsChangedAsync(DisplayPermissionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Revision <= 0 || snapshot.Entries is null || snapshot.Entries.Count != 3 ||
            snapshot.Entries.Any(entry => entry is null || entry.FeatureGroupId != "main" || entry.ComponentId is not ("islands" or "together" or "separate")) ||
            snapshot.Entries.Select(entry => entry.ComponentId).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("Invalid organization permission snapshot.", nameof(snapshot));
        long capturedEpoch;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (epoch == 0 || snapshot.Revision <= permissionRevision) return;
            permissionRevision = snapshot.Revision;
            bool nextAllowed = snapshot.Entries.Single(entry => entry.ComponentId == "islands").Allowed;
            bool changed = nextAllowed != allowed;
            allowed = nextAllowed;
            if (!allowed || !changed && confirmed || publicationOwner != 0) return;
            capturedEpoch = epoch;
            publicationOwner = capturedEpoch;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var publish = await publisher.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            List<Business> candidate;
            IReadOnlyList<string> candidateShared;
            ApplicationState next;
            Task<ProtocolResult> receipt;
            lock (gate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (capturedEpoch != epoch || !allowed) return;
                var now = clock.GetUtcNow();
                candidate = initialized ? businesses.ToList() : CreateInitialBusinesses(now);
                candidateShared = initialized ? sharedIds : Freeze(candidate.Take(2).Select(value => value.Activity.ActivityId).ToArray());
                revision = checked(revision + 1);
                next = CreateState(candidate, candidateShared, now);
                // Calling the SDK here captures this connection before GetSnapshot can start a new epoch.
                receipt = publish(next, deadline.Token);
            }
            var result = await receipt.WaitAsync(deadline.Token).ConfigureAwait(false);
            lock (gate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (capturedEpoch != epoch || !result.Accepted || result.ActivityRejections is { Count: > 0 }) return;
                // Only a successful publication admits new activities into the sample's business state.
                businesses = candidate;
                sharedIds = candidateShared;
                state = next;
                initialized = confirmed = true;
            }
        }
        finally { lock (gate) if (publicationOwner == capturedEpoch) publicationOwner = 0; }
    }

    public Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = invocation.Slot;
            bool validEntry = slot is { EntryKind: ActionEntryKind.Component, EntryId: "controls" } && Commands.Contains(slot.ActionSlotId, StringComparer.Ordinal) ||
                slot is { EntryKind: ActionEntryKind.Component, EntryId: "islands" or "left" or "right" or "together" or "separate", ActionSlotId: "update" } or
                { EntryKind: ActionEntryKind.TaskbarFlyout, EntryId: "details", ActionSlotId: "update" };
            if (slot is null || slot.ApplicationId != declaration.ApplicationId || slot.FeatureGroupId != "main" || !validEntry ||
                invocation.Parameter is not { Kind: ActionParameterKind.None, Boolean: null, Number: null, Text: null } ||
                string.IsNullOrWhiteSpace(invocation.RequestId) || invocation.RequestId.Length > 256 || invocation.Sequence <= 0)
                return Complete(ProtocolResult.Reject("ActionNotAvailable", "组织演示动作或参数无效。"));
            bool creates = slot.ActionSlotId is "add" or "reset";
            if (creates && !allowed) return Complete(ProtocolResult.Reject("DisplayNotAllowed", "显示已关闭，不能创建新活动。"));
            if (!confirmed) return Complete(ProtocolResult.Reject("ActivityNotConfirmed", "本会话尚未确认活动。"));
            if (publicationOwner != 0) return Complete(ProtocolResult.Reject("Busy", "活动正在等待接收确认。"));
            if (revision == long.MaxValue || creates && nextActivity > long.MaxValue - 3)
                return Complete(ProtocolResult.Reject("DemoSequenceExhausted", "演示状态序号已耗尽。"));
            var now = clock.GetUtcNow();
            var active = businesses.Where(value => !value.Activity.Ended && value.Activity.ExpiresAt > now).ToList();
            if (slot.ActionSlotId == "add" && active.Count >= MaximumActivities)
                return Complete(ProtocolResult.Reject("DemoActivityLimit", "演示最多保留八个活动。"));
            if (!creates && active.Count == 0)
                return Complete(slot.ActionSlotId == "end" ? ProtocolResult.Success("AlreadyEnded") : ProtocolResult.Reject("NoActiveActivity", "没有仍有效的活动。"));
            switch (slot.ActionSlotId)
            {
                case "add": active.Add(CreateBusiness(now, 0)); businesses = active; break;
                case "reset": businesses = CreateInitialBusinesses(now); sharedIds = Freeze(businesses.Take(2).Select(value => value.Activity.ActivityId).ToArray()); shared = true; updatePhase = 0; break;
                case "end":
                    var last = active[^1];
                    businesses = active.Select(value => ReferenceEquals(value, last) ? value with { Activity = value.Activity with { Ended = true } } : value).ToList();
                    break;
                case "update": updatePhase = (updatePhase + 1) % 3; break;
                case "shared": shared = !shared; break;
            }
            revision++;
            state = CreateState(businesses, sharedIds, now);
            return Complete(ProtocolResult.Success("ActionSucceeded"), state);
        }

        Task<ActionCompletion> Complete(ProtocolResult result, ApplicationState? next = null) =>
            Task.FromResult(new ActionCompletion(invocation.RequestId, invocation.Sequence, result, next));
    }

    private List<Business> CreateInitialBusinesses(DateTimeOffset now) =>
        [CreateBusiness(now, 30), CreateBusiness(now, 20), CreateBusiness(now, 10)];

    private Business CreateBusiness(DateTimeOffset now, int order)
    {
        string id = "a" + nextActivity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        nextActivity = checked(nextActivity + 1);
        return new(new(id, now.AddMinutes(5), Order: order), new(TimerDirection.CountUp, now, 12000));
    }

    private ApplicationState CreateState(IReadOnlyList<Business> current, IReadOnlyList<string> links, DateTimeOffset now)
    {
        var activities = current.Where(value => value.Activity.Ended || value.Activity.ExpiresAt > now).ToArray();
        var active = activities.Where(value => !value.Activity.Ended).ToArray();
        var items = new List<DynamicItemState>();
        foreach (var business in active)
        {
            string id = business.Activity.ActivityId;
            items.Add(new(id + "-timer", "timer", Freeze(id), new(Timer: business.Timer)));
            items.Add(new(id + "-status", "status", Freeze(id), new(Status: new(id + (updatePhase switch
            { 1 => " 正在处理", 2 => " 等待确认", _ => " 就绪" }), updatePhase == 2 ? StatusMarker.Attention : StatusMarker.Normal))));
        }
        if (shared && links.Count == 2 && links.All(id => active.Any(value => value.Activity.ActivityId == id)))
            items.Add(new("shared", "shared", Freeze(links.ToArray()), new(Status: new("共同任务"))));
        var empty = new DynamicContentState(Array.Empty<ActivityState>(), Array.Empty<DynamicItemState>());
        return new(revision, Freeze(
            new ComponentReading("main", "left", "左侧邻居"), new("main", "islands", "活动组织"),
            new("main", "right", "右侧邻居"), new("main", "controls", "活动操作"),
            new("main", "together", "固定合并"), new("main", "separate", "固定拆分")),
            Freeze(new DynamicEntryState("main", "islands", new(Freeze(activities.Select(value => value.Activity).ToArray()), items.AsReadOnly())),
                new("main", "together", empty), new("main", "separate", empty)),
            TemplateEntries: Freeze(new TemplateEntryState(new("main", TemplateEntryKind.Component, "controls"), Array.Empty<TemplateFieldValue>())));
    }

    private static ApplicationDeclaration CreateDeclaration(string applicationId)
    {
        var structures = Freeze(
            new ItemStructureDeclaration("timer", true, new(PresetTemplate.Timer, ContentFields.Timer, new(WidthTier.Small)),
                new(PresetTemplate.Timer, ContentFields.Timer, new(WidthTier.Large)), PrimaryActivation: new(ItemActivationKind.ToggleSelf)),
            new ItemStructureDeclaration("status", true, new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small)),
                new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Large)), PrimaryActivation: new(ItemActivationKind.ToggleSelf)),
            new ItemStructureDeclaration("shared", false, new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small))));
        var controls = new EntryTemplateDeclaration("controls", Freeze(new HostTemplate("controls",
            new("organization-controls", TemplateNodeKind.Horizontal, Children: Freeze(
                Button("add", "新增"), Button("end", "结束"), Button("update", "更新"), Button("reset", "重新开始"), Button("shared", "共享"))))),
            Array.Empty<TemplateFieldDeclaration>());
        return new(applicationId, Freeze(new FeatureGroupDeclaration("main", Freeze(
            new ComponentDeclaration("left", Slots("update")),
            new("islands", Slots("update"), new(DynamicContentKind.LiveIsland, structures, DynamicGrouping.UserChoice)),
            new("right", Slots("update")),
            new("controls", Freeze(Commands.Select(value => new ActionSlotDeclaration(value)).ToArray()), Template: controls),
            new("together", Slots("update"), new(DynamicContentKind.LiveIsland, structures, DynamicGrouping.Together)),
            new("separate", Slots("update"), new(DynamicContentKind.LiveIsland, structures, DynamicGrouping.Separate))),
            Freeze(new TaskbarFlyoutDeclaration("details", Slots("update"))))));
    }

    private static TemplateNode Button(string id, string text) => new(id, TemplateNodeKind.Button,
        Value: new(Literal: new(TemplateValueKind.Text, Text: text)), AccessibleName: text, Action: new(TemplateActionKind.Business, id));
    private static IReadOnlyList<ActionSlotDeclaration> Slots(string id) => Freeze(new ActionSlotDeclaration(id));
    private static IReadOnlyList<T> Freeze<T>(params T[] values) => Array.AsReadOnly(values);
    private sealed record Business(ActivityState Activity, TimerBasis Timer);
}

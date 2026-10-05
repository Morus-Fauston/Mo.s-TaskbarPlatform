using Mtp.Contracts;
using Mtp.Sdk;

internal sealed class PresetDemoProvider : IDeclarationProvider, IDisplayPermissionObserver, IActionHandler
{
    private static readonly string[] Commands = ["mode", "forward", "backward", "semantics", "status", "end"];
    private const string LongStatus = "多个任务正在后台协作，完整状态可以悬停查看；这段文字只在固定区域内滚动，不改变项宽度，也不会发送业务刷新消息。";
    private readonly object gate = new();
    private readonly ApplicationDeclaration declaration;
    private readonly TimeProvider clock;
    private readonly TaskCompletionSource<Func<ApplicationState, CancellationToken, Task<ProtocolResult>>> publisher = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApplicationState state;
    private ActivityState? activity;
    private TimerBasis? timer;
    private long revision;
    private long sessionEpoch;
    private long permissionRevision;
    private bool allowed;
    private bool sessionReconfirmed;
    private bool indeterminate;
    private bool semanticsSwapped;
    private double progressValue = 25;
    private int statusPhase;

    public PresetDemoProvider(string applicationId, TimeProvider? timeProvider = null,
        Func<ApplicationState, CancellationToken, Task<ProtocolResult>>? publish = null)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 || applicationId != applicationId.Trim())
            throw new ArgumentException("Invalid preset demo application identity.", nameof(applicationId));
        declaration = CreateDeclaration(applicationId);
        clock = timeProvider ?? TimeProvider.System;
        if (publish is not null) publisher.TrySetResult(publish);
        state = CreateState(false, clock.GetUtcNow());
    }

    public void Bind(SdkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        publisher.TrySetResult(client.PublishAsync);
    }

    /// <summary>Program must not periodically publish this observation: all animation is local to Host.</summary>
    public ApplicationState Tick() { lock (gate) return state; }

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sessionEpoch = checked(sessionEpoch + 1);
            permissionRevision = 0;
            allowed = sessionReconfirmed = false;
            state = CreateState(false, clock.GetUtcNow());
            return Task.FromResult(new ApplicationSnapshot(declaration, state));
        }
    }

    public async Task OnDisplayPermissionsChangedAsync(DisplayPermissionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Revision <= 0 || snapshot.Entries is null || snapshot.Entries.Count > DisplayPermissionLimits.MaximumEntriesPerApplication)
            throw new ArgumentException("Invalid preset permission snapshot.", nameof(snapshot));
        long epoch;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Revision <= permissionRevision) return;
            permissionRevision = snapshot.Revision;
            bool nextAllowed = snapshot.Entries.Any(entry => entry is { FeatureGroupId: "main", ComponentId: "presets", Allowed: true });
            bool changed = allowed != nextAllowed;
            allowed = nextAllowed;
            if (!allowed || !changed && sessionReconfirmed) return;
            epoch = sessionEpoch;
        }
        // Connect may not have returned to Program. The SDK connection token bounds this wait and any publication.
        var publish = await publisher.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        ApplicationState next;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (epoch != sessionEpoch || snapshot.Revision != permissionRevision || !allowed) return;
            DateTimeOffset now = clock.GetUtcNow();
            if (activity is null)
            {
                activity = new("demo", now.AddMinutes(5));
                timer = new(TimerDirection.CountUp, now, 12000);
            }
            // Reconnection confirms still-valid business; it never restarts or renews ended/expired activity.
            revision = checked(revision + 1);
            sessionReconfirmed = true;
            next = state = CreateState(true, now);
        }
        await publish(next, cancellationToken).ConfigureAwait(false);
    }

    public Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = invocation.Slot;
            bool validEntry = slot is { EntryKind: ActionEntryKind.Component, EntryId: "controls" } && Commands.Contains(slot.ActionSlotId, StringComparer.Ordinal) ||
                slot is { EntryKind: ActionEntryKind.Component, EntryId: "presets", ActionSlotId: "end" } or
                { EntryKind: ActionEntryKind.TaskbarFlyout, EntryId: "details", ActionSlotId: "end" };
            if (slot is null || slot.ApplicationId != declaration.ApplicationId || slot.FeatureGroupId != "main" || !validEntry ||
                invocation.Parameter is not { Kind: ActionParameterKind.None, Boolean: null, Number: null, Text: null } ||
                string.IsNullOrWhiteSpace(invocation.RequestId) || invocation.RequestId.Length > 256 || invocation.Sequence <= 0)
                return Complete(invocation, ProtocolResult.Reject("ActionNotAvailable", "预设演示动作或参数无效。"));
            if (!sessionReconfirmed || activity is null)
                return Complete(invocation, ProtocolResult.Reject("ActivityNotConfirmed", "本会话尚未确认演示活动。"));
            if (activity.Ended)
                return Complete(invocation, slot.ActionSlotId == "end" ? ProtocolResult.Success("AlreadyEnded") : ProtocolResult.Reject("ActivityEnded", "演示活动已结束。"));
            DateTimeOffset now = clock.GetUtcNow();
            if (activity.ExpiresAt <= now && slot.ActionSlotId != "end")
                return Complete(invocation, ProtocolResult.Reject("ActivityExpired", "演示活动已到期。"));
            if (revision == long.MaxValue)
                return Complete(invocation, ProtocolResult.Reject("DemoSequenceExhausted", "演示状态序号已耗尽。"));
            switch (slot.ActionSlotId)
            {
                case "mode": indeterminate = !indeterminate; break;
                case "semantics": semanticsSwapped = !semanticsSwapped; break;
                case "status": statusPhase = (statusPhase + 1) % 3; break;
                case "end": activity = activity with { Ended = true }; break;
                case "forward":
                case "backward":
                    double value = slot.ActionSlotId == "forward" ? 75 : 25;
                    if (!indeterminate && progressValue == value) return Complete(invocation, ProtocolResult.Success("Unchanged"));
                    progressValue = value; indeterminate = false; break;
                default: return Complete(invocation, ProtocolResult.Reject("ActionNotAvailable", "演示动作尚不可用。"));
            }
            revision++;
            state = CreateState(true, now);
            return Complete(invocation, ProtocolResult.Success("ActionSucceeded"), state);
        }
    }

    private static Task<ActionCompletion> Complete(ActionInvocation invocation, ProtocolResult result, ApplicationState? next = null) =>
        Task.FromResult(new ActionCompletion(invocation.RequestId, invocation.Sequence, result, next));

    private ApplicationState CreateState(bool confirmed, DateTimeOffset now)
    {
        bool includeActivity = confirmed && activity is not null && (activity.Ended || activity.ExpiresAt > now);
        var items = new List<DynamicItemState>();
        if (includeActivity && !activity!.Ended)
        {
            var progress = indeterminate ? new ProgressReading(ProgressMode.Indeterminate) : new(ProgressMode.Determinate, progressValue, 100);
            var status = statusPhase switch
            {
                1 => new StatusReading("需要注意：" + LongStatus, StatusMarker.Attention),
                2 => new StatusReading("处理失败：" + LongStatus, StatusMarker.Error),
                _ => new StatusReading("就绪", StatusMarker.Normal)
            };
            Add("counter-text", new(Counter: Counter("counter-text")));
            Add("counter-ring", new(Progress: progress, Counter: Counter("counter-ring")));
            Add("counter-bar", new(Progress: progress, Counter: Counter("counter-bar")));
            Add("progress", new(Progress: progress));
            Add("status", new(Status: status));
            Add("composite", new(Timer: timer, Progress: progress, Counter: Counter("composite"), Status: status));
        }
        return new(revision, Array.AsReadOnly(new ComponentReading[]
        {
            new("main", "presets", "预设模板演示"), new("main", "controls", "演示操作")
        }), Array.AsReadOnly(new DynamicEntryState[]
        {
            new("main", "presets", new(includeActivity ? Array.AsReadOnly(new[] { activity! }) : Array.Empty<ActivityState>(), items.AsReadOnly()))
        }), TemplateEntries: Array.AsReadOnly(new TemplateEntryState[]
        {
            new(new("main", TemplateEntryKind.Component, "controls"), Array.Empty<TemplateFieldValue>())
        }));

        void Add(string id, DynamicItemFields fields) => items.Add(new(id, id, Array.AsReadOnly(new[] { "demo" }), fields));
    }

    private CounterReading Counter(string id) => new((id == "counter-ring") != semanticsSwapped ? CounterSemantics.CurrentIndex : CounterSemantics.CompletedCount, 4, 10);

    private static ApplicationDeclaration CreateDeclaration(string applicationId)
    {
        ItemStructureDeclaration Item(string id, PresetTemplate template, ContentFields fields, PresetVariant variant,
            ContentWidth width, TextOverflow overflow = TextOverflow.Ellipsis) => new(id, false, new(template, fields, width, variant), Overflow: overflow);
        var structures = Array.AsReadOnly(new[]
        {
            Item("counter-text", PresetTemplate.Counter, ContentFields.Counter, PresetVariant.Text, new(WidthTier.Small)),
            Item("counter-ring", PresetTemplate.Counter, ContentFields.Counter | ContentFields.Progress, PresetVariant.Ring, new(WidthTier.Medium)),
            Item("counter-bar", PresetTemplate.Counter, ContentFields.Counter | ContentFields.Progress, PresetVariant.Bar, new(WidthTier.Medium)),
            Item("progress", PresetTemplate.Progress, ContentFields.Progress, PresetVariant.Bar, new(WidthTier.Medium)),
            Item("status", PresetTemplate.Status, ContentFields.Status, PresetVariant.Text, new(WidthTier.Medium), TextOverflow.Scroll),
            Item("composite", PresetTemplate.Composite, ContentFields.Counter | ContentFields.Timer | ContentFields.Status | ContentFields.Progress,
                PresetVariant.Ring, new(Slots: 4))
        });
        return new(applicationId, Array.AsReadOnly(new FeatureGroupDeclaration[]
        {
            new("main", Array.AsReadOnly(new ComponentDeclaration[]
            {
                new("presets", Array.AsReadOnly(new ActionSlotDeclaration[] { new("end") }), new(DynamicContentKind.LiveIsland, structures)),
                new("controls", Array.AsReadOnly(Commands.Select(command => new ActionSlotDeclaration(command)).ToArray()), Template: ControlsTemplate())
            }), Array.AsReadOnly(new TaskbarFlyoutDeclaration[]
            {
                new("details", Array.AsReadOnly(new ActionSlotDeclaration[] { new("end") }))
            }))
        }));
    }

    private static EntryTemplateDeclaration ControlsTemplate() => new("main", Array.AsReadOnly(new HostTemplate[]
    {
        new("main", new("preset-controls", TemplateNodeKind.Horizontal, Children: Array.AsReadOnly(new[]
        {
            Button("mode", "模式"), Button("forward", "前进"), Button("backward", "后退"),
            Button("semantics", "计数"), Button("status", "状态"), Button("end", "结束")
        })))
    }), Array.Empty<TemplateFieldDeclaration>());

    private static TemplateNode Button(string id, string text) => new(id, TemplateNodeKind.Button,
        Value: new(Literal: new(TemplateValueKind.Text, Text: text)), AccessibleName: text, Action: new(TemplateActionKind.Business, id));
}

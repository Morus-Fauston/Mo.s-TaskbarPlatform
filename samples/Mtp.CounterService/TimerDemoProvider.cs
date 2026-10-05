using System.Globalization;
using Mtp.Contracts;
using Mtp.Sdk;

internal sealed class TimerDemoProvider : IDeclarationProvider, IDisplayPermissionObserver, IActionHandler
{
    private const int MaximumRepeatedItems = 8;
    private readonly object gate = new();
    private readonly ApplicationDeclaration declaration;
    private readonly TimeProvider clock;
    private readonly TaskCompletionSource<Func<ApplicationState, CancellationToken, Task<ProtocolResult>>> publisher = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<TimerEntry> entries = [];
    private ApplicationState state;
    private long revision;
    private long sessionEpoch;
    private long permissionRevision;
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private long nextItemId = 1;
    private bool allowed;
    private bool initialized;
    private bool sessionReconfirmed;

    public TimerDemoProvider(string applicationId, TimeProvider? timeProvider = null,
        Func<ApplicationState, CancellationToken, Task<ProtocolResult>>? publish = null)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 || applicationId != applicationId.Trim())
            throw new ArgumentException("Invalid timer demo application identity.", nameof(applicationId));
        declaration = CreateDeclaration(applicationId);
        clock = timeProvider ?? TimeProvider.System;
        if (publish is not null) publisher.TrySetResult(publish);
        state = CreateState(false);
    }

    public void Bind(SdkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        publisher.TrySetResult(client.PublishAsync);
    }

    public async Task OnDisplayPermissionsChangedAsync(DisplayPermissionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        long epoch;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Revision <= permissionRevision) return;
            permissionRevision = snapshot.Revision;
            bool nextAllowed = snapshot.Entries.Any(entry => entry is { FeatureGroupId: "main", ComponentId: "timers", Allowed: true });
            bool changed = allowed != nextAllowed;
            allowed = nextAllowed;
            if (!allowed || !changed && sessionReconfirmed) return;
            epoch = sessionEpoch;
        }
        // The SDK may deliver permission before Connect has returned to Program.
        // Its connection lifetime cancels this wait; no unbounded task or retry loop is created here.
        var publish = await publisher.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        ApplicationState next;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (epoch != sessionEpoch || snapshot.Revision != permissionRevision || !allowed) return;
            DateTimeOffset now = clock.GetUtcNow();
            if (!initialized)
            {
                entries.Add(CreateEntry("up", "up", now));
                entries.Add(CreateEntry("down", "down", now));
                initialized = true;
            }
            else entries.RemoveAll(entry => entry.Activity.ExpiresAt <= now);
            revision = checked(revision + 1);
            sessionReconfirmed = true;
            next = state = CreateState(true);
        }
        await publish(next, cancellationToken).ConfigureAwait(false);
    }

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sessionEpoch = checked(sessionEpoch + 1);
            permissionRevision = 0;
            allowed = sessionReconfirmed = false;
            // Existing business data stays in memory, but a new connection must receive its own Host permission.
            state = CreateState(false);
            return Task.FromResult(new ApplicationSnapshot(declaration, state));
        }
    }

    /// <summary>Observation never advances a timer basis, revision or activity expiry and does not publish.</summary>
    public ApplicationState Tick() { lock (gate) return state; }

    public Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = invocation.Slot;
            bool panelReset = slot is { EntryKind: ActionEntryKind.TaskbarFlyout, EntryId: "details", ActionSlotId: "reset" };
            if (slot is null || slot.ApplicationId != declaration.ApplicationId || slot.FeatureGroupId != "main" ||
                slot.EntryKind != ActionEntryKind.Component && !panelReset ||
                invocation.Parameter is not { Kind: ActionParameterKind.None, Boolean: null, Number: null, Text: null } ||
                string.IsNullOrWhiteSpace(invocation.RequestId) || invocation.RequestId.Length > 256 || invocation.Sequence <= 0)
                return Reject(invocation, "ActionNotAvailable", "计时演示动作或参数无效。");
            if (!sessionReconfirmed) return Reject(invocation, "ActivityNotConfirmed", "本会话尚未重新确认计时活动。");
            if (revision == long.MaxValue) return Reject(invocation, "DemoSequenceExhausted", "演示状态序号已耗尽。");
            DateTimeOffset now = clock.GetUtcNow();
            var valid = entries.Where(entry => entry.Activity.ExpiresAt > now).ToList();
            if (slot.EntryId == "timers" && slot.ActionSlotId.StartsWith("toggle-", StringComparison.Ordinal))
            {
                string structure = slot.ActionSlotId[7..];
                int index = valid.FindIndex(entry => entry.StructureId == structure);
                if (index < 0) return Reject(invocation, "ActivityNotAvailable", "对应计时活动不存在或已到期。");
                var entry = valid[index];
                valid[index] = entry with
                {
                    Basis = entry.Basis with { ReferenceUtc = now, ValueMillisecondsAtReference = CurrentValue(entry), IsPaused = !entry.Basis.IsPaused },
                    ReferenceTimestamp = clock.GetTimestamp()
                };
            }
            else if (slot is { EntryId: "controls", ActionSlotId: "add" })
            {
                if (!allowed) return Reject(invocation, "ActivityCreationNotAllowed", "显示许可关闭，不能创建新计时。");
                var used = valid.Select(entry => entry.StructureId).ToHashSet(StringComparer.Ordinal);
                string? structure = Enumerable.Range(1, MaximumRepeatedItems).Select(index => "repeat-" + index.ToString(CultureInfo.InvariantCulture))
                    .FirstOrDefault(id => !used.Contains(id));
                if (structure is null) return Reject(invocation, "DemoItemLimit", "演示最多八个重复计时及两个固定计时。");
                if (nextItemId == long.MaxValue) return Reject(invocation, "DemoSequenceExhausted", "演示项序号已耗尽。");
                string id = "timer-" + instanceId + "-" + nextItemId.ToString(CultureInfo.InvariantCulture);
                valid.Add(CreateEntry(id, structure, now));
                nextItemId++;
            }
            else if (slot is { EntryId: "controls", ActionSlotId: "remove" })
            {
                int index = valid.FindLastIndex(entry => entry.StructureId.StartsWith("repeat-", StringComparison.Ordinal));
                if (index < 0) return Reject(invocation, "DemoItemsEmpty", "当前没有可移除的重复计时。");
                valid.RemoveAt(index);
            }
            else if (slot is { EntryId: "controls", ActionSlotId: "reset" } || panelReset)
            {
                if (!allowed) return Reject(invocation, "ActivityCreationNotAllowed", "显示许可关闭，不能重启计时活动。");
                valid = valid.Select(entry => CreateEntry(entry.ItemId, entry.StructureId, now)).ToList();
                if (valid.All(entry => entry.StructureId != "up")) valid.Insert(0, CreateEntry("up", "up", now));
                if (valid.All(entry => entry.StructureId != "down")) valid.Insert(1, CreateEntry("down", "down", now));
            }
            else return Reject(invocation, "ActionNotAvailable", "未知计时演示动作。");
            entries.Clear();
            entries.AddRange(valid);
            revision++;
            state = CreateState(true);
            return Task.FromResult(new ActionCompletion(invocation.RequestId, invocation.Sequence, ProtocolResult.Success("ActionSucceeded"), state));
        }
    }

    private double CurrentValue(TimerEntry entry)
    {
        if (entry.Basis.IsPaused) return entry.Basis.ValueMillisecondsAtReference;
        double elapsed = Math.Max(0, clock.GetElapsedTime(entry.ReferenceTimestamp, clock.GetTimestamp()).TotalMilliseconds);
        double value = entry.Basis.ValueMillisecondsAtReference + (entry.Basis.Direction == TimerDirection.CountUp ? elapsed : -elapsed);
        double minimum = entry.Basis.ShowOvertime && entry.Basis.Direction == TimerDirection.CountDown ? -TimeSpan.MaxValue.TotalMilliseconds : 0;
        return Math.Clamp(value, minimum, TimeSpan.MaxValue.TotalMilliseconds);
    }

    private static Task<ActionCompletion> Reject(ActionInvocation invocation, string code, string message) =>
        Task.FromResult(new ActionCompletion(invocation.RequestId, invocation.Sequence, ProtocolResult.Reject(code, message)));

    private TimerEntry CreateEntry(string itemId, string structureId, DateTimeOffset now)
    {
        bool up = structureId == "up";
        double duration = up ? 60000 : structureId == "down" ? 5000 : 8000;
        return new(itemId, structureId, new("activity-" + itemId, now.AddMinutes(5)),
            new(up ? TimerDirection.CountUp : TimerDirection.CountDown, now, up ? 0 : duration,
                ShowOvertime: !up && structureId != "down", ProgressDurationMilliseconds: duration), clock.GetTimestamp());
    }

    private ApplicationState CreateState(bool includeActivities) => new(revision,
        Array.AsReadOnly(new ComponentReading[] { new("main", "timers", "计时实况岛"), new("main", "controls", "计时控制") }),
        Array.AsReadOnly(new DynamicEntryState[] { new("main", "timers", new(
            includeActivities ? Array.AsReadOnly(entries.Select(entry => entry.Activity).ToArray()) : Array.Empty<ActivityState>(),
            includeActivities ? Array.AsReadOnly(entries.Select(entry => new DynamicItemState(entry.ItemId, entry.StructureId,
                Array.AsReadOnly(new[] { entry.Activity.ActivityId }), new(Timer: entry.Basis))).ToArray()) : Array.Empty<DynamicItemState>())) }),
        TemplateEntries: Array.AsReadOnly(new TemplateEntryState[] { new(new("main", TemplateEntryKind.Component, "controls"), Array.Empty<TemplateFieldValue>()) }));

    private sealed record TimerEntry(string ItemId, string StructureId, ActivityState Activity, TimerBasis Basis, long ReferenceTimestamp);

    private static ApplicationDeclaration CreateDeclaration(string applicationId)
    {
        var normal = new ItemPresentation(PresetTemplate.Timer, ContentFields.Timer, new(WidthTier.Medium));
        var expanded = normal with { Width = new(Slots: 6) };
        var structures = new List<ItemStructureDeclaration>();
        var actions = new List<ActionSlotDeclaration>();
        foreach (string id in new[] { "up", "down" }.Concat(Enumerable.Range(1, MaximumRepeatedItems).Select(index => "repeat-" + index.ToString(CultureInfo.InvariantCulture))))
        {
            string action = "toggle-" + id;
            actions.Add(new(action));
            structures.Add(new(id, id.StartsWith("repeat-", StringComparison.Ordinal), normal, expanded,
                ExpandTargetStructureIds: Array.AsReadOnly(new[] { "up", "down" }), Animation: SemanticAnimation.EnterExit,
                PrimaryActivation: new(ItemActivationKind.ToggleSelf),
                ControlActivations: Array.AsReadOnly(new ItemControlActivation[]
                {
                    new(ItemControlKind.PrimaryButton, new(ItemActivationKind.BusinessAction, action)),
                    new(ItemControlKind.SecondaryButton, new(ItemActivationKind.ToggleDeclaredTargets))
                })));
        }
        return new(applicationId, Array.AsReadOnly(new FeatureGroupDeclaration[]
        {
            new("main", Array.AsReadOnly(new ComponentDeclaration[]
            {
                new("timers", actions.AsReadOnly(), new(DynamicContentKind.LiveIsland, structures.AsReadOnly())),
                new("controls", Array.AsReadOnly(new ActionSlotDeclaration[] { new("add"), new("remove"), new("reset") }), Template: ControlsTemplate())
            }), Array.AsReadOnly(new TaskbarFlyoutDeclaration[] { new("details", Array.AsReadOnly(new ActionSlotDeclaration[] { new("reset") })) }))
        }));
    }

    private static EntryTemplateDeclaration ControlsTemplate() => new("main", Array.AsReadOnly(new HostTemplate[]
    {
        new("main", new("timer-controls", TemplateNodeKind.Horizontal, Children: Array.AsReadOnly(new TemplateNode[]
        {
            Control("add", "增加计时"), Control("remove", "移除计时"), Control("reset", "重设计时")
        })))
    }), Array.Empty<TemplateFieldDeclaration>());

    private static TemplateNode Control(string id, string text) => new(id, TemplateNodeKind.Button,
        Value: new(Literal: new(TemplateValueKind.Text, Text: text)), AccessibleName: text,
        Action: new(TemplateActionKind.Business, id));
}

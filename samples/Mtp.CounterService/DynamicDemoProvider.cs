using System.Globalization;
using Mtp.Contracts;
using Mtp.Sdk;

/// <summary>A bounded ordinary-item fixture; readings and collection membership change independently.</summary>
internal sealed class DynamicDemoProvider(string applicationId) : IDeclarationProvider, IActionHandler
{
    private const int MaximumItems = 8;
    private readonly object gate = new();
    private readonly ApplicationDeclaration declaration = CreateDeclaration(applicationId);
    private readonly List<(string Id, string Structure)> items = [("a", "small"), ("b", "large")];
    private long count;
    private long revision;
    private long nextItemId = 3;

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ApplicationSnapshot(declaration, CreateState()));
        }
    }

    public ApplicationState Tick()
    {
        lock (gate)
        {
            revision = checked(revision + 1);
            if (count < long.MaxValue) count++;
            return CreateState();
        }
    }

    public Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = invocation.Slot;
            bool add = slot is { EntryKind: ActionEntryKind.Component, EntryId: "left", ActionSlotId: "add" };
            bool remove = slot is { EntryKind: ActionEntryKind.Component, EntryId: "right", ActionSlotId: "remove" };
            bool refresh = slot is { EntryKind: ActionEntryKind.Component, EntryId: "items", ActionSlotId: "refresh" } or
            { EntryKind: ActionEntryKind.TaskbarFlyout, EntryId: "details", ActionSlotId: "refresh" };
            if (slot?.ApplicationId != applicationId || slot.FeatureGroupId != "main" || !(add || remove || refresh) ||
                invocation.Parameter is not { Kind: ActionParameterKind.None, Boolean: null, Number: null, Text: null } ||
                string.IsNullOrWhiteSpace(invocation.RequestId) || invocation.RequestId.Length > 256 || invocation.Sequence <= 0)
                return Completed(invocation, ProtocolResult.Reject("ActionNotAvailable", "动态项演示动作或参数无效。"));
            if (add && items.Count == MaximumItems)
                return Completed(invocation, ProtocolResult.Reject("DemoItemLimit", "演示最多显示八项。"));
            if (remove && items.Count == 0)
                return Completed(invocation, ProtocolResult.Reject("DemoItemsEmpty", "当前没有可移除的项。"));
            if (revision == long.MaxValue || add && nextItemId == long.MaxValue)
                return Completed(invocation, ProtocolResult.Reject("DemoSequenceExhausted", "演示序号已耗尽。"));
            if (add)
            {
                items.Add(("item-" + nextItemId.ToString(CultureInfo.InvariantCulture), nextItemId % 2 == 0 ? "large" : "small"));
                nextItemId++;
            }
            else if (remove) items.RemoveAt(items.Count - 1);
            revision++;
            return Completed(invocation, ProtocolResult.Success("ActionSucceeded"), CreateState());
        }
    }

    private ApplicationState CreateState() => new(revision,
        Array.AsReadOnly(new ComponentReading[]
        {
            new("main", "left", "增加项"),
            new("main", "items", "动态项 · " + items.Count.ToString(CultureInfo.InvariantCulture), count),
            new("main", "right", "移除项")
        }),
        Array.AsReadOnly(new DynamicEntryState[]
        {
            new("main", "items", new(Array.Empty<ActivityState>(),
                Array.AsReadOnly(items.Select(item => new DynamicItemState(item.Id, item.Structure, Array.Empty<string>(),
                    new(Counter: new(CounterSemantics.CompletedCount, count)))).ToArray())))
        }));

    private static Task<ActionCompletion> Completed(ActionInvocation invocation, ProtocolResult result, ApplicationState? state = null) =>
        Task.FromResult(new ActionCompletion(invocation.RequestId, invocation.Sequence, result, state));

    private static ApplicationDeclaration CreateDeclaration(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id != id.Trim())
            throw new ArgumentException("Invalid dynamic demo application identity.", nameof(id));
        var medium = new ItemPresentation(PresetTemplate.Counter, ContentFields.Counter, new(WidthTier.Medium));
        var structures = Array.AsReadOnly(new ItemStructureDeclaration[]
        {
            new("small", true, medium with { Width = new(WidthTier.Small) }, medium,
                Animation: SemanticAnimation.EnterExit, PrimaryActivation: new(ItemActivationKind.ToggleSelf)),
            new("large", true, medium with { Width = new(WidthTier.Large) }, medium,
                Animation: SemanticAnimation.EnterExit, PrimaryActivation: new(ItemActivationKind.ToggleSelf))
        });
        return new(id, Array.AsReadOnly(new FeatureGroupDeclaration[]
        {
            new("main", Array.AsReadOnly(new ComponentDeclaration[]
            {
                new("left", Slots("add")),
                new("items", Slots("refresh"), new(DynamicContentKind.OrdinaryItems, structures)),
                new("right", Slots("remove"))
            }), Array.AsReadOnly(new TaskbarFlyoutDeclaration[] { new("details", Slots("refresh")) }))
        }));
    }

    private static IReadOnlyList<ActionSlotDeclaration> Slots(string id) => Array.AsReadOnly(new[] { new ActionSlotDeclaration(id) });
}

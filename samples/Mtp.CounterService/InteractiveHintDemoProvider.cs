using Mtp.Contracts;
using Mtp.Sdk;

/// <summary>Local demonstration values only; no operating-system setting is read or changed.</summary>
internal sealed class InteractiveHintDemoProvider : IDeclarationProvider, IDisplayPermissionObserver, IActionHandler
{
    private readonly object gate = new();
    private readonly ApplicationDeclaration declaration;
    private readonly TaskCompletionSource<Func<FlyoutRequest, CancellationToken, Task<ProtocolResult>>> requester = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApplicationState state = State(0, 20);
    private long epoch;
    private bool requestPending;

    public InteractiveHintDemoProvider(string applicationId, Func<FlyoutRequest, CancellationToken, Task<ProtocolResult>>? request = null)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 || applicationId != applicationId.Trim())
            throw new ArgumentException("Invalid interactive hint demo application identity.", nameof(applicationId));
        var controls = new HostTemplate("controls", new("controls-root", TemplateNodeKind.Horizontal, Children: Freeze(
            Button("request", "调节演示值", new(TemplateActionKind.Business, "request")),
            Button("requestGroup", "打开详情", new(TemplateActionKind.Business, "requestGroup")))));
        var details = new HostTemplate("details", new("details-root", TemplateNodeKind.Vertical, Children: Freeze(
            Text("title", "演示详情"), Button("confirm", "确认", new(TemplateActionKind.Business, "confirm")))));
        var expanded = new HostTemplate("expanded", new("expanded-root", TemplateNodeKind.Vertical, Children: Freeze(
            Text("expanded-title", "从可交互提示展开的关联面板"), Button("confirm", "确认", new(TemplateActionKind.Business, "confirm")),
            Button("back", "返回", new(TemplateActionKind.Back)))));
        var adjust = new HostTemplate("adjust", new("adjust-root", TemplateNodeKind.Vertical, Children: Freeze(
            new TemplateNode("slider", TemplateNodeKind.Slider, Value: new(StateFieldId: "level"), AccessibleName: "演示值",
                Action: new(TemplateActionKind.Business, "level"), Minimum: 0, Maximum: 100, Step: 5),
            new TemplateNode("buttons", TemplateNodeKind.Horizontal, Children: Freeze(
                Button("expand", "展开详情", new(TemplateActionKind.ExpandHint)),
                Button("fail", "演示失败", new(TemplateActionKind.Business, "fail")))))));
        declaration = new(applicationId, Freeze(new FeatureGroupDeclaration("main",
            Freeze(new ComponentDeclaration("controls", Freeze(new ActionSlotDeclaration("request"), new("requestGroup")),
                Template: new("controls", Freeze(controls), Array.Empty<TemplateFieldDeclaration>()))),
            Freeze(new TaskbarFlyoutDeclaration("details", Freeze(new ActionSlotDeclaration("confirm")),
                new("details", Freeze(details, expanded), Array.Empty<TemplateFieldDeclaration>(), Panels: Freeze(new TemplatePanelReference("expanded"))))),
            Hints: Freeze(new HintEntryDeclaration("adjust", FlyoutKind.InteractiveHint,
                new("adjust", Freeze(adjust), Freeze(new TemplateFieldDeclaration("level", TemplateValueKind.Number))),
                Freeze(new ActionSlotDeclaration("level", ActionParameterKind.Number), new("fail")), new("details", "expanded"))))));
        if (request is not null) requester.TrySetResult(request);
    }

    public void Bind(SdkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        requester.TrySetResult(client.RequestFlyoutAsync);
    }

    public ApplicationState Tick() { lock (gate) return state; }

    public Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            epoch = checked(epoch + 1);
            return Task.FromResult(new ApplicationSnapshot(declaration, state));
        }
    }

    public Task OnDisplayPermissionsChangedAsync(DisplayPermissionSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Revision <= 0 || snapshot.Entries is null || snapshot.Entries.Count != 0)
            throw new ArgumentException("Unexpected interactive hint demo permission entries.", nameof(snapshot));
        return Task.CompletedTask;
    }

    public async Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        long capturedEpoch;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = invocation.Slot;
            if (slot is null || slot.ApplicationId != declaration.ApplicationId || slot.FeatureGroupId != "main" ||
                string.IsNullOrWhiteSpace(invocation.RequestId) || invocation.RequestId.Length > 256 || invocation.Sequence <= 0)
                return Complete(ProtocolResult.Reject("ActionNotAvailable", "演示动作身份无效。"));
            bool level = slot is { EntryKind: ActionEntryKind.Hint, EntryId: "adjust", ActionSlotId: "level" };
            bool request = slot is { EntryKind: ActionEntryKind.Component, EntryId: "controls", ActionSlotId: "request" or "requestGroup" };
            bool fail = slot is { EntryKind: ActionEntryKind.Hint, EntryId: "adjust", ActionSlotId: "fail" };
            bool confirm = slot is { EntryKind: ActionEntryKind.TaskbarFlyout, EntryId: "details", ActionSlotId: "confirm" };
            bool validParameter = level
                ? invocation.Parameter is { Kind: ActionParameterKind.Number, Number: { } number, Boolean: null, Text: null } &&
                    double.IsFinite(number) && number is >= 0 and <= 100 && number % 5 == 0
                : invocation.Parameter is { Kind: ActionParameterKind.None, Number: null, Boolean: null, Text: null };
            if (!(level || request || fail || confirm) || !validParameter)
                return Complete(ProtocolResult.Reject("ActionNotAvailable", "演示动作槽或参数无效。"));
            if (epoch == 0) return Complete(ProtocolResult.Reject("SessionNotReady", "当前会话尚未提交声明。"));
            if (level)
            {
                if (invocation.Parameter.Number >= 90)
                    return Complete(ProtocolResult.Reject("DemoLevelRejected", "演示值必须低于90，已保留最后确认值。"));
                state = State(checked(state.Revision + 1), invocation.Parameter.Number!.Value);
                return Complete(ProtocolResult.Success("ActionSucceeded"), state);
            }
            if (fail) return Complete(ProtocolResult.Reject("DemoFailure", "演示操作失败，已保留最后确认值。"));
            if (confirm) return Complete(ProtocolResult.Success("ActionSucceeded"));
            if (requestPending) return Complete(ProtocolResult.Reject("Busy", "显示请求正在等待确认。"));
            requestPending = true;
            capturedEpoch = epoch;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(ActionLimits.WaitTimeoutSeconds));
            var request = await requester.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            Task<ProtocolResult> receipt;
            lock (gate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (epoch != capturedEpoch) return Complete(ProtocolResult.Reject("StaleSession", "旧会话显示请求已失效。"));
                bool hint = invocation.Slot.ActionSlotId == "request";
                receipt = request(new("sdk-assigned", 1, "main", hint ? "adjust" : "details",
                    hint ? FlyoutKind.InteractiveHint : FlyoutKind.TaskbarGroup, state.Revision), deadline.Token);
            }
            var result = await receipt.WaitAsync(deadline.Token).ConfigureAwait(false);
            lock (gate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                return Complete(epoch == capturedEpoch ? result : ProtocolResult.Reject("StaleSession", "旧会话显示请求已失效。"));
            }
        }
        finally { lock (gate) requestPending = false; }

        ActionCompletion Complete(ProtocolResult result, ApplicationState? confirmed = null) =>
            new(invocation.RequestId, invocation.Sequence, result, confirmed);
    }

    private static ApplicationState State(long revision, double level) => new(revision, Freeze(new ComponentReading("main", "controls", "交互提示演示")),
        TemplateEntries: Freeze(
            new TemplateEntryState(new("main", TemplateEntryKind.Component, "controls"), Array.Empty<TemplateFieldValue>()),
            new TemplateEntryState(new("main", TemplateEntryKind.TaskbarFlyout, "details"), Array.Empty<TemplateFieldValue>()),
            new TemplateEntryState(new("main", TemplateEntryKind.Hint, "adjust"), Freeze(new TemplateFieldValue("level", new(TemplateValueKind.Number, Number: level))))));
    private static TemplateNode Text(string id, string text) => new(id, TemplateNodeKind.Text, Value: new(Literal: new(TemplateValueKind.Text, Text: text)));
    private static TemplateNode Button(string id, string text, TemplateAction action) => new(id, TemplateNodeKind.Button,
        Value: new(Literal: new(TemplateValueKind.Text, Text: text)), AccessibleName: text, Action: action);
    private static IReadOnlyList<T> Freeze<T>(params T[] values) => Array.AsReadOnly(values);
}

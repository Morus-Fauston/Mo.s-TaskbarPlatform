using Mtp.Contracts;
using Mtp.Sdk;

internal sealed class FlyoutDemoProvider : IDeclarationProvider, IDisplayPermissionObserver, IActionHandler
{
    private readonly object gate = new();
    private readonly ApplicationDeclaration declaration;
    private readonly ApplicationState state;
    private readonly TaskCompletionSource<Func<FlyoutRequest, CancellationToken, Task<ProtocolResult>>> requester = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long epoch;
    private bool requestPending;

    public FlyoutDemoProvider(string applicationId, Func<FlyoutRequest, CancellationToken, Task<ProtocolResult>>? request = null)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 || applicationId != applicationId.Trim())
            throw new ArgumentException("Invalid flyout demo application identity.", nameof(applicationId));
        declaration = new(applicationId, Freeze(new FeatureGroupDeclaration("main", Freeze(
            new ComponentDeclaration("controls", Freeze(new ActionSlotDeclaration("request"), new("confirm"), new("hint"), new("fail")), Template: Templates(true)),
            new ComponentDeclaration("item", Freeze(new ActionSlotDeclaration("confirm")), new(DynamicContentKind.OrdinaryItems,
                Freeze(new ItemStructureDeclaration("entry", false,
                    new(PresetTemplate.Status, ContentFields.Status, new(Tier: WidthTier.Small)),
                    PrimaryActivation: new(ItemActivationKind.TaskbarFlyout, "details")))))),
            Freeze(new TaskbarFlyoutDeclaration("details", Freeze(new ActionSlotDeclaration("confirm"), new("fail")), Templates(false))),
            Hints: Freeze(new HintEntryDeclaration("notice", FlyoutKind.ShortHint, new("notice", Freeze(new HostTemplate("notice",
                Text("notice-text", "SDK 短提示：3 秒后自动关闭，点击可穿透。"))), Array.Empty<TemplateFieldDeclaration>()))))));
        state = new(0, Freeze(new ComponentReading("main", "controls", "浮窗操作"), new("main", "item", "浮窗入口")),
            Freeze(new DynamicEntryState("main", "item", new(Array.Empty<ActivityState>(), Freeze(
                new DynamicItemState("entry", "entry", Array.Empty<string>(), new(Status: new("查看详情"))))))),
            TemplateEntries: Freeze(
                new TemplateEntryState(new("main", TemplateEntryKind.Component, "controls"), Array.Empty<TemplateFieldValue>()),
                new TemplateEntryState(new("main", TemplateEntryKind.TaskbarFlyout, "details"), Array.Empty<TemplateFieldValue>()),
                new TemplateEntryState(new("main", TemplateEntryKind.Hint, "notice"), Array.Empty<TemplateFieldValue>())));
        if (request is not null) requester.TrySetResult(request);
    }

    public void Bind(SdkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        requester.TrySetResult(client.RequestFlyoutAsync);
    }

    /// <summary>No state changes or periodic publications: requests refer to the confirmed declaration revision.</summary>
    public ApplicationState Tick() => state;

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
        // This demo declares ordinary items, so no activity permission or callback publication is needed.
        if (snapshot.Revision <= 0 || snapshot.Entries is null || snapshot.Entries.Count != 0)
            throw new ArgumentException("Unexpected activity permission entries.", nameof(snapshot));
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
            bool validEntry = slot is { EntryKind: ActionEntryKind.Component, EntryId: "controls", ActionSlotId: "request" or "confirm" or "hint" or "fail" } or
                { EntryKind: ActionEntryKind.Component, EntryId: "item", ActionSlotId: "confirm" } or
                { EntryKind: ActionEntryKind.TaskbarFlyout, EntryId: "details", ActionSlotId: "confirm" or "fail" };
            if (slot is null || slot.ApplicationId != declaration.ApplicationId || slot.FeatureGroupId != "main" || !validEntry ||
                invocation.Parameter is not { Kind: ActionParameterKind.None, Boolean: null, Number: null, Text: null } ||
                string.IsNullOrWhiteSpace(invocation.RequestId) || invocation.RequestId.Length > 256 || invocation.Sequence <= 0)
                return Complete(ProtocolResult.Reject("ActionNotAvailable", "浮窗演示动作或参数无效。"));
            if (epoch == 0) return Complete(ProtocolResult.Reject("SessionNotReady", "当前会话尚未提交声明。"));
            if (slot.ActionSlotId == "confirm") return Complete(ProtocolResult.Success("ActionSucceeded"));
            if (slot.ActionSlotId == "fail") return Complete(ProtocolResult.Reject("DemoFailure", "演示操作未完成，已保留最后确认值。"));
            if (requestPending) return Complete(ProtocolResult.Reject("Busy", "浮窗请求正在等待接收确认。"));
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
                if (epoch != capturedEpoch) return Complete(ProtocolResult.Reject("StaleSession", "旧会话请求已失效。"));
                // SDK captures the current connection synchronously; its independent reader receives the
                // Host queue receipt while the action worker awaits this call. No state publication waits
                // for ActionCompleted, and no unowned worker or request queue is created here.
                bool hint = invocation.Slot.ActionSlotId == "hint";
                receipt = request(new("sdk-assigned", 1, "main", hint ? "notice" : "details", hint ? FlyoutKind.ShortHint : FlyoutKind.TaskbarGroup,
                    state.Revision, Position: hint ? FlyoutPosition.TopLeft : FlyoutPosition.Default), deadline.Token);
            }
            var result = await receipt.WaitAsync(deadline.Token).ConfigureAwait(false);
            lock (gate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                return Complete(epoch == capturedEpoch ? result : ProtocolResult.Reject("StaleSession", "旧会话请求已失效。"));
            }
        }
        finally { lock (gate) requestPending = false; }

        ActionCompletion Complete(ProtocolResult result) => new(invocation.RequestId, invocation.Sequence, result);
    }

    private static EntryTemplateDeclaration Templates(bool controls)
    {
        var templates = new List<HostTemplate>();
        if (controls) templates.Add(new("controls", new("controls-root", TemplateNodeKind.Horizontal, Children: Freeze(
            Button("open", "打开面板", new(TemplateActionKind.OpenPanel, "panel")),
            Button("request", "请求浮窗", new(TemplateActionKind.Business, "request")),
            Button("hint", "显示短提示", new(TemplateActionKind.Business, "hint"))))));
        templates.Add(new("panel", new("panel-root", TemplateNodeKind.Vertical, Children: Freeze(
            Text("title", "浮窗主面板"),
            Text("description", "查看层级详情，或在空间允许时并列显示补充信息。"),
            Button("child", "层级详情", new(TemplateActionKind.OpenPanel, "child")),
            Button("side", "并列信息", new(TemplateActionKind.OpenPanel, "side")), Confirm(),
            Button("fail", "演示操作失败", new(TemplateActionKind.Business, "fail")), Back()))));
        templates.Add(new("child", new("child-root", TemplateNodeKind.Vertical, Children: Freeze(
            Text("title", "层级详情"), Text("description", "此面板替换当前面板；返回后继续主面板操作。"), Confirm(), Back()))));
        templates.Add(new("side", new("side-root", TemplateNodeKind.Vertical, Children: Freeze(
            Text("title", "并列信息"), Text("description", "查看补充信息，也可返回继续主面板操作。"), Confirm(), Back()))));
        var panels = new List<TemplatePanelReference>();
        if (controls) panels.Add(new("panel"));
        panels.Add(new("child", PanelPresentation.Hierarchical));
        panels.Add(new("side", PanelPresentation.Parallel));
        return new(controls ? "controls" : "panel", templates.AsReadOnly(), Array.Empty<TemplateFieldDeclaration>(), Panels: panels.AsReadOnly());
    }

    private static TemplateNode Text(string id, string text) => new(id, TemplateNodeKind.Text, Value: new(Literal: new(TemplateValueKind.Text, Text: text)));
    private static TemplateNode Button(string id, string text, TemplateAction action) => new(id, TemplateNodeKind.Button,
        Value: new(Literal: new(TemplateValueKind.Text, Text: text)), AccessibleName: text, Action: action);
    private static TemplateNode Confirm() => Button("confirm", "确认", new(TemplateActionKind.Business, "confirm"));
    private static TemplateNode Back() => Button("back", "返回", new(TemplateActionKind.Back));
    private static IReadOnlyList<T> Freeze<T>(params T[] values) => Array.AsReadOnly(values);
}

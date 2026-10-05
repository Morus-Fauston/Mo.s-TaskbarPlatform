using Mtp.Contracts;
using Mtp.Sdk;

/// <summary>Bounded local event demonstration; no operating-system business is performed.</summary>
internal sealed class EventDemoProvider : IDeclarationProvider, IDisplayPermissionObserver, IActionHandler
{
    private readonly object gate = new();
    private readonly ApplicationDeclaration declaration;
    private readonly TaskCompletionSource<Func<FlyoutRequest, CancellationToken, Task<ProtocolResult>>> requester = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int[] confirmations = new int[12];
    private ApplicationState state;
    private long epoch;
    private bool requestPending;
    private int nextChannel = 2;

    public EventDemoProvider(string applicationId, Func<FlyoutRequest, CancellationToken, Task<ProtocolResult>>? request = null)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 || applicationId != applicationId.Trim())
            throw new ArgumentException("Invalid event demo application identity.", nameof(applicationId));
        var controls = new HostTemplate("controls", new("controls-root", TemplateNodeKind.Horizontal, Children: Freeze(
            Button("send", "发送事件", new(TemplateActionKind.Business, "send")),
            Button("persistent", "持续事件", new(TemplateActionKind.Business, "persistent")),
            Button("next", "下一通道", new(TemplateActionKind.Business, "next")))));
        var channels = Enumerable.Range(0, 12).Select(index => new EventChannelDeclaration("event" + index,
            index == 1 ? EventClosePolicy.Persistent : EventClosePolicy.AutoClose, EventTemplate(index),
            Freeze(new ActionSlotDeclaration("confirm"), new("fail")))).ToArray();
        declaration = new(applicationId, Freeze(new FeatureGroupDeclaration("main",
            Freeze(new ComponentDeclaration("controls", Freeze(new ActionSlotDeclaration("send"), new("persistent"), new("next")),
                Template: new("controls", Freeze(controls), Array.Empty<TemplateFieldDeclaration>()))),
            Freeze(new TaskbarFlyoutDeclaration("details", Freeze(new ActionSlotDeclaration("send")),
                new("details", Freeze(new HostTemplate("details", Button("send", "发送事件", new(TemplateActionKind.Business, "send")))),
                    Array.Empty<TemplateFieldDeclaration>()))), EventChannels: Array.AsReadOnly(channels))));
        state = CreateState(0);
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
            throw new ArgumentException("Unexpected event demo permission entries.", nameof(snapshot));
        return Task.CompletedTask;
    }

    public async Task<ActionCompletion> HandleAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        long capturedEpoch;
        int channel;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = invocation.Slot;
            if (slot is null || slot.ApplicationId != declaration.ApplicationId || slot.FeatureGroupId != "main" ||
                string.IsNullOrWhiteSpace(invocation.RequestId) || invocation.RequestId.Length > 256 || invocation.Sequence <= 0 ||
                invocation.Parameter is not { Kind: ActionParameterKind.None, Number: null, Boolean: null, Text: null })
                return Complete(ProtocolResult.Reject("ActionNotAvailable", "演示动作身份或参数无效。"));
            bool request = slot is { EntryKind: ActionEntryKind.Component, EntryId: "controls", ActionSlotId: "send" or "persistent" or "next" } or
                { EntryKind: ActionEntryKind.TaskbarFlyout, EntryId: "details", ActionSlotId: "send" };
            channel = Array.FindIndex(declaration.FeatureGroups![0].EventChannels!.ToArray(), value => value.ChannelId == slot.EntryId);
            bool business = slot.EntryKind == ActionEntryKind.EventChannel && channel >= 0 && slot.ActionSlotId is "confirm" or "fail";
            if (!(request || business)) return Complete(ProtocolResult.Reject("ActionNotAvailable", "演示动作槽无效。"));
            if (epoch == 0) return Complete(ProtocolResult.Reject("SessionNotReady", "当前会话尚未提交声明。"));
            if (business)
            {
                if (slot.ActionSlotId == "fail") return Complete(ProtocolResult.Reject("DemoEventFailure", "演示事件操作失败，已保留最后确认值。"));
                confirmations[channel] = checked(confirmations[channel] + 1);
                state = CreateState(checked(state.Revision + 1));
                return Complete(ProtocolResult.Success("ActionSucceeded"), state);
            }
            if (requestPending) return Complete(ProtocolResult.Reject("Busy", "事件显示请求正在等待确认。"));
            channel = slot.ActionSlotId switch { "send" => 0, "persistent" => 1, _ => nextChannel };
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
                if (epoch != capturedEpoch) return Complete(ProtocolResult.Reject("StaleSession", "旧会话事件请求已失效。"));
                receipt = request(new("sdk-assigned", 1, "main", "event" + channel, FlyoutKind.EventGroup, state.Revision,
                    Position: channel == 0 ? FlyoutPosition.TopRight : FlyoutPosition.Default), deadline.Token);
            }
            var result = await receipt.WaitAsync(deadline.Token).ConfigureAwait(false);
            lock (gate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (epoch != capturedEpoch) return Complete(ProtocolResult.Reject("StaleSession", "旧会话事件请求已失效。"));
                if (result.Accepted && invocation.Slot.ActionSlotId == "next") nextChannel = channel == 11 ? 2 : channel + 1;
                return Complete(result);
            }
        }
        finally { lock (gate) requestPending = false; }

        ActionCompletion Complete(ProtocolResult result, ApplicationState? confirmed = null) =>
            new(invocation.RequestId, invocation.Sequence, result, confirmed);
    }

    private ApplicationState CreateState(long revision)
    {
        var entries = new List<TemplateEntryState>
        { new(new("main", TemplateEntryKind.Component, "controls"), Array.Empty<TemplateFieldValue>()),
          new(new("main", TemplateEntryKind.TaskbarFlyout, "details"), Array.Empty<TemplateFieldValue>()) };
        for (int channel = 0; channel < confirmations.Length; channel++)
            entries.Add(new(new("main", TemplateEntryKind.EventChannel, "event" + channel), Freeze(
                new TemplateFieldValue("count", new(TemplateValueKind.Text, Text: "确认次数：" + confirmations[channel])),
                new TemplateFieldValue("status", new(TemplateValueKind.Text, Text: confirmations[channel] == 0 ? "等待确认" : "操作已确认")))));
        return new(revision, Freeze(new ComponentReading("main", "controls", "事件浮窗演示")), TemplateEntries: entries.AsReadOnly());
    }

    private static EntryTemplateDeclaration EventTemplate(int channel)
    {
        var main = new HostTemplate("main", new("main-root", TemplateNodeKind.Vertical, Children: Freeze(
            Text("title", "事件通道 " + channel), Field("count", "count"), Field("status", "status"),
            Button("confirm", "确认事件", new(TemplateActionKind.Business, "confirm")),
            Button("fail", "演示失败", new(TemplateActionKind.Business, "fail")),
            Button("open", "打开详情", new(TemplateActionKind.OpenPanel, "child")),
            Button("parallel", "并列信息", new(TemplateActionKind.OpenPanel, "parallel")))));
        var child = new HostTemplate("child", new("child-root", TemplateNodeKind.Vertical, Children: Freeze(
            Text("title", "事件详情 " + channel), Field("count", "count"),
            Button("confirm", "确认事件", new(TemplateActionKind.Business, "confirm")), Button("back", "返回", new(TemplateActionKind.Back)))));
        var parallel = new HostTemplate("parallel", new("parallel-root", TemplateNodeKind.Vertical, Children: Freeze(
            Text("title", "关联信息 " + channel), Field("status", "status"), Button("back", "返回", new(TemplateActionKind.Back)))));
        return new("main", Freeze(main, child, parallel), Freeze(new TemplateFieldDeclaration("count", TemplateValueKind.Text),
            new("status", TemplateValueKind.Text)), Panels: Freeze(new TemplatePanelReference("child"), new("parallel", PanelPresentation.Parallel)));
    }

    private static TemplateNode Text(string id, string text) => new(id, TemplateNodeKind.Text, Value: new(Literal: new(TemplateValueKind.Text, Text: text)));
    private static TemplateNode Field(string id, string field) => new(id, TemplateNodeKind.Text, Value: new(StateFieldId: field));
    private static TemplateNode Button(string id, string text, TemplateAction action) => new(id, TemplateNodeKind.Button,
        Value: new(Literal: new(TemplateValueKind.Text, Text: text)), AccessibleName: text, Action: action);
    private static IReadOnlyList<T> Freeze<T>(params T[] values) => Array.AsReadOnly(values);
}

using Mtp.Contracts;

namespace Mtp.Host;

/// <summary>Owns one template surface's local interaction, serialized independently from UI objects.</summary>
public sealed class TemplateInteractionController : IDisposable
{
    private readonly object gate = new();
    private readonly BrokerStateStore states;
    private readonly string applicationId;
    private readonly TemplateEntryReference entry;
    private readonly TemplateActionSender sender;
    private readonly Dictionary<string, ControlState> controls = new(StringComparer.Ordinal);
    private readonly List<string> panels = [];
    private BrokerApplicationSnapshot? current;
    private EntryTemplateDeclaration? declaration;
    private HostTemplate? template;
    private TemplateEntryState? confirmed;
    private CancellationTokenSource viewLifetime = new();
    private long generation;
    private bool disposed;

    public TemplateInteractionController(BrokerStateStore states, string applicationId, TemplateEntryReference entry, TemplateActionSender sender)
    {
        this.states = states ?? throw new ArgumentNullException(nameof(states));
        this.applicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
        this.entry = entry ?? throw new ArgumentNullException(nameof(entry));
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    public TemplateSurfaceSnapshot? GetSnapshot()
    {
        lock (gate)
        {
            Synchronize();
            if (template is null || current is null || confirmed is null) return null;
            return new(applicationId, current.SessionId, generation, current.State!.Revision, template,
                Array.AsReadOnly(Project().Select(value => value.Snapshot).ToArray()));
        }
    }

    public ProtocolResult Preview(string nodeId, TemplateValue value, long? expectedGeneration = null)
    {
        lock (gate)
        {
            Synchronize();
            var target = Find(nodeId, expectedGeneration);
            if (target is null) return Rejected("StaleTemplate", "模板或控件已失效");
            if (!target.Available) return Rejected("ControlUnavailable", "控件当前不可操作");
            if (!TryNormalize(target.Node, value, out var normalized)) return Rejected("InvalidControlValue", "控件值无效");
            Control(nodeId).Preview = normalized;
            return ProtocolResult.Success();
        }
    }

    public Task<ProtocolResult> ActivateAsync(string nodeId, TemplateValue? value = null,
        CancellationToken cancellationToken = default, long? expectedGeneration = null)
    {
        Request request; ControlState control;
        lock (gate)
        {
            Synchronize();
            if (cancellationToken.IsCancellationRequested) return Completed("ActionCancelled", "操作已取消");
            var target = Find(nodeId, expectedGeneration);
            if (target is null) return Completed("StaleTemplate", "模板或控件已失效");
            if (!target.Available) return Completed("ControlUnavailable", "控件当前不可操作");
            var action = target.Node.Action;
            if (action is null) return Completed("ActionNotAvailable", "控件未绑定动作");
            if (action.Kind != TemplateActionKind.Business) return Task.FromResult(Navigate(action));
            TemplateValue? normalized = null;
            ActionParameter parameter;
            if (target.Node.Kind is TemplateNodeKind.Toggle or TemplateNodeKind.Slider)
            {
                var candidate = value ?? target.Snapshot.Preview ?? target.Snapshot.Confirmed;
                if (value is null && target.Node.Kind == TemplateNodeKind.Toggle && candidate?.Boolean is { } boolean)
                    candidate = new(TemplateValueKind.Boolean, Boolean: !boolean);
                if (!TryNormalize(target.Node, candidate, out normalized)) return Completed("InvalidControlValue", "控件值无效");
                parameter = normalized!.Kind == TemplateValueKind.Boolean ? new(ActionParameterKind.Boolean, Boolean: normalized.Boolean) :
                    new(ActionParameterKind.Number, Number: normalized.Number);
            }
            else
            {
                if (value is not null) return Completed("InvalidControlValue", "按钮不接受额外值");
                parameter = new();
            }
            if (entry.Kind is not (TemplateEntryKind.Component or TemplateEntryKind.TaskbarFlyout))
                return Completed("ActionNotAvailable", "入口未声明业务动作");
            var slot = new ActionSlotReference(applicationId, entry.FeatureGroupId,
                entry.Kind == TemplateEntryKind.Component ? ActionEntryKind.Component : ActionEntryKind.TaskbarFlyout, entry.EntryId, action.TargetId!);
            if (current!.Declaration!.ActionSlots.Any(item => item.Reference == slot && item.ParameterKind == parameter.Kind) != true)
                return Completed("ActionNotAvailable", "当前入口动作或参数未声明");
            control = Control(nodeId);
            control.Error = null; control.Preview = normalized;
            request = new(slot, parameter, normalized, current.SessionId, generation, viewLifetime.Token, cancellationToken);
            if (control.Current is not null)
            {
                if (control.Pending is { } replaced)
                {
                    replaced.Cancellation.Unregister();
                    replaced.Completion.TrySetResult(Rejected("Superseded", "待发送目标已被更新"));
                }
                control.Pending = request;
                request.Cancellation = cancellationToken.Register(() => CancelPending(control, request));
                return request.Completion.Task;
            }
            control.Current = request;
        }
        _ = ExecuteAsync(nodeId, control, request);
        return request.Completion.Task;
    }

    private async Task ExecuteAsync(string nodeId, ControlState control, Request request)
    {
        ProtocolResult result;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.ViewToken, request.CallerToken);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            result = await sender(request.Slot, request.Parameter, request.SessionId, cancellation.Token).WaitAsync(cancellation.Token).ConfigureAwait(false);
            result ??= Rejected("InvalidActionResult", "动作未返回确认结果");
        }
        catch (OperationCanceledException)
        {
            result = request.ViewToken.IsCancellationRequested ? Rejected("StaleTemplate", "模板已更换或关闭") :
                Rejected("ActionCancelled", "操作已取消");
        }
        catch (Exception) { result = Rejected("ActionFailed", "动作通道未能完成请求"); }
        Request? next = null;
        lock (gate)
        {
            Synchronize();
            if (disposed || request.Generation != generation || !ReferenceEquals(control.Current, request))
                request.Completion.TrySetResult(Rejected("StaleTemplate", "模板已更换或关闭"));
            else
            {
                control.Current = null; control.Preview = null;
                control.Error = result.Accepted ? null : result.Code;
                request.Completion.TrySetResult(result);
                if (control.Pending is { } pending)
                {
                    control.Pending = null;
                    pending.Cancellation.Unregister();
                    if (pending.CallerToken.IsCancellationRequested)
                        pending.Completion.TrySetResult(Rejected("ActionCancelled", "操作已取消"));
                    else if (Find(nodeId, pending.Generation)?.Available != true)
                        pending.Completion.TrySetResult(Rejected("ControlUnavailable", "控件当前不可操作"));
                    else
                    {
                        next = pending; control.Current = pending;
                        control.Preview = pending.Value; control.Error = null;
                    }
                }
            }
        }
        if (next is not null) _ = ExecuteAsync(nodeId, control, next);
    }

    private void CancelPending(ControlState control, Request request)
    {
        lock (gate)
        {
            if (!ReferenceEquals(control.Pending, request)) return;
            control.Pending = null;
            control.Preview = control.Current?.Value;
            request.Completion.TrySetResult(Rejected("ActionCancelled", "操作已取消"));
        }
    }

    private ProtocolResult Navigate(TemplateAction action)
    {
        if (action.Kind == TemplateActionKind.Back)
        {
            if (panels.Count == 0) return Rejected("NoPreviousPanel", "已处于主面板");
            panels.RemoveAt(panels.Count - 1);
        }
        else if (action.Kind == TemplateActionKind.OpenPanel)
        {
            if (declaration?.Panels?.Any(value => value.TemplateId == action.TargetId) != true ||
                declaration.Templates.Any(value => value.TemplateId == action.TargetId) != true)
                return Rejected("UnknownPanel", "面板未声明");
            if (panels.Count >= TemplateLimits.TemplatesPerEntry - 1) return Rejected("PanelDepthExceeded", "面板层数超出预算");
            panels.Add(action.TargetId!);
        }
        else return Rejected("InvalidAction", "动作种类无效");
        ResetView();
        template = declaration!.Templates.First(value => value.TemplateId == (panels.Count == 0 ? declaration.MainTemplateId : panels[^1]));
        return ProtocolResult.Success();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ResetView();
            viewLifetime.Dispose();
            current = null; declaration = null; template = null; confirmed = null;
        }
    }

    private void Synchronize()
    {
        if (disposed) return;
        var next = states.GetSnapshot(applicationId);
        var schema = next?.Declaration?.Templates.FirstOrDefault(value => value.Entry == entry)?.Declaration;
        var values = next?.State?.TemplateEntries?.FirstOrDefault(value => value.Entry == entry);
        bool changed = !ReferenceEquals(schema, declaration) || current?.SessionId != next?.SessionId;
        bool lostConnection = current?.IsInteractive == true && next?.IsInteractive != true;
        if (changed || lostConnection || values is null && confirmed is not null)
        {
            ResetView();
            if (changed) panels.Clear();
        }
        current = next; declaration = schema; confirmed = values;
        template = schema is null || values is null ? null : schema.Templates.FirstOrDefault(value =>
            value.TemplateId == (panels.Count == 0 ? schema.MainTemplateId : panels[^1]));
    }

    private void ResetView()
    {
        generation = checked(generation + 1);
        viewLifetime.Cancel(); viewLifetime.Dispose(); viewLifetime = new();
        foreach (var control in controls.Values)
        {
            control.Current?.Completion.TrySetResult(Rejected("StaleTemplate", "模板已更换或关闭"));
            control.Pending?.Cancellation.Unregister();
            control.Pending?.Completion.TrySetResult(Rejected("StaleTemplate", "模板已更换或关闭"));
        }
        controls.Clear();
    }

    private IEnumerable<ProjectedNode> Project()
    {
        if (template is null) yield break;
        foreach (var node in Visit(template.Root, true, true)) yield return node;
    }

    private IEnumerable<ProjectedNode> Visit(TemplateNode node, bool parentEnabled, bool parentVisible)
    {
        bool visible = parentVisible && Resolve(node.Visible)?.Boolean != false;
        bool enabled = parentEnabled && Resolve(node.Enabled)?.Boolean != false;
        controls.TryGetValue(node.NodeId, out var control);
        bool available = enabled && visible && current?.IsInteractive == true && current.IsConnected;
        bool busy = control?.Current is not null;
        yield return new(node, new(node.NodeId, Resolve(node.Value), control?.Preview,
            available && !busy, visible, busy, control?.Error), available);
        foreach (var child in node.Children ?? [])
            foreach (var projected in Visit(child, enabled, visible)) yield return projected;
    }

    private TemplateValue? Resolve(TemplateBinding? binding) => binding?.Literal ??
        confirmed?.Fields.FirstOrDefault(value => value.FieldId == binding?.StateFieldId)?.Value ??
        declaration?.Fields.FirstOrDefault(value => value.FieldId == binding?.StateFieldId)?.DefaultValue;

    private ProjectedNode? Find(string nodeId, long? expectedGeneration) =>
        disposed || nodeId is null || expectedGeneration is { } expected && expected != generation ? null :
        Project().FirstOrDefault(value => value.Node.NodeId == nodeId);

    private ControlState Control(string nodeId)
    {
        if (!controls.TryGetValue(nodeId, out var control)) controls.Add(nodeId, control = new());
        return control;
    }

    private static bool TryNormalize(TemplateNode node, TemplateValue? value, out TemplateValue? normalized)
    {
        normalized = null;
        if (node.Kind == TemplateNodeKind.Toggle && value is { Kind: TemplateValueKind.Boolean, Boolean: not null, Text: null, Number: null, ResourceId: null })
            normalized = value;
        else if (node.Kind == TemplateNodeKind.Slider && value is { Kind: TemplateValueKind.Number, Number: { } number, Text: null, Boolean: null, ResourceId: null } &&
            double.IsFinite(number) && node.Minimum is { } minimum && node.Maximum is { } maximum && node.Step is { } step)
        {
            double steps = (Math.Clamp(number, minimum, maximum) - minimum) / step;
            double maximumSteps = (maximum - minimum) / step;
            double nearestMaximum = Math.Round(maximumSteps);
            double lastStep = Math.Abs(maximumSteps - nearestMaximum) <= 1e-8 ? nearestMaximum : Math.Floor(maximumSteps);
            double rounded = minimum + Math.Min(Math.Round(steps, MidpointRounding.AwayFromZero), lastStep) * step;
            if (double.IsFinite(rounded)) normalized = new(TemplateValueKind.Number, Number: Math.Clamp(rounded, minimum, maximum));
        }
        return normalized is not null;
    }

    private static ProtocolResult Rejected(string code, string message) => ProtocolResult.Reject(code, message);
    private static Task<ProtocolResult> Completed(string code, string message) => Task.FromResult(Rejected(code, message));
    private sealed class ControlState { public TemplateValue? Preview; public string? Error; public Request? Current; public Request? Pending; }
    private sealed record Request(ActionSlotReference Slot, ActionParameter Parameter, TemplateValue? Value, string SessionId,
        long Generation, CancellationToken ViewToken, CancellationToken CallerToken)
    {
        public TaskCompletionSource<ProtocolResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Cancellation { get; set; }
    }
    private sealed record ProjectedNode(TemplateNode Node, TemplateNodeSnapshot Snapshot, bool Available);
}

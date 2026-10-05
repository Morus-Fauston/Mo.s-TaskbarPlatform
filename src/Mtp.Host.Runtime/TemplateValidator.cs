using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host;

/// <summary>Validates and freezes Host-owned template declarations and complete typed field snapshots.</summary>
public sealed class TemplateValidator
{
    public CoreResult<ValidatedEntryTemplate> Validate(TemplateEntryReference entry, EntryTemplateDeclaration template,
        IReadOnlyList<ActionSlotDeclaration> actions, IReadOnlyList<ImageResourceDeclaration> images, ref int remainingNodes,
        bool allowHintActions = false, bool allowHintExpansion = false)
    {
        if (entry.Kind == TemplateEntryKind.Hint && template?.Panels is { Count: > 0 })
            return Reject("template_reference_invalid", "短提示不能声明内部关联面板；展开使用已验证的任务栏目标");
        if (template?.Templates is null || template.Fields is null || !ValidId(template.MainTemplateId) ||
            template.Templates.Count is < 1 or > TemplateLimits.TemplatesPerEntry || template.Fields.Count > TemplateLimits.FieldsPerEntry ||
            template.Panels?.Count > TemplateLimits.TemplatesPerEntry)
            return Reject("template_budget_invalid", "模板集合缺失或超出预算");
        int cost = 1 + template.Templates.Count + template.Fields.Count + (template.Panels?.Count ?? 0);
        var imageIds = images.Select(value => value.ResourceId).ToHashSet(StringComparer.Ordinal);
        var fields = new Dictionary<string, TemplateFieldDeclaration>(StringComparer.Ordinal);
        foreach (var field in template.Fields)
        {
            if (field is null || !ValidId(field.FieldId) || !Enum.IsDefined(field.Kind) || !fields.TryAdd(field.FieldId, field) ||
                (field.Optional && field.DefaultValue is null) || (!field.Optional && field.DefaultValue is not null) ||
                (field.DefaultValue is not null && !ValidValue(field.DefaultValue, field.Kind, imageIds)))
                return Reject("template_field_invalid", "字段或默认值无效");
            if (field.DefaultValue is not null) cost++;
        }
        var context = new NodeContext(fields, imageIds, actions, entry.Kind, allowHintActions, allowHintExpansion);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in template.Templates)
        {
            if (definition is null || !ValidId(definition.TemplateId) || !ids.Add(definition.TemplateId))
                return Reject("template_identity_invalid", "模板身份缺失或重复");
            int nodes = 0;
            if (!ValidTree(definition.Root, 1, new(StringComparer.Ordinal), new(ReferenceEqualityComparer.Instance), context, ref nodes, ref cost))
                return Reject("template_structure_invalid", "节点结构、属性或预算无效");
        }
        if (!ids.Contains(template.MainTemplateId) || (template.CompactTemplateId is not null && !ids.Contains(template.CompactTemplateId)))
            return Reject("template_reference_invalid", "入口引用的模板不存在");
        var panels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in template.Panels ?? [])
            if (panel is null || !ValidId(panel.TemplateId) || !Enum.IsDefined(panel.Presentation) ||
                panel.TemplateId == template.MainTemplateId || !ids.Contains(panel.TemplateId) || !panels.Add(panel.TemplateId))
                return Reject("template_reference_invalid", "关联面板引用无效或重复");
        var edges = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var definition in template.Templates)
        {
            var targets = Walk(definition.Root).Where(node => node.Action?.Kind == TemplateActionKind.OpenPanel).Select(node => node.Action!.TargetId!).ToArray();
            if (targets.Any(target => !panels.Contains(target))) return Reject("template_reference_invalid", "目标不是本入口关联面板");
            edges.Add(definition.TemplateId, targets);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        bool Acyclic(string id)
        {
            if (active.Contains(id)) return false;
            if (!visited.Add(id)) return true;
            active.Add(id);
            foreach (var target in edges[id]) if (!Acyclic(target)) return false;
            active.Remove(id);
            return true;
        }
        if (ids.Any(id => !Acyclic(id))) return Reject("template_reference_invalid", "关联面板不能形成引用环");
        if (cost > remainingNodes) return Reject("template_budget_exceeded", "模板超出共享声明节点预算");
        var frozen = template with
        {
            Templates = Array.AsReadOnly(template.Templates.Select(value => value with { Root = Freeze(value.Root) }).ToArray()),
            Fields = Array.AsReadOnly(template.Fields.ToArray()),
            Panels = Array.AsReadOnly(template.Panels?.ToArray() ?? [])
        };
        remainingNodes -= cost;
        return CoreResult<ValidatedEntryTemplate>.Success(new(entry, frozen));
    }

    private static bool ValidTree(TemplateNode? node, int depth, HashSet<string> ids, HashSet<TemplateNode> ancestors,
        NodeContext context, ref int nodes, ref int cost)
    {
        if (node is null || depth > TemplateLimits.MaximumDepth || ++nodes > TemplateLimits.NodesPerTemplate ||
            !ValidId(node.NodeId) || !ids.Add(node.NodeId) || !Enum.IsDefined(node.Kind) || !ancestors.Add(node) ||
            node.Children?.Count > TemplateLimits.ChildrenPerNode || node.AccessibleName?.Length > 1024 ||
            (node.Icon is not null && !Enum.IsDefined(node.Icon.Value))) return false;
        cost++;
        bool container = node.Kind is TemplateNodeKind.Horizontal or TemplateNodeKind.Vertical or TemplateNodeKind.Group or TemplateNodeKind.ListItem;
        bool control = node.Kind is TemplateNodeKind.Button or TemplateNodeKind.IconButton or TemplateNodeKind.Toggle or TemplateNodeKind.Slider or TemplateNodeKind.ListItem;
        if ((!container && node.Children is { Count: > 0 }) || (!control && node.Action is not null) ||
            (node.Kind != TemplateNodeKind.Slider && (node.Minimum is not null || node.Maximum is not null || node.Step is not null)) ||
            (node.Kind is not (TemplateNodeKind.Button or TemplateNodeKind.IconButton) && node.Icon is not null) ||
            (node.Kind != TemplateNodeKind.Image && node.Decorative) ||
            (container || node.Kind == TemplateNodeKind.Separator) && node.Value is not null) return false;
        if ((node.Kind is TemplateNodeKind.Horizontal or TemplateNodeKind.Vertical or TemplateNodeKind.Separator) && node.AccessibleName is not null)
            return false;
        if (node.Enabled is not null && !ValidBinding(node.Enabled, [TemplateValueKind.Boolean], context) ||
            node.Visible is not null && !ValidBinding(node.Visible, [TemplateValueKind.Boolean], context)) return false;
        var expected = node.Kind switch
        {
            TemplateNodeKind.Text => new[] { TemplateValueKind.Text },
            TemplateNodeKind.Image or TemplateNodeKind.IconButton => [TemplateValueKind.Resource],
            TemplateNodeKind.Button => [TemplateValueKind.Text, TemplateValueKind.Resource],
            TemplateNodeKind.Toggle => [TemplateValueKind.Boolean],
            TemplateNodeKind.Slider => [TemplateValueKind.Number],
            _ => Array.Empty<TemplateValueKind>()
        };
        if (node.Value is not null && !ValidBinding(node.Value, expected, context)) return false;
        if (node.Kind is TemplateNodeKind.Text or TemplateNodeKind.Image or TemplateNodeKind.Toggle or TemplateNodeKind.Slider && node.Value is null)
            return false;
        if ((control && node.Kind != TemplateNodeKind.ListItem || node.Kind == TemplateNodeKind.Image && !node.Decorative) &&
            string.IsNullOrWhiteSpace(node.AccessibleName)) return false;
        if (node.Kind is TemplateNodeKind.Toggle or TemplateNodeKind.Slider &&
            (node.Value?.StateFieldId is null || node.Action?.Kind != TemplateActionKind.Business)) return false;
        if (node.Kind == TemplateNodeKind.IconButton && node.Icon is null && node.Value is null) return false;
        if (node.Icon is not null && node.Value is not null && BindingKind(node.Value, context) == TemplateValueKind.Resource) return false;
        if (node.Kind == TemplateNodeKind.Slider && (node.Minimum is not double min || !double.IsFinite(min) ||
            node.Maximum is not double max || !double.IsFinite(max) || min >= max || !double.IsFinite(max - min) ||
            node.Step is not double step || !double.IsFinite(step) || step <= 0)) return false;
        if (node.Kind == TemplateNodeKind.Slider && context.Fields[node.Value!.StateFieldId!].DefaultValue?.Number is double defaultNumber &&
            !ValidSliderValue(node, defaultNumber)) return false;
        if (node.Kind is TemplateNodeKind.Button or TemplateNodeKind.IconButton && node.Action is null) return false;
        if (node.Action is { } action)
        {
            cost++;
            if (!Enum.IsDefined(action.Kind) || (action.Kind is TemplateActionKind.Back or TemplateActionKind.ExpandHint
                ? action.TargetId is not null : !ValidId(action.TargetId))) return false;
            if (context.EntryKind == TemplateEntryKind.Hint && !context.AllowHintActions) return false;
            if (context.EntryKind == TemplateEntryKind.Hint && action.Kind is TemplateActionKind.OpenPanel or TemplateActionKind.Back) return false;
            if (action.Kind == TemplateActionKind.ExpandHint &&
                (context.EntryKind != TemplateEntryKind.Hint || !context.AllowHintActions || !context.AllowHintExpansion)) return false;
            if (action.Kind == TemplateActionKind.Business)
            {
                var parameter = node.Kind == TemplateNodeKind.Toggle ? ActionParameterKind.Boolean :
                    node.Kind == TemplateNodeKind.Slider ? ActionParameterKind.Number : ActionParameterKind.None;
                if (!context.Actions.Any(value => value.ActionSlotId == action.TargetId && value.ParameterKind == parameter)) return false;
            }
        }
        cost += (node.Value is null ? 0 : 1 + (node.Value.Literal is null ? 0 : 1)) +
            (node.Enabled is null ? 0 : 1 + (node.Enabled.Literal is null ? 0 : 1)) +
            (node.Visible is null ? 0 : 1 + (node.Visible.Literal is null ? 0 : 1));
        foreach (var child in node.Children ?? [])
            if (!ValidTree(child, depth + 1, ids, ancestors, context, ref nodes, ref cost)) return false;
        ancestors.Remove(node);
        return true;
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= DeclarationValidator.MaximumIdLength && value == value.Trim();

    private sealed record NodeContext(IReadOnlyDictionary<string, TemplateFieldDeclaration> Fields, HashSet<string> Images,
        IReadOnlyList<ActionSlotDeclaration> Actions, TemplateEntryKind EntryKind, bool AllowHintActions, bool AllowHintExpansion);

    private static TemplateValueKind? BindingKind(TemplateBinding binding, NodeContext context) => binding.Literal?.Kind ??
        (binding.StateFieldId is not null && context.Fields.TryGetValue(binding.StateFieldId, out var field) ? field.Kind : null);

    private static bool ValidBinding(TemplateBinding binding, TemplateValueKind[] expected, NodeContext context)
    {
        if ((binding.Literal is null) == (binding.StateFieldId is null)) return false;
        if (binding.Literal is { } value) return expected.Contains(value.Kind) && ValidValue(value, value.Kind, context.Images);
        return ValidId(binding.StateFieldId) && context.Fields.TryGetValue(binding.StateFieldId!, out var field) && expected.Contains(field.Kind);
    }

    private static bool ValidValue(TemplateValue? value, TemplateValueKind expected, HashSet<string> images) =>
        value is not null && value.Kind == expected && value.Kind switch
        {
            TemplateValueKind.Text => value.Text is { Length: <= 1024 } && value.Boolean is null && value.Number is null && value.ResourceId is null,
            TemplateValueKind.Boolean => value.Boolean is not null && value.Text is null && value.Number is null && value.ResourceId is null,
            TemplateValueKind.Number => value.Number is double number && double.IsFinite(number) && value.Text is null && value.Boolean is null && value.ResourceId is null,
            TemplateValueKind.Resource => ValidId(value.ResourceId) && images.Contains(value.ResourceId!) && value.Text is null && value.Boolean is null && value.Number is null,
            _ => false
        };
    private static CoreResult<ValidatedEntryTemplate> Reject(string code, string message) =>
        CoreResult<ValidatedEntryTemplate>.Failure(new(code, message, "template"));

    public CoreResult<IReadOnlyList<TemplateEntryState>> ValidateState(ValidatedApplicationDeclaration declaration,
        IReadOnlyList<TemplateEntryState>? entries)
    {
        entries ??= [];
        if (entries.Count != declaration.Templates.Count) return StateError("模板状态必须包含全部声明入口");
        var schemas = declaration.Templates.ToDictionary(value => value.Entry);
        var images = declaration.Images.Select(value => value.ResourceId).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<TemplateEntryReference>();
        var frozen = new List<TemplateEntryState>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry?.Entry is null || !seen.Add(entry.Entry) || !schemas.TryGetValue(entry.Entry, out var schema) ||
                entry.Fields is null || entry.Fields.Count > TemplateLimits.FieldsPerEntry) return StateError("模板状态入口无效或重复");
            var fields = schema.Declaration.Fields.ToDictionary(value => value.FieldId, StringComparer.Ordinal);
            var values = new Dictionary<string, TemplateValue>(StringComparer.Ordinal);
            foreach (var field in entry.Fields)
                if (field is null || !ValidId(field.FieldId) || !fields.TryGetValue(field.FieldId, out var definition) ||
                    !ValidValue(field.Value, definition.Kind, images) || !values.TryAdd(field.FieldId, field.Value))
                    return StateError("模板字段缺失、重复、类型或资源无效");
            foreach (var definition in schema.Declaration.Fields)
            {
                if (values.ContainsKey(definition.FieldId)) continue;
                if (!definition.Optional) return StateError("必需模板字段缺失");
                values.Add(definition.FieldId, definition.DefaultValue!);
            }
            foreach (var slider in schema.Declaration.Templates.SelectMany(value => Walk(value.Root)).Where(value => value.Kind == TemplateNodeKind.Slider))
            {
                var value = values[slider.Value!.StateFieldId!].Number!.Value;
                if (!ValidSliderValue(slider, value))
                    return StateError("滑块确认值必须在范围内并符合步长");
            }
            frozen.Add(new(entry.Entry, Array.AsReadOnly(schema.Declaration.Fields.Select(value => new TemplateFieldValue(value.FieldId, values[value.FieldId])).ToArray())));
        }
        return CoreResult<IReadOnlyList<TemplateEntryState>>.Success(Array.AsReadOnly(frozen.ToArray()));
    }

    private static CoreResult<IReadOnlyList<TemplateEntryState>> StateError(string message) =>
        CoreResult<IReadOnlyList<TemplateEntryState>>.Failure(new("template_state_invalid", message, "state.templateEntries"));

    private static bool ValidSliderValue(TemplateNode slider, double value)
    {
        var steps = (value - slider.Minimum!.Value) / slider.Step!.Value;
        if (!double.IsFinite(value) || value < slider.Minimum || value > slider.Maximum || !double.IsFinite(steps)) return false;
        var nearest = Math.Round(steps);
        var canonical = slider.Minimum.Value + nearest * slider.Step.Value;
        return Math.Abs(steps - nearest) <= 1e-8 || value == canonical;
    }

    private static TemplateNode Freeze(TemplateNode node) => node with
    {
        Children = Array.AsReadOnly(node.Children?.Select(Freeze).ToArray() ?? [])
    };

    private static IEnumerable<TemplateNode> Walk(TemplateNode node)
    {
        yield return node;
        foreach (var child in node.Children ?? [])
            foreach (var descendant in Walk(child)) yield return descendant;
    }
}

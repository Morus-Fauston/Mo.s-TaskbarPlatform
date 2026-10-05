using Mtp.Contracts;

internal static class TemplateCounterDeclaration
{
    public static EntryTemplateDeclaration Create() => new("main", [new HostTemplate("main",
        new("root", TemplateNodeKind.Horizontal, Children:
        [
            new("status-image", TemplateNodeKind.Image, Value: Literal(new(TemplateValueKind.Resource, ResourceId: "status")), AccessibleName: "示例状态图像"),
            new("group", TemplateNodeKind.Group, Children:
            [new("vertical", TemplateNodeKind.Vertical, Children:
                [new("row", TemplateNodeKind.ListItem, Children:
                    [new("count-text", TemplateNodeKind.Text, Value: new(StateFieldId: "count"), AccessibleName: "确认计数")])])]),
            new("separator", TemplateNodeKind.Separator),
            new("increment", TemplateNodeKind.Button, Value: Literal(new(TemplateValueKind.Text, Text: "+10")), AccessibleName: "增加计数", Action: new(TemplateActionKind.Business, "activate")),
            new("icon-increment", TemplateNodeKind.IconButton, AccessibleName: "增加十", Icon: HostIcon.Play, Action: new(TemplateActionKind.Business, "activate")),
            new("enabled-toggle", TemplateNodeKind.Toggle, Value: new(StateFieldId: "enabled"), AccessibleName: "演示开关", Action: new(TemplateActionKind.Business, "enabled")),
            new("level-slider", TemplateNodeKind.Slider, Value: new(StateFieldId: "level"), AccessibleName: "演示读数", Action: new(TemplateActionKind.Business, "level"), Minimum: 0, Maximum: 100, Step: 5),
        ]))],
        [new("count", TemplateValueKind.Text), new("enabled", TemplateValueKind.Boolean), new("level", TemplateValueKind.Number)]);

    private static TemplateBinding Literal(TemplateValue value) => new(Literal: value);
}

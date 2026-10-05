using System.Collections.Generic;

namespace Mtp.Contracts;

public enum TemplateNodeKind { Text, Image, Button, IconButton, Toggle, Slider, Horizontal, Vertical, Group, Separator, ListItem }
public enum TemplateValueKind { Text, Boolean, Number, Resource }
public enum TemplateEntryKind { Component, TaskbarFlyout, Hint, EventChannel }
public enum TemplateActionKind { Business, OpenPanel, Back }
public enum HostIcon { Info, Check, Warning, Error, Play, Pause, Settings, ChevronRight, ChevronLeft }
public enum PanelPresentation { Hierarchical, Parallel }
public enum ImageResourceFormat { Png, Jpeg }

public sealed record TemplateValue(TemplateValueKind Kind, string? Text = null, bool? Boolean = null,
    double? Number = null, string? ResourceId = null);
public sealed record TemplateBinding(TemplateValue? Literal = null, string? StateFieldId = null);
public sealed record TemplateFieldDeclaration(string FieldId, TemplateValueKind Kind, bool Optional = false,
    TemplateValue? DefaultValue = null);
public sealed record TemplateAction(TemplateActionKind Kind, string? TargetId = null);
public sealed record TemplateNode(string NodeId, TemplateNodeKind Kind,
    IReadOnlyList<TemplateNode>? Children = null, TemplateBinding? Value = null,
    string? AccessibleName = null, TemplateAction? Action = null, HostIcon? Icon = null,
    bool Decorative = false, double? Minimum = null, double? Maximum = null, double? Step = null,
    TemplateBinding? Enabled = null, TemplateBinding? Visible = null);
public sealed record HostTemplate(string TemplateId, TemplateNode Root);
public sealed record TemplatePanelReference(string TemplateId, PanelPresentation Presentation = PanelPresentation.Hierarchical);
public sealed record EntryTemplateDeclaration(string MainTemplateId, IReadOnlyList<HostTemplate> Templates,
    IReadOnlyList<TemplateFieldDeclaration> Fields, string? CompactTemplateId = null,
    IReadOnlyList<TemplatePanelReference>? Panels = null);
public sealed record ImageResourceDeclaration(string ResourceId, ImageResourceFormat Format);
public sealed record TemplateEntryReference(string FeatureGroupId, TemplateEntryKind Kind, string EntryId);
public sealed record TemplateFieldValue(string FieldId, TemplateValue Value);
public sealed record TemplateEntryState(TemplateEntryReference Entry, IReadOnlyList<TemplateFieldValue> Fields);

public static class TemplateLimits
{
    public const int TemplatesPerEntry = 16;
    public const int NodesPerTemplate = 256;
    public const int MaximumDepth = 8;
    public const int ChildrenPerNode = 64;
    public const int FieldsPerEntry = 128;
    public const int ImagesPerApplication = 64;
    public const int EncodedImageBytes = 4 * 1024 * 1024;
    public const int ImageEdgePixels = 1024;
    public const int DecodedImageBytesPerApplication = 16 * 1024 * 1024;
}

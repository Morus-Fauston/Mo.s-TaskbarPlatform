using Mtp.Contracts;

namespace Mtp.Host;

public sealed record TemplateNodeSnapshot(string NodeId, TemplateValue? Confirmed, TemplateValue? Preview,
    bool Enabled, bool Visible, bool Busy, string? Error = null);
public sealed record TemplateSurfaceSnapshot(string ApplicationId, string SessionId, long Generation,
    long Revision, HostTemplate Template, IReadOnlyList<TemplateNodeSnapshot> Nodes);

public delegate Task<ProtocolResult> TemplateActionSender(ActionSlotReference slot, ActionParameter parameter,
    string expectedSessionId, CancellationToken cancellationToken);

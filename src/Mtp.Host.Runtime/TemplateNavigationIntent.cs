using Mtp.Contracts;

namespace Mtp.Host;

public sealed record TemplateNavigationIntent(string ApplicationId, TemplateEntryReference Entry, string SessionId,
    long ControllerGeneration, string SourceTemplateId, string SourceNodeId, TemplateAction Action);

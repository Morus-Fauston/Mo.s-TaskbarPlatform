using Mtp.Contracts;

namespace Mtp.Host;

/// <summary>Frozen entry schema. The validator owns construction and all nested collection copies.</summary>
public sealed record ValidatedEntryTemplate(TemplateEntryReference Entry, EntryTemplateDeclaration Declaration);

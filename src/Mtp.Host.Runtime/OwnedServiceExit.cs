namespace Mtp.Host;

/// <summary>A confirmed exit of an explicitly Host-owned service in its bound protocol session.</summary>
public sealed record OwnedServiceExit(string ApplicationId, string SessionId, int ProcessId, int ExitCode);

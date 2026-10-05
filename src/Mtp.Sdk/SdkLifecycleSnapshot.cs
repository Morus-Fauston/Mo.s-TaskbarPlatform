namespace Mtp.Sdk;

/// <summary>SDK-owned workers and queues only; arbitrary provider tasks are not owned or claimed stopped.</summary>
public sealed record SdkLifecycleSnapshot(bool CredentialReaderCompleted, bool RecoveryWorkerCompleted,
    int CredentialQueueCount, SdkConnectionLifecycleSnapshot? CurrentConnection,
    SdkConnectionLifecycleSnapshot? LastRetiredConnection);

public sealed record SdkConnectionLifecycleSnapshot(string SessionId, bool ReaderCompleted,
    bool ActionWorkerCompleted, bool HeartbeatWorkerCompleted, bool PermissionWorkerCompleted,
    bool PendingRequest, int ActionQueueCount, int PermissionQueueCount, int ActivePermissionSubscriptions);

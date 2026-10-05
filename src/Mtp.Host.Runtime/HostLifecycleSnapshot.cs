namespace Mtp.Host;

/// <summary>Observed owned resources; retained business snapshots are not live transport sessions.</summary>
public sealed record HostLifecycleSnapshot(int OwnedServices, int OwnedBrokers, int ActiveClockTasks,
    int ActiveRecoveryTasks, int ActiveOutputDrains, int OutstandingActions, int BusyActions,
    BrokerGenerationLifecycleSnapshot? CurrentBroker, BrokerGenerationLifecycleSnapshot? LastRetiredBroker);

public sealed record BrokerGenerationLifecycleSnapshot(long Generation, bool ReceiverCompleted,
    bool ActionDispatcherCompleted, bool PermissionPublisherCompleted, int ActionQueueCount,
    int PermissionSnapshotCount, int PendingRegistrationCount);

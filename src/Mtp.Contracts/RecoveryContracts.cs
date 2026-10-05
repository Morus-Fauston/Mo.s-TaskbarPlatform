using System;
using System.Collections.Generic;

namespace Mtp.Contracts;

public static class RecoveryLimits
{
    public const int ServiceWindowSeconds = 20;
    public const int BrokerWindowSeconds = 25;
    public const int ConnectionTimeoutSeconds = 5;
    public const int MaximumTicketAttempts = 3;
    public const int MaximumServiceRestarts = 1;
    public const int MaximumBrokerAttempts = 3;
    public static IReadOnlyList<int> TicketAttemptSeconds { get; } = Array.AsReadOnly(new[] { 0, 6, 13 });
}

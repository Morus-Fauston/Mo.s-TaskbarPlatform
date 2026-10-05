using Mtp.Transport;

namespace Mtp.Platform.Core.Tests;

public sealed class HeartbeatMonitorTests
{
    [Fact]
    public void BusinessIdleSessionStaysHealthyWhileIncreasingHeartbeatsArrive()
    {
        var clock = new ManualClock();
        var monitor = new HeartbeatMonitor(clock);
        Assert.True(monitor.StartSession("app", "session").Accepted);
        for (var sequence = 1; sequence <= 10; sequence++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(monitor.Receive("app", "session", sequence, clock.GetUtcNow()).Accepted);
            Assert.Empty(monitor.Tick());
        }
        Assert.Equal(10, monitor.GetSnapshot("app")!.LastSequence);
        Assert.Null(monitor.GetSnapshot("app")!.Failure);
    }

    [Fact]
    public void DeadlineReportsOnceAndDoesNotFaultAnApplicationWithFreshHeartbeat()
    {
        var clock = new ManualClock();
        var monitor = new HeartbeatMonitor(clock);
        monitor.StartSession("idle", "one");
        monitor.StartSession("healthy", "two");
        clock.Advance(HeartbeatMonitor.Timeout - TimeSpan.FromTicks(1));
        Assert.Empty(monitor.Tick());
        Assert.True(monitor.Receive("healthy", "two", 1, clock.GetUtcNow()).Accepted);
        clock.Advance(TimeSpan.FromTicks(1));

        Assert.Equal(new HeartbeatFault("idle", "one", HeartbeatFailureReason.HeartbeatTimedOut), Assert.Single(monitor.Tick()));
        Assert.Empty(monitor.Tick());
        Assert.Null(monitor.GetSnapshot("healthy")!.Failure);
        Assert.Equal(HeartbeatFailureReason.HeartbeatTimedOut, monitor.GetSnapshot("idle")!.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidOrRepeatedSequencesDoNotExtendDeadline(long repeatedSequence)
    {
        var clock = new ManualClock();
        var monitor = new HeartbeatMonitor(clock);
        monitor.StartSession("app", "session");
        Assert.True(monitor.Receive("app", "session", 2, clock.GetUtcNow()).Accepted);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(monitor.Receive("app", "session", repeatedSequence, clock.GetUtcNow()).Accepted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(monitor.Tick());
        Assert.Equal(2, monitor.GetSnapshot("app")!.LastSequence);
    }

    [Fact]
    public void DeadlineUsesReceiverMonotonicClockDespiteSenderAndUtcClockChanges()
    {
        var clock = new ManualClock();
        var monitor = new HeartbeatMonitor(clock);
        monitor.StartSession("app", "session");
        Assert.True(monitor.Receive("app", "session", 1, DateTimeOffset.MaxValue).Accepted);
        clock.UtcOffset = TimeSpan.FromDays(30);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(monitor.Tick());
        clock.UtcOffset = TimeSpan.FromDays(-30);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(monitor.Tick());
        Assert.Equal(DateTimeOffset.MaxValue, monitor.GetSnapshot("app")!.LastSentAt);
    }

    [Fact]
    public void NewSessionRejectsOldHeartbeatFaultAndCleanup()
    {
        var clock = new ManualClock();
        var monitor = new HeartbeatMonitor(clock);
        monitor.StartSession("app", "old");
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(monitor.StartSession("app", "new").Accepted);
        Assert.False(monitor.Receive("app", "old", 99, clock.GetUtcNow()).Accepted);
        Assert.Null(monitor.ReportFault("app", "old", HeartbeatFailureReason.ProcessExited));
        monitor.EndSession("app", "old");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(monitor.Tick());
        Assert.Equal("new", Assert.Single(monitor.Snapshots).SessionId);
        Assert.Equal(0, monitor.GetSnapshot("app")!.LastSequence);
        Assert.True(monitor.Receive("app", "new", 1, clock.GetUtcNow()).Accepted);
    }

    [Fact]
    public void RepeatedSessionRegistrationCannotRenewDeadline()
    {
        var clock = new ManualClock();
        var monitor = new HeartbeatMonitor(clock);
        monitor.StartSession("app", "session");
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(monitor.StartSession("app", "session").Accepted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(monitor.Tick());
    }

    [Theory]
    [InlineData(HeartbeatFailureReason.ProcessExited)]
    [InlineData(HeartbeatFailureReason.PipeDisconnected)]
    public void FirstFaultIsRetainedAndFailedSessionRequiresNewHandshake(HeartbeatFailureReason reason)
    {
        var clock = new ManualClock();
        var monitor = new HeartbeatMonitor(clock);
        monitor.StartSession("app", "old");
        Assert.Equal(new HeartbeatFault("app", "old", reason), monitor.ReportFault("app", "old", reason));
        Assert.Null(monitor.ReportFault("app", "old", HeartbeatFailureReason.HeartbeatTimedOut));
        Assert.False(monitor.Receive("app", "old", 1, clock.GetUtcNow()).Accepted);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Empty(monitor.Tick());
        Assert.Equal(reason, monitor.GetSnapshot("app")!.Failure);
        Assert.True(monitor.StartSession("app", "new").Accepted);
        Assert.True(monitor.Receive("app", "new", 1, clock.GetUtcNow()).Accepted);
        Assert.Null(monitor.GetSnapshot("app")!.Failure);
    }

    [Fact]
    public void SessionCountIsBoundedAndSnapshotsCannotMutateLiveState()
    {
        var monitor = new HeartbeatMonitor(new ManualClock());
        for (var index = 0; index < Mtp.Contracts.ProtocolLimits.MaximumApplications; index++)
            Assert.True(monitor.StartSession($"app-{index}", "old").Accepted);
        Assert.False(monitor.StartSession("overflow", "old").Accepted);
        var snapshot = monitor.Snapshots;
        Assert.Throws<NotSupportedException>(() => ((IList<HeartbeatSessionSnapshot>)snapshot).Clear());
        Assert.True(monitor.StartSession("app-0", "new").Accepted);
        Assert.Equal("old", snapshot.Single(value => value.ApplicationId == "app-0").SessionId);
        monitor.EndSession("app-0", "new");
        Assert.True(monitor.StartSession("overflow", "session").Accepted);
        Assert.Equal(Mtp.Contracts.ProtocolLimits.MaximumApplications, monitor.Snapshots.Count);
    }

    [Fact]
    public void ShutdownDiscardsSessionsAndSuppressesLateSignals()
    {
        var clock = new ManualClock();
        var monitor = new HeartbeatMonitor(clock);
        monitor.StartSession("app", "session");
        monitor.Shutdown();
        monitor.Shutdown();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(monitor.Tick());
        Assert.Empty(monitor.Snapshots);
        Assert.Null(monitor.GetSnapshot("app"));
        Assert.Null(monitor.ReportFault("app", "session", HeartbeatFailureReason.ProcessExited));
        Assert.False(monitor.Receive("app", "session", 1, clock.GetUtcNow()).Accepted);
        Assert.False(monitor.StartSession("app", "new").Accepted);
    }

    [Fact]
    public void InvalidFieldsDoNotCreateSessionsOrChangeLiveness()
    {
        var clock = new ManualClock();
        var monitor = new HeartbeatMonitor(clock);
        Assert.False(monitor.StartSession(" app", "session").Accepted);
        Assert.False(monitor.StartSession("app", new string('s', 257)).Accepted);
        Assert.Empty(monitor.Snapshots);
        monitor.StartSession("app", "session");
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(monitor.Receive("app", "session", 1, default).Accepted);
        Assert.Null(monitor.ReportFault("app", "session", (HeartbeatFailureReason)999));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(monitor.Tick());
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public TimeSpan UtcOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks).Add(UtcOffset);
        public void Advance(TimeSpan delta) => ticks += delta.Ticks;
    }
}

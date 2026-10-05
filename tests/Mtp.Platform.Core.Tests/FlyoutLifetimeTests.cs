namespace Mtp.Platform.Core.Tests;

public sealed class FlyoutLifetimeTests
{
    [Fact]
    public void ShortHintStartsOnFirstVisibilityAndIgnoresPointerAndWallClockChanges()
    {
        var clock = new ManualClock();
        var lifetime = new FlyoutLifetime(7, FlyoutLifetimeKind.ShortHint, clock: clock);
        clock.Advance(60_000);
        Assert.False(lifetime.IsExpired(7));
        Assert.True(lifetime.MarkVisible(7));
        clock.Advance(2_000);
        Assert.True(lifetime.MarkVisible(7)); // Repeated layout/visibility notification cannot extend life.
        Assert.True(lifetime.SetInteraction(7, "pointer", true));
        clock.UtcNow = clock.UtcNow.AddDays(10);
        clock.Advance(999);
        Assert.False(lifetime.IsExpired(7));
        clock.Advance(1);
        Assert.True(lifetime.IsExpired(7));
        Assert.False(lifetime.IsProtected(7));
    }

    [Fact]
    public void OnlyAnExplicitNewValidRefreshRestartsTheWholeHintDuration()
    {
        var clock = new ManualClock();
        var lifetime = new FlyoutLifetime(1, FlyoutLifetimeKind.ShortHint, clock: clock);
        Assert.True(lifetime.Refresh(1)); // Creation time still does not count.
        clock.Advance(10_000);
        Assert.True(lifetime.MarkVisible(1));
        clock.Advance(2_500);
        Assert.True(lifetime.Refresh(1));
        Assert.False(lifetime.Refresh(0));
        clock.Advance(2_999);
        Assert.False(lifetime.IsExpired(1));
        clock.Advance(1);
        Assert.True(lifetime.IsExpired(1));
    }

    [Fact]
    public void MultipleInteractiveSourcesPauseUntilTheLastOneEndsThenRestartFiveSeconds()
    {
        var clock = new ManualClock();
        var lifetime = new FlyoutLifetime(1, FlyoutLifetimeKind.InteractiveHint, clock: clock);
        Assert.True(lifetime.MarkVisible(1));
        clock.Advance(4_999);
        Assert.True(lifetime.SetInteraction(1, "pointer", true));
        Assert.True(lifetime.SetInteraction(1, "keyboard", true));
        clock.Advance(20_000);
        Assert.True(lifetime.Refresh(1));
        Assert.False(lifetime.IsExpired(1));
        Assert.True(lifetime.SetInteraction(1, "pointer", false));
        clock.Advance(20_000);
        Assert.False(lifetime.IsExpired(1));
        Assert.True(lifetime.SetInteraction(1, "keyboard", false));
        clock.Advance(4_999);
        Assert.False(lifetime.SetInteraction(1, "keyboard", false)); // A duplicate end cannot reset.
        Assert.False(lifetime.IsExpired(1));
        clock.Advance(1);
        Assert.True(lifetime.IsExpired(1));
        Assert.False(lifetime.IsProtected(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventProtectionLastsFiveSecondsAfterInteractionIndependentlyOfEightSecondExpiry(bool persistent)
    {
        var clock = new ManualClock();
        var lifetime = new FlyoutLifetime(1, FlyoutLifetimeKind.EventGroup, persistent, clock);
        lifetime.MarkVisible(1);
        Assert.False(lifetime.IsProtected(1));
        Assert.True(lifetime.SetInteraction(1, "keyboard", true));
        clock.Advance(50_000);
        Assert.True(lifetime.IsProtected(1));
        Assert.False(lifetime.IsExpired(1));
        Assert.True(lifetime.SetInteraction(1, "keyboard", false));
        clock.Advance(4_999);
        Assert.True(lifetime.IsProtected(1));
        clock.Advance(1);
        Assert.False(lifetime.IsProtected(1));
        Assert.False(lifetime.IsExpired(1));
        clock.Advance(2_999);
        Assert.False(lifetime.IsExpired(1));
        clock.Advance(1);
        Assert.Equal(!persistent, lifetime.IsExpired(1));
    }

    [Fact]
    public void OldGenerationsAndExcessSourcesCannotMutateCurrentLifetime()
    {
        var clock = new ManualClock();
        var lifetime = new FlyoutLifetime(8, FlyoutLifetimeKind.InteractiveHint, clock: clock);
        Assert.False(lifetime.MarkVisible(7));
        Assert.False(lifetime.SetInteraction(8, "before-visible", true));
        lifetime.MarkVisible(8);
        Assert.False(lifetime.SetInteraction(7, "old", true));
        Assert.False(lifetime.SetInteraction(8, "", true));
        Assert.False(lifetime.SetInteraction(8, new string('a', 257), true));
        for (var i = 0; i < 16; i++) Assert.True(lifetime.SetInteraction(8, i.ToString(), true));
        Assert.True(lifetime.SetInteraction(8, "0", true));
        Assert.False(lifetime.SetInteraction(8, "overflow", true));
        clock.Advance(10_000);
        for (var i = 0; i < 16; i++) Assert.True(lifetime.SetInteraction(8, i.ToString(), false));
        clock.Advance(5_000);
        Assert.True(lifetime.IsExpired(8));
        Assert.False(lifetime.IsExpired(7));
        Assert.False(lifetime.IsProtected(7));
    }

    [Fact]
    public void TaskbarGroupsNeverExpireAndInvalidKindsOrPersistentHintsAreRejected()
    {
        var clock = new ManualClock();
        var lifetime = new FlyoutLifetime(1, FlyoutLifetimeKind.TaskbarGroup, clock: clock);
        lifetime.MarkVisible(1);
        clock.Advance(100_000);
        Assert.False(lifetime.IsExpired(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlyoutLifetime(0, FlyoutLifetimeKind.ShortHint));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlyoutLifetime(1, (FlyoutLifetimeKind)99));
        Assert.Throws<ArgumentException>(() => new FlyoutLifetime(1, FlyoutLifetimeKind.ShortHint, true));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public DateTimeOffset UtcNow = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(long milliseconds) => timestamp += milliseconds;
    }
}

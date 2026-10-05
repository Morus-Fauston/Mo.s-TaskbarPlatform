using Mtp.Contracts;
using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class TimerPresentationSamplerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();

    [Theory]
    [InlineData(TimerDirection.CountUp, 5000, 8750, "00:08")]
    [InlineData(TimerDirection.CountDown, 10000, 6250, "00:07")]
    public void FirstObservationUsesProviderReferenceThenAdvancesFromLocalMonotonicTime(
        TimerDirection direction, double initial, double expected, string text)
    {
        var sampler = new TimerPresentationSampler();
        var item = Item(new(direction, Now.AddSeconds(-2), initial));
        Assert.True(sampler.Synchronize([item], Now, TimeSpan.FromSeconds(10)).Accepted);
        var reading = Assert.Single(sampler.Sample(TimeSpan.FromSeconds(11.75)));
        Assert.Equal(new TaskbarItemKey(new("app", "main", "island"), "timer", 1), reading.Key);
        Assert.Equal(expected, reading.ValueMilliseconds);
        Assert.Equal(text, reading.Text);
        Assert.True(reading.IsAdvancing);
        Assert.False(reading.IsPaused);
        Assert.False(reading.IsOvertime);
        Assert.Null(reading.ProgressFraction);
    }

    [Fact]
    public void EqualBasisSurvivesBusinessRefreshUtcJumpsAndDelayedMonotonicCallbacks()
    {
        var sampler = new TimerPresentationSampler();
        var item = Item(new(TimerDirection.CountUp, Now, 1000));
        Assert.True(sampler.Synchronize([item], Now, TimeSpan.FromSeconds(10)).Accepted);
        Assert.Equal(3000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(12))).ValueMilliseconds);
        item = item with { Item = item.Item with { Fields = item.Item.Fields with { Timer = item.Item.Fields.Timer! with { } } } };
        Assert.True(sampler.Synchronize([item], Now.AddHours(5), TimeSpan.FromSeconds(13)).Accepted);
        Assert.Equal(5000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(14))).ValueMilliseconds);
        Assert.True(sampler.Synchronize([item], Now.AddHours(-5), TimeSpan.FromSeconds(11)).Accepted);
        Assert.Equal(5000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(12))).ValueMilliseconds);
        Assert.Equal(6000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(15))).ValueMilliseconds);
    }

    [Fact]
    public void PauseResumeAndProviderAdjustmentRequireNewBasis()
    {
        var sampler = new TimerPresentationSampler();
        var paused = Item(new(TimerDirection.CountDown, Now.AddHours(-1), 5000, IsPaused: true));
        Assert.True(sampler.Synchronize([paused], Now, TimeSpan.FromSeconds(10)).Accepted);
        var frozen = Assert.Single(sampler.Sample(TimeSpan.FromSeconds(30)));
        Assert.Equal(5000, frozen.ValueMilliseconds);
        Assert.True(frozen.IsPaused);
        Assert.False(frozen.IsAdvancing);
        var resumed = Item(new(TimerDirection.CountDown, Now.AddSeconds(20), 5000));
        Assert.True(sampler.Synchronize([resumed], Now.AddSeconds(20), TimeSpan.FromSeconds(30)).Accepted);
        Assert.Equal(4000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(31))).ValueMilliseconds);
        var adjusted = Item(new(TimerDirection.CountDown, Now.AddSeconds(21), 12000));
        Assert.True(sampler.Synchronize([adjusted], Now.AddSeconds(21), TimeSpan.FromSeconds(31)).Accepted);
        Assert.Equal(11000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(32))).ValueMilliseconds);
    }

    [Theory]
    [InlineData(false, 0, "00:00", false, false)]
    [InlineData(true, -1250, "超时 00:01", true, true)]
    public void CountdownCrossingZeroFollowsExplicitOvertimePolicy(bool overtime, double value, string text,
        bool isOvertime, bool advancing)
    {
        var sampler = new TimerPresentationSampler();
        Assert.True(sampler.Synchronize([Item(new(TimerDirection.CountDown, Now, 1000, ShowOvertime: overtime))],
            Now, TimeSpan.Zero).Accepted);
        var reading = Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(2250)));
        Assert.Equal(value, reading.ValueMilliseconds);
        Assert.Equal(text, reading.Text);
        Assert.Equal(isOvertime, reading.IsOvertime);
        Assert.Equal(advancing, reading.IsAdvancing);
    }

    [Theory]
    [InlineData(TimerDirection.CountUp, 2500, 0.25)]
    [InlineData(TimerDirection.CountDown, 2500, 0.75)]
    [InlineData(TimerDirection.CountUp, 15000, 1)]
    [InlineData(TimerDirection.CountDown, 15000, 0)]
    [InlineData(TimerDirection.CountDown, -1000, 1)]
    public void TimerRingOnlyUsesExplicitDurationAndClampsWithoutEndingTimer(TimerDirection direction, double value, double fraction)
    {
        var sampler = new TimerPresentationSampler();
        Assert.True(sampler.Synchronize([Item(new(direction, Now, value, ShowOvertime: true, ProgressDurationMilliseconds: 10000))],
            Now, TimeSpan.Zero).Accepted);
        var reading = Assert.Single(sampler.Sample(TimeSpan.Zero));
        Assert.Equal(fraction, reading.ProgressFraction);
        Assert.True(reading.IsAdvancing);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("presence")]
    [InlineData("owner")]
    [InlineData("screen")]
    public void SessionPresenceAndOwnerReplacementNeverReuseOldTimeAnchor(string change)
    {
        var sampler = new TimerPresentationSampler();
        var item = Item(new(TimerDirection.CountUp, Now, 1000));
        Assert.True(sampler.Synchronize([item], Now, TimeSpan.FromSeconds(10)).Accepted);
        item = change switch
        {
            "session" => item with { SessionId = "new-session" },
            "presence" => item with { PresenceGeneration = 2 },
            "owner" => item with { Handle = item.Handle with { Owner = Guid.NewGuid() } },
            _ => item with { Handle = item.Handle with { ScreenId = "other-screen" } }
        };
        Assert.True(sampler.Synchronize([item], Now.AddSeconds(100), TimeSpan.FromSeconds(20)).Accepted);
        Assert.Equal(102000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(21))).ValueMilliseconds);
    }

    [Fact]
    public void ChangingOnlyInteractionHandleDoesNotReanchorSameSessionAndPresence()
    {
        var sampler = new TimerPresentationSampler();
        var item = Item(new(TimerDirection.CountUp, Now, 1000));
        Assert.True(sampler.Synchronize([item], Now, TimeSpan.FromSeconds(10)).Accepted);
        Assert.True(sampler.Synchronize([item with { Handle = item.Handle with { Generation = 2 } }],
            Now.AddHours(1), TimeSpan.FromSeconds(20)).Accepted);
        Assert.Equal(12000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(21))).ValueMilliseconds);
    }

    [Fact]
    public void HiddenRemovedAndNonTimerPresentationsReleaseSamplesAndClearResetsClock()
    {
        var sampler = new TimerPresentationSampler();
        var item = Item(new(TimerDirection.CountUp, Now, 1000));
        Assert.True(sampler.Synchronize([item], Now, TimeSpan.FromSeconds(100)).Accepted);
        var snapshot = sampler.Sample(TimeSpan.FromSeconds(101));
        Assert.True(sampler.Synchronize([], Now, TimeSpan.FromSeconds(102)).Accepted);
        Assert.Empty(sampler.Sample(TimeSpan.FromSeconds(103)));
        Assert.Equal(2000, Assert.Single(snapshot).ValueMilliseconds);
        Assert.True(sampler.Synchronize([item], Now.AddSeconds(20), TimeSpan.FromSeconds(104)).Accepted);
        Assert.Equal(21000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(104))).ValueMilliseconds);
        Assert.True(sampler.Synchronize([item with { Presentation = new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small)) }],
            Now, TimeSpan.FromSeconds(105)).Accepted);
        Assert.Empty(sampler.Sample(TimeSpan.FromSeconds(106)));
        sampler.Clear();
        Assert.True(sampler.Synchronize([item], Now, TimeSpan.Zero).Accepted);
        Assert.Equal(2000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(1))).ValueMilliseconds);
        sampler.Clear();
        Assert.Empty(sampler.Sample(TimeSpan.Zero));
    }

    [Fact]
    public void FullBudgetIsAcceptedAndOverflowOrDuplicatesRejectAtomically()
    {
        var sampler = new TimerPresentationSampler();
        var items = Enumerable.Range(0, 2048).Select(i => Item(new(TimerDirection.CountUp, Now, 0), "i" + i)).ToArray();
        Assert.True(sampler.Synchronize(items, Now, TimeSpan.Zero).Accepted);
        Assert.Equal(2048, sampler.Sample(TimeSpan.FromSeconds(1)).Count);
        Assert.False(sampler.Synchronize(items.Append(Item(new(TimerDirection.CountUp, Now, 0), "overflow")).ToArray(),
            Now, TimeSpan.FromSeconds(100)).Accepted);
        Assert.False(sampler.Synchronize([items[0], items[0]], Now, TimeSpan.FromSeconds(100)).Accepted);
        Assert.False(sampler.Synchronize([items[0], items[0] with { PresenceGeneration = 2 }], Now, TimeSpan.FromSeconds(100)).Accepted);
        var unchanged = sampler.Sample(TimeSpan.FromSeconds(2));
        Assert.Equal(2048, unchanged.Count);
        Assert.All(unchanged, reading => Assert.Equal(2000, reading.ValueMilliseconds));
    }

    [Fact]
    public void InvalidTimesAndMalformedBasisCannotPoisonLastValidSamples()
    {
        var sampler = new TimerPresentationSampler();
        var item = Item(new(TimerDirection.CountUp, Now, 1000));
        Assert.True(sampler.Synchronize([item], Now, TimeSpan.Zero).Accepted);
        Assert.False(sampler.Synchronize([item], Now, TimeSpan.FromTicks(-1)).Accepted);
        Assert.False(sampler.Synchronize(null!, Now, TimeSpan.Zero).Accepted);
        Assert.False(sampler.Synchronize([null!], Now, TimeSpan.Zero).Accepted);
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Sample(TimeSpan.FromTicks(-1)));
        foreach (var invalid in new TimerBasis[]
        {
            new(TimerDirection.CountUp, Now, double.NaN), new(TimerDirection.CountUp, Now, double.PositiveInfinity),
            new(TimerDirection.CountUp, Now, TimeSpan.MaxValue.TotalMilliseconds + 1), new(TimerDirection.CountUp, Now, -1),
            new(TimerDirection.CountDown, Now, -1), new((TimerDirection)99, Now, 1), new(TimerDirection.CountUp, default, 1),
            new(TimerDirection.CountUp, Now, 1, ProgressDurationMilliseconds: 0),
            new(TimerDirection.CountUp, Now, 1, ProgressDurationMilliseconds: double.NaN),
            new(TimerDirection.CountUp, Now, 1, ProgressDurationMilliseconds: double.PositiveInfinity),
            new(TimerDirection.CountUp, Now, 1, ProgressDurationMilliseconds: TimeSpan.MaxValue.TotalMilliseconds + 1)
        })
            Assert.False(sampler.Synchronize([Item(invalid)], Now, TimeSpan.FromSeconds(100)).Accepted);
        Assert.Equal(2000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(1))).ValueMilliseconds);
    }

    [Theory]
    [InlineData(TimerDirection.CountUp)]
    [InlineData(TimerDirection.CountDown)]
    public void ExtremeElapsedTimeSaturatesFiniteReadingsAndStopsUnchangingDriver(TimerDirection direction)
    {
        var sampler = new TimerPresentationSampler();
        var value = direction == TimerDirection.CountUp ? TimeSpan.MaxValue.TotalMilliseconds : -TimeSpan.MaxValue.TotalMilliseconds;
        var item = Item(new(direction, DateTimeOffset.MinValue.AddTicks(1), value, ShowOvertime: true,
            ProgressDurationMilliseconds: double.Epsilon));
        Assert.True(sampler.Synchronize([item], DateTimeOffset.MaxValue, TimeSpan.Zero).Accepted);
        var reading = Assert.Single(sampler.Sample(TimeSpan.MaxValue));
        Assert.Equal(value, reading.ValueMilliseconds);
        Assert.False(reading.IsAdvancing);
        Assert.Equal(1, reading.ProgressFraction);
        Assert.DoesNotContain("-", reading.Text);
        Assert.InRange(reading.Text.Length, 8, 32);
        Assert.Throws<NotSupportedException>(() => ((IList<TimerDisplayReading>)sampler.Sample(TimeSpan.MaxValue)).Clear());
    }

    [Fact]
    public void LongDurationsUseUntruncatedHoursAndFutureCountUpReferencesRemainNonnegative()
    {
        var sampler = new TimerPresentationSampler();
        Assert.True(sampler.Synchronize([Item(new(TimerDirection.CountUp, Now, 445506999, IsPaused: true))],
            Now, TimeSpan.Zero).Accepted);
        Assert.Equal("123:45:06", Assert.Single(sampler.Sample(TimeSpan.Zero)).Text);
        Assert.True(sampler.Synchronize([Item(new(TimerDirection.CountUp, Now.AddSeconds(10), 0))], Now, TimeSpan.Zero).Accepted);
        var waiting = Assert.Single(sampler.Sample(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, waiting.ValueMilliseconds);
        Assert.True(waiting.IsAdvancing);
        Assert.Equal(1000, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(11))).ValueMilliseconds);
    }

    private static HostItemPresentation Item(TimerBasis basis, string id = "timer", string session = "session", long presence = 1)
    {
        var presentation = new ItemPresentation(PresetTemplate.Timer, ContentFields.Timer, new(WidthTier.Medium));
        var structure = new ItemStructureDeclaration("timer", true, presentation);
        var identity = new DynamicItemIdentity("app", "main", "island", id);
        return new(new(Owner, "screen", identity, 1), session, DynamicContentKind.LiveIsland,
            structure, new(id, "timer", ["activity"], new(Timer: basis)), false, presentation, true, presence);
    }
}

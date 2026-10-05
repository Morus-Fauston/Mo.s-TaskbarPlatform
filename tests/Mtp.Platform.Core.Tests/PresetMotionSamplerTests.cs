using Mtp.Contracts;
using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class PresetMotionSamplerTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly TaskbarItemKey Key = new(new("app", "main", "presets"), "item", 1);

    [Fact]
    public void DeterminateProgressRetargetsFromCurrentDisplayedFractionUsingSharedCurve()
    {
        var sampler = new PresetMotionSampler();
        Assert.True(sampler.Synchronize([Item(new(ProgressMode.Determinate, 25, 100))], [], TimeSpan.Zero, false).Accepted);
        Assert.Equal(0.25, Assert.Single(sampler.Sample(TimeSpan.Zero, false)).ProgressFraction);
        Assert.True(sampler.Synchronize([Item(new(ProgressMode.Determinate, 75, 100))], [], TimeSpan.Zero, false).Accepted);
        var halfway = Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(110), false));
        Assert.Equal(0.6875, halfway.ProgressFraction);
        Assert.True(halfway.IsAnimating);
        Assert.True(sampler.Synchronize([Item(new(ProgressMode.Determinate, 25, 100))], [], TimeSpan.FromMilliseconds(110), false).Accepted);
        Assert.Equal(0.6875, Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(110), false)).ProgressFraction);
        Assert.Equal(0.3046875, Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(220), false)).ProgressFraction);
        var final = Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(330), false));
        Assert.Equal(0.25, final.ProgressFraction);
        Assert.False(final.IsAnimating);
    }

    [Fact]
    public void ScrollWaitsAtEachMeasuredEndAndReducedMotionImmediatelyReturnsToEllipsisPosition()
    {
        var sampler = new PresetMotionSampler();
        var item = Item(text: "超出固定槽位的说明", overflow: TextOverflow.Scroll);
        Assert.True(sampler.Synchronize([item], [new(Key, 148, 100)], TimeSpan.Zero, false).Accepted);
        Assert.Equal(0, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(1), false)).TextOffsetDip);
        Assert.Equal(-24, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(2), false)).TextOffsetDip);
        Assert.Equal(-48, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(3.5), false)).TextOffsetDip);
        Assert.Equal(-24, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(5), false)).TextOffsetDip);
        Assert.Equal(0, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(6), false)).TextOffsetDip);
        var reduced = Assert.Single(sampler.Sample(TimeSpan.FromSeconds(8), true));
        Assert.Equal(0, reduced.TextOffsetDip);
        Assert.False(reduced.IsAnimating);
        Assert.True(sampler.Synchronize([item], [new(Key, 50, 100)], TimeSpan.FromSeconds(8), false).Accepted);
        Assert.False(Assert.Single(sampler.Sample(TimeSpan.FromSeconds(10), false)).IsAnimating);
    }

    [Fact]
    public void BusyPhaseNeverBecomesAProgressFractionAndReducedMotionPreservesBusyMeaning()
    {
        var sampler = new PresetMotionSampler();
        Assert.True(sampler.Synchronize([Item(new(ProgressMode.Indeterminate))], [], TimeSpan.Zero, false).Accepted);
        var first = Assert.Single(sampler.Sample(TimeSpan.Zero, false));
        var next = Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(600), false));
        Assert.Null(next.ProgressFraction);
        Assert.True(next.IsIndeterminate);
        Assert.True(next.IsAnimating);
        Assert.NotEqual(first.BusyPhase, next.BusyPhase);
        var reduced = Assert.Single(sampler.Sample(TimeSpan.FromSeconds(1), true));
        Assert.True(reduced.IsIndeterminate);
        Assert.Null(reduced.ProgressFraction);
        Assert.Equal(0, reduced.BusyPhase);
        Assert.False(reduced.IsAnimating);
    }

    [Fact]
    public void SameTextRefreshKeepsScrollPhaseNewTextResetsAndLongSamplingRetainsOnlyCurrentTracks()
    {
        var sampler = new PresetMotionSampler();
        var item = Item(text: "原始长文本", overflow: TextOverflow.Scroll);
        Assert.True(sampler.Synchronize([item], [new(Key, 148, 100)], TimeSpan.Zero, false).Accepted);
        Assert.Equal(-24, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(2), false)).TextOffsetDip);
        Assert.True(sampler.Synchronize([item], [new(Key, 148, 100)], TimeSpan.FromSeconds(2), false).Accepted);
        Assert.Equal(-36, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(2.5), false)).TextOffsetDip);
        Assert.True(sampler.Synchronize([Item(text: "替换后的新文本", overflow: TextOverflow.Scroll)], [new(Key, 148, 100)], TimeSpan.FromSeconds(3), false).Accepted);
        Assert.Equal(0, Assert.Single(sampler.Sample(TimeSpan.FromSeconds(3), false)).TextOffsetDip);
        for (int i = 0; i < 1000; i++)
        {
            var sample = Assert.Single(sampler.Sample(TimeSpan.FromDays(i + 1), false));
            Assert.InRange(sample.TextOffsetDip, -48, 0);
        }
        Assert.InRange(Assert.Single(sampler.Sample(TimeSpan.MaxValue, false)).TextOffsetDip, -48, 0);
        Assert.True(sampler.Synchronize([], [], TimeSpan.MaxValue, false).Accepted);
        Assert.Empty(sampler.Sample(TimeSpan.MaxValue, false));
    }

    [Fact]
    public void ReducedMotionAndNoAnimationSemanticFinishDeterminateTransition()
    {
        var sampler = new PresetMotionSampler();
        var initial = Item(new(ProgressMode.Determinate, 1, 4));
        Assert.True(sampler.Synchronize([initial], [], TimeSpan.Zero, false).Accepted);
        Assert.True(sampler.Synchronize([Item(new(ProgressMode.Determinate, 3, 4))], [], TimeSpan.Zero, false).Accepted);
        Assert.Equal(0.75, Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(20), true)).ProgressFraction);
        Assert.Equal(0.75, Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(30), false)).ProgressFraction);
        Assert.True(sampler.Synchronize([initial with { Structure = initial.Structure with { Animation = SemanticAnimation.None } }], [],
            TimeSpan.FromMilliseconds(30), false).Accepted);
        var instant = Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(30), false));
        Assert.Equal(0.25, instant.ProgressFraction);
        Assert.False(instant.IsAnimating);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("owner")]
    [InlineData("screen")]
    [InlineData("presence")]
    public void NewIdentityDoesNotReuseOldTransition(string change)
    {
        var sampler = new PresetMotionSampler();
        Assert.True(sampler.Synchronize([Item(new(ProgressMode.Determinate, 1, 4))], [], TimeSpan.Zero, false).Accepted);
        var item = Item(new(ProgressMode.Determinate, 3, 4));
        item = change switch
        {
            "session" => item with { SessionId = "next" },
            "owner" => item with { Handle = item.Handle with { Owner = Guid.NewGuid() } },
            "screen" => item with { Handle = item.Handle with { ScreenId = "next" } },
            _ => item with { PresenceGeneration = 2 }
        };
        Assert.True(sampler.Synchronize([item], [], TimeSpan.Zero, false).Accepted);
        Assert.Equal(0.75, Assert.Single(sampler.Sample(TimeSpan.Zero, false)).ProgressFraction);
    }

    [Fact]
    public void SameTargetRefreshAndDelayedSampleDoNotRestartOrReverseAnimation()
    {
        var sampler = new PresetMotionSampler();
        var low = Item(new(ProgressMode.Determinate, 0, 1));
        var high = Item(new(ProgressMode.Determinate, 1, 1));
        Assert.True(sampler.Synchronize([low], [], TimeSpan.Zero, false).Accepted);
        Assert.True(sampler.Synchronize([high], [], TimeSpan.Zero, false).Accepted);
        Assert.Equal(0.875, Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(110), false)).ProgressFraction);
        Assert.True(sampler.Synchronize([high], [], TimeSpan.FromMilliseconds(100), false).Accepted);
        Assert.Equal(0.875, Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(90), false)).ProgressFraction);
        Assert.Equal(1, Assert.Single(sampler.Sample(TimeSpan.FromMilliseconds(220), false)).ProgressFraction);
    }

    [Fact]
    public void MissingIndependentProgressAndTextVariantNeverInferCounterCompletion()
    {
        var sampler = new PresetMotionSampler();
        var item = Item() with
        {
            Item = Item().Item with { Fields = new(Counter: new(CounterSemantics.CurrentIndex, 4, 10)) },
            Presentation = new(PresetTemplate.Counter, ContentFields.Counter, new(WidthTier.Small))
        };
        Assert.True(sampler.Synchronize([item], [], TimeSpan.Zero, false).Accepted);
        Assert.Null(Assert.Single(sampler.Sample(TimeSpan.Zero, false)).ProgressFraction);
        var text = Item(new(ProgressMode.Determinate, 1, 4));
        text = text with { Presentation = text.Presentation with { Variant = PresetVariant.Text } };
        Assert.True(sampler.Synchronize([text], [], TimeSpan.Zero, false).Accepted);
        Assert.Null(Assert.Single(sampler.Sample(TimeSpan.Zero, false)).ProgressFraction);
    }

    [Fact]
    public void InvalidInputAndBudgetOverflowAreAtomicAndClearReleasesAllTracks()
    {
        var sampler = new PresetMotionSampler();
        var item = Item(new(ProgressMode.Determinate, 1, 4));
        var items = Enumerable.Range(0, 2048).Select(i => item with
        {
            Handle = item.Handle with { Item = item.Handle.Item with { ItemId = "i" + i } },
            Item = item.Item with { ItemId = "i" + i }
        }).ToArray();
        Assert.True(sampler.Synchronize(items, [], TimeSpan.Zero, false).Accepted);
        Assert.Equal(2048, sampler.Sample(TimeSpan.Zero, false).Count);
        Assert.False(sampler.Synchronize(items.Append(item).ToArray(), [], TimeSpan.Zero, false).Accepted);
        Assert.False(sampler.Synchronize([item, item], [], TimeSpan.Zero, false).Accepted);
        Assert.False(sampler.Synchronize([item], [new(Key, double.NaN, 1)], TimeSpan.Zero, false).Accepted);
        Assert.False(sampler.Synchronize([item], [new(Key, 1, -1)], TimeSpan.Zero, false).Accepted);
        Assert.False(sampler.Synchronize([item], [new(Key, 1000001, 1)], TimeSpan.Zero, false).Accepted);
        Assert.False(sampler.Synchronize([item], [new(Key, 1, 1), new(Key, 1, 1)], TimeSpan.Zero, false).Accepted);
        Assert.False(sampler.Synchronize([Item(new(ProgressMode.Determinate, double.NaN, 1))], [], TimeSpan.Zero, false).Accepted);
        Assert.False(sampler.Synchronize([Item(new(ProgressMode.Indeterminate, 1, 1))], [], TimeSpan.Zero, false).Accepted);
        Assert.False(sampler.Synchronize([item], [], TimeSpan.FromTicks(-1), false).Accepted);
        Assert.False(sampler.Synchronize(null!, [], TimeSpan.Zero, false).Accepted);
        Assert.False(sampler.Synchronize([null!], [], TimeSpan.Zero, false).Accepted);
        Assert.Equal(2048, sampler.Sample(TimeSpan.Zero, false).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Sample(TimeSpan.FromTicks(-1), false));
        var snapshot = sampler.Sample(TimeSpan.MaxValue, false);
        sampler.Clear(); sampler.Clear();
        Assert.Empty(sampler.Sample(TimeSpan.Zero, false));
        Assert.Equal(2048, snapshot.Count);
        Assert.True(sampler.Synchronize([item], [], TimeSpan.Zero, false).Accepted);
        Assert.Single(sampler.Sample(TimeSpan.Zero, false));
        Assert.True(sampler.Synchronize([], [], TimeSpan.Zero, false).Accepted);
        Assert.Empty(sampler.Sample(TimeSpan.Zero, false));
    }

    private static HostItemPresentation Item(ProgressReading? progress = null, string text = "准备", TextOverflow overflow = TextOverflow.Ellipsis)
    {
        var fields = progress is null ? ContentFields.Status : ContentFields.Progress | ContentFields.Status;
        var presentation = new ItemPresentation(progress is null ? PresetTemplate.Status : PresetTemplate.Composite, fields, new(WidthTier.Large));
        return new(new(Owner, "screen", new("app", "main", "presets", "item"), 1), "session", DynamicContentKind.OrdinaryItems,
            new("preset", true, presentation, Overflow: overflow), new("item", "preset", [], new(Progress: progress, Status: new(text))),
            false, presentation, true, 1);
    }
}

using Mtp.Host.Experiments;

namespace Mtp.Platform.Core.Tests;

public sealed class VisibilityProbePolicyTests
{
    [Fact]
    public void LogicalVisibilityAloneCannotMakeAnUnmeasuredProbeVisible()
    {
        var sample = new ProbeSignals(true, true, true, true, true, true, true, null, null);
        Assert.Equal(ProbeExposure.Unknown, ProbeVisibilityPolicy.Evaluate(sample));
    }

    [Fact]
    public void ContradictoryLogicalFlagsRemainUnknown()
    {
        var sample = new ProbeSignals(true, true, true, true, true, false, true, true, ProbeClip.Full);
        Assert.Equal(ProbeExposure.Unknown, ProbeVisibilityPolicy.Evaluate(sample));
    }

    [Theory]
    [InlineData(ProbeClip.Full, ProbeExposure.VisibleCandidate)]
    [InlineData(ProbeClip.Partial, ProbeExposure.Hidden)]
    [InlineData(ProbeClip.Empty, ProbeExposure.Hidden)]
    [InlineData(null, ProbeExposure.Unknown)]
    public void OnlyFullyMeasuredExposureIsAVisibleCandidate(ProbeClip? clip, ProbeExposure expected)
        => Assert.Equal(expected, ProbeVisibilityPolicy.Evaluate(new(true, true, true, true, true, true, true, true, clip)));

    [Fact]
    public void LostParentOrUnstableSnapshotCannotRestoreVisibility()
    {
        var sample = new ProbeSignals(true, true, true, true, true, true, true, true, ProbeClip.Full);
        Assert.Equal(ProbeExposure.Unknown, ProbeVisibilityPolicy.Evaluate(sample with { ParentMatches = false }));
        Assert.Equal(ProbeExposure.Unknown, ProbeVisibilityPolicy.Evaluate(sample with { Stable = false }));
        Assert.Equal(ProbeExposure.Unknown, ProbeVisibilityPolicy.Evaluate(sample with { ProbeExists = false }));
        Assert.Equal(ProbeExposure.Hidden, ProbeVisibilityPolicy.Evaluate(sample with { FullyOnScreen = false }));
    }
}

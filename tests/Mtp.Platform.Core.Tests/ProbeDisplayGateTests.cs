using Mtp.Host.Experiments;

namespace Mtp.Platform.Core.Tests;

public sealed class ProbeDisplayGateTests
{
    [Fact]
    public void ObservationModeNeverEnablesComponentsEvenWithAVisibleCandidate()
    {
        var gate = new ProbeDisplayGate();
        var state = gate.Update("a", ProbeExposure.VisibleCandidate, true, false, TimeSpan.Zero);
        Assert.False(state.Show);
        Assert.True(state.UserWantsVisible);
    }

    [Fact]
    public void LossOnlySuppressesThatScreenAndRecoveryUsesTheCurrentPreference()
    {
        var gate = new ProbeDisplayGate();
        Assert.True(gate.Update("a", ProbeExposure.VisibleCandidate, true, true, TimeSpan.Zero).Show);
        var unavailable = gate.Update("a", ProbeExposure.Unknown, true, true, TimeSpan.Zero);
        Assert.False(unavailable.Show);
        Assert.True(unavailable.UserWantsVisible);
        Assert.True(gate.Update("b", ProbeExposure.VisibleCandidate, true, true, TimeSpan.Zero).Show);
        Assert.True(gate.Update("a", ProbeExposure.VisibleCandidate, true, true, TimeSpan.Zero).Show);
        Assert.False(gate.Update("a", ProbeExposure.VisibleCandidate, false, true, TimeSpan.Zero).Show);
        Assert.False(gate.Update("a", ProbeExposure.Hidden, true, true, TimeSpan.Zero).Show);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-1)]
    public void StaleAndFutureSamplesCannotRestoreAComponent(int milliseconds)
        => Assert.False(new ProbeDisplayGate().Update("a", ProbeExposure.VisibleCandidate, true, true,
            TimeSpan.FromMilliseconds(milliseconds)).Show);
}

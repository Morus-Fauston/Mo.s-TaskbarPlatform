using TaskbarIslandLab.Logic;

namespace TaskbarIslandLab.Tests;

public class MeasurementTests
{
    [Fact]
    public void CpuUsesWallTimeAndReportsCoreAndMachinePercentSeparately()
    {
        var cost = CpuCost.Calculate(0.5, 2, 8, 20);
        Assert.Equal(25, cost.SingleCorePercent);
        Assert.Equal(3.125, cost.MachinePercent);
        Assert.Equal(25, cost.MillisecondsPerUpdate);
        Assert.Null(CpuCost.Calculate(0.5, 2, 8, 0).MillisecondsPerUpdate);
    }

    [Fact]
    public void TimelineSeparatesWarmupIdleVisibleHiddenAndRestore()
    {
        var options = new LabOptions { VisibleSeconds = 600, HiddenSeconds = 20 };
        Assert.Equal("warmup", MeasurementPhase.At(0, options).Name);
        Assert.Equal("idle", MeasurementPhase.At(5, options).Name);
        Assert.Equal("visible", MeasurementPhase.At(15, options).Name);
        Assert.Equal("hidden-data-updating", MeasurementPhase.At(615, options).Name);
        Assert.Equal("restored", MeasurementPhase.At(635, options).Name);
        Assert.Equal("finished", MeasurementPhase.At(645, options).Name);
        var hidden = MeasurementPhase.At(616, options);
        Assert.True(hidden.Update);
        Assert.False(hidden.Visible);
    }
}

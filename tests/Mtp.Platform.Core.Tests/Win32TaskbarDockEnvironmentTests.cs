using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class Win32TaskbarDockEnvironmentTests
{
    [Fact]
    public void ExpiredProbeResultIsDiscardedAndWorkAreaRemainsAvailable()
    {
        var environment = new Win32TaskbarDockEnvironment(new ExpiredClock());
        var result = environment.Capture(null);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Geometry);
        Assert.Null(result.Value.TaskbarIdentity);
        Assert.Equal("dock_probe_timeout", result.Value.Error?.Code);
        Assert.Equal(TaskbarVisibility.Allowed, result.Value.Visibility);
        Assert.True(result.Value.DisplayBounds.Contains(result.Value.WorkArea));
    }

    private sealed class ExpiredClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp += 1000;
    }

    [Fact]
    public void RealDisplayEnumerationAndSimulatedTaskbarFailureKeepAValidWorkArea()
    {
        var environment = new Win32TaskbarDockEnvironment { SimulateUnavailable = true };
        var displays = environment.GetDisplays();
        Assert.NotEmpty(displays);
        Assert.Single(displays, item => item.IsPrimary);
        var result = environment.Capture(null);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Value!.Geometry);
        Assert.Equal("dock_taskbar_unavailable", result.Value.Error?.Code);
        Assert.True(result.Value.DisplayBounds.Contains(result.Value.WorkArea));
        Assert.True(result.Value.Dpi > 0);
    }

    [Fact]
    public void MissingDisplayReturnsExplicitPrimaryFallbackWithoutGuessingAnAnchor()
    {
        var environment = new Win32TaskbarDockEnvironment();
        var result = environment.Capture("mtp-test-missing-display");
        Assert.True(result.IsSuccess);
        Assert.Equal(environment.GetDisplays().Single(item => item.IsPrimary).Id, result.Value!.DisplayId);
        Assert.Equal("dock_display_missing", result.Value.Error?.Code);
        Assert.Null(result.Value.Geometry);
    }

    [Fact]
    public void RealExplorerReadProducesValidatedGeometryOrAnExplicitFallback()
    {
        var result = new Win32TaskbarDockEnvironment().Capture(null);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var snapshot = result.Value!;
        if (snapshot.Geometry is not null)
        {
            Assert.Equal(snapshot.DisplayId, snapshot.Geometry.DisplayId);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.TaskbarIdentity));
            var placement = TaskbarDockPlacement.Calculate(snapshot.Geometry, new(240, 32), 8);
            if (placement.IsSuccess) Assert.True(snapshot.DisplayBounds.Contains(placement.Value));
            else Assert.NotNull(placement.Error);
        }
        else Assert.True(snapshot.Error is not null || snapshot.Visibility != TaskbarVisibility.Allowed);
    }
}

using TaskbarIslandLab.Logic;

namespace TaskbarIslandLab.Tests;

public class OptionsTests
{
    [Fact]
    public void DefaultIsBoundedOwnedWindowVerification()
    {
        var options = LabOptions.Parse([]);
        Assert.Equal("owned", options.Mode);
        Assert.Equal("verify", options.Scenario);
        Assert.Null(options.Monitor);
        Assert.Equal(0, options.ParentHwnd);
        Assert.InRange(options.TimeoutSeconds, 1, 60);
    }

    [Theory]
    [InlineData("--mode explorer")]
    [InlineData("--mode explorer --scenario manual")]
    [InlineData("--mode owned --parent-hwnd 123")]
    [InlineData("--mode explorer --scenario verify --monitor display --parent-hwnd 123")]
    [InlineData("--hz 59")]
    [InlineData("--alpha NaN")]
    [InlineData("--visible-seconds -1")]
    [InlineData("--mode owned --mode explorer")]
    public void InvalidOrImplicitExplorerRequestsAreRejected(string args)
        => Assert.Throws<ArgumentException>(() => LabOptions.Parse(args.Split(' ')));

    [Fact]
    public void ExplorerRequiresExplicitModeScreenAndParent()
    {
        var options = LabOptions.Parse(["--mode", "explorer", "--scenario", "manual", "--monitor", "display", "--parent-hwnd", "0x123"]);
        Assert.Equal(291, options.ParentHwnd);
    }
}

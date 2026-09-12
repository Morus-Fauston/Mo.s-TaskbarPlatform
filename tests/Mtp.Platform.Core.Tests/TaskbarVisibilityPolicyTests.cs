namespace Mtp.Platform.Core.Tests;

public sealed class TaskbarVisibilityPolicyTests
{
    [Theory]
    [InlineData(true, true, TaskbarVisibility.FullScreen)]
    [InlineData(true, false, TaskbarVisibility.FullScreen)]
    [InlineData(true, null, TaskbarVisibility.FullScreen)]
    [InlineData(false, false, TaskbarVisibility.TaskbarHidden)]
    [InlineData(null, false, TaskbarVisibility.TaskbarHidden)]
    [InlineData(null, true, TaskbarVisibility.Unknown)]
    [InlineData(false, null, TaskbarVisibility.Unknown)]
    [InlineData(false, true, TaskbarVisibility.Allowed)]
    public void SuppressionTakesPriorityOverUnavailableEnvironment(bool? fullScreen, bool? exposed, TaskbarVisibility expected)
        => Assert.Equal(expected, TaskbarVisibilityPolicy.Evaluate(fullScreen, exposed));
}

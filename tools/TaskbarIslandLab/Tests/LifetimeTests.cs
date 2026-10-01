using TaskbarIslandLab.Logic;

namespace TaskbarIslandLab.Tests;

public class LifetimeTests
{
    [Fact]
    public void PartialInitializationRetainsFailedCleanupAndPreventsRecreationUntilRetry()
    {
        var lifetime = new IslandLifetime();
        var token = lifetime.Begin();
        var hostExists = true;
        var sourceExists = true;
        var fail = true;
        lifetime.Own("host", () => hostExists = false);
        lifetime.Own("source", () =>
        {
            if (fail) throw new IOException("release failed");
            sourceExists = false;
        });
        lifetime.Stop("initialize-failed");
        Assert.False(lifetime.Accepts(token));
        Assert.True(hostExists);
        Assert.True(sourceExists);
        Assert.Equal(LifetimeState.CleanupPending, lifetime.State);
        Assert.Single(lifetime.CleanupErrors);
        Assert.Throws<InvalidOperationException>(() => lifetime.Begin());
        fail = false;
        lifetime.Stop("retry");
        Assert.False(hostExists);
        Assert.False(sourceExists);
        Assert.Equal("initialize-failed", lifetime.StopReason);
        Assert.Equal(LifetimeState.Closed, lifetime.State);
    }

    [Fact]
    public void LostParentClosesResourcesAndRejectsOldCallbacksAfterExplicitRecreation()
    {
        var lifetime = new IslandLifetime();
        var old = lifetime.Begin();
        var resources = new List<string> { "parent", "host", "source" };
        foreach (var name in resources.ToArray())
            lifetime.Own(name, () => resources.Remove(name));
        lifetime.Ready(old);
        Assert.True(lifetime.Accepts(old));
        lifetime.Stop("parent-lost");
        Assert.Empty(resources);
        Assert.False(lifetime.Accepts(old));
        var current = lifetime.Begin();
        lifetime.Ready(current);
        Assert.False(lifetime.Accepts(old));
        Assert.True(lifetime.Accepts(current));
        lifetime.Stop("normal");
        lifetime.Stop("normal");
        Assert.Equal(LifetimeState.Closed, lifetime.State);
    }
}

using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class IslandDisplayTests
{
    [Fact]
    public void HealthyEnvironmentReplacementAndManualRetryRecreateExactlyOnce()
    {
        var adapter = new IslandBoundary();
        var display = new IslandDisplaySession(adapter);
        display.SetIntent(true);
        display.Refresh("first");
        display.Refresh("second");
        Assert.Equal(IslandDisplayState.Embedded, display.State);
        Assert.Equal(2, adapter.Starts);
        display.Retry();
        Assert.Equal(IslandDisplayState.Embedded, display.State);
        Assert.Equal(3, adapter.Starts);
    }

    [Fact]
    public void ShutdownRejectsLateRecoveryEvenWhenCleanupNeedsRetry()
    {
        var adapter = new IslandBoundary();
        var display = new IslandDisplaySession(adapter);
        display.SetIntent(true);
        display.Refresh("first");
        adapter.FailClose = true;
        Assert.False(display.Shutdown());
        display.Refresh("second");
        display.Retry();
        display.SetIntent(true);
        Assert.Equal(1, adapter.Starts);
        adapter.FailClose = false;
        Assert.True(display.Shutdown());
        Assert.Equal(IslandDisplayState.Closed, display.State);
    }
    [Fact]
    public void FailedEmbeddingIsBoundedAndNewEnvironmentRecoversWithoutChangingUserIntent()
    {
        var adapter = new IslandBoundary { Fail = true };
        var display = new IslandDisplaySession(adapter);
        display.SetIntent(true);
        display.Refresh("shell-1");
        for (var i = 0; i < 20; i++) display.Refresh("shell-1");
        Assert.Equal(1, adapter.Starts);
        Assert.Equal(IslandDisplayState.Failed, display.State);
        Assert.NotNull(display.Error);
        adapter.Fail = false;
        display.Refresh(null);
        display.Refresh("shell-2");
        Assert.Equal(IslandDisplayState.Embedded, display.State);
        Assert.Equal(2, adapter.Starts);
        Assert.Single(display.ErrorHistory);
        display.SetIntent(false);
        display.Refresh("shell-3");
        Assert.Equal(2, adapter.Starts);
        Assert.Equal(IslandDisplayState.Hidden, display.State);
    }

    internal sealed class IslandBoundary : IIslandSessionAdapter
    {
        public bool Fail;
        public bool FailClose;
        public int Starts;
        public bool IsAlive { get; private set; }
        public CoreResult<bool> Start() { Starts++; IsAlive = !Fail; return Fail ? CoreResult<bool>.Failure(new("embed_failed", "Cannot embed")) : CoreResult<bool>.Success(true); }
        public CoreResult<bool> Close() { if (FailClose) return CoreResult<bool>.Failure(new("close_failed", "Close failed")); IsAlive = false; return CoreResult<bool>.Success(true); }
    }
}

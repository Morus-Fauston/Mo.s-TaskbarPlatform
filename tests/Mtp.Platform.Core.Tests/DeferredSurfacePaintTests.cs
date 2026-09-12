using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class DeferredSurfacePaintTests
{
    [Fact]
    public void PaintWaitsForConnectedBrushAndOnlySuccessfulPaintIsAcknowledged()
    {
        Action? queued = null;
        var connected = false;
        var paints = 0;
        var fail = true;
        var surface = new DeferredSurfacePaint(action => { queued = action; return true; }, () => connected, () =>
        {
            paints++;
            return fail ? CoreResult<bool>.Failure(new("paint_failed", "GDI failed")) : CoreResult<bool>.Success(true);
        });
        surface.Schedule();
        Assert.Equal(0, paints);
        queued!();
        Assert.Equal(0, paints);
        Assert.NotNull(surface.Error);
        connected = true;
        surface.Schedule();
        queued!();
        Assert.False(surface.IsPainted);
        Assert.Equal("paint_failed", surface.Error?.Code);
        fail = false;
        surface.Schedule();
        queued!();
        Assert.True(surface.IsPainted);
        Assert.Null(surface.Error);
        Assert.Equal(2, paints);
    }

    [Fact]
    public void QueuedPaintCannotAccessAClosedWindow()
    {
        Action? queued = null;
        var paints = 0;
        var surface = new DeferredSurfacePaint(action => { queued = action; return true; }, () => true,
            () => { paints++; return CoreResult<bool>.Success(true); });
        surface.Schedule();
        surface.Cancel();
        queued!();
        Assert.Equal(0, paints);
        Assert.False(surface.IsPainted);
    }

    [Fact]
    public void QueueFailureAndPaintExceptionBecomeVisibleErrors()
    {
        var rejected = new DeferredSurfacePaint(_ => false, () => true, () => CoreResult<bool>.Success(true));
        rejected.Schedule();
        Assert.Equal("dock_surface_queue_failed", rejected.Error?.Code);
        Action? queued = null;
        var throws = new DeferredSurfacePaint(action => { queued = action; return true; }, () => true,
            () => throw new InvalidOperationException("native failure"));
        throws.Schedule();
        queued!();
        Assert.Equal("dock_surface_paint_failed", throws.Error?.Code);
        Assert.False(throws.IsPainted);
    }
}

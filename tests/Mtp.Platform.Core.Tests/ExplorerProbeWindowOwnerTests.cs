using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class ExplorerProbeWindowOwnerTests
{
    [Fact]
    public void SuccessfulCleanupRestoresNativeStateBeforeReleasingTheWindow()
    {
        var log = new List<string>();
        var operations = new RecordingOperations(log);
        var resource = new RecordingResource(log);
        var owner = Owned(operations, resource);
        owner.MarkStyleChanged((nint)123);
        owner.MarkReparented();
        var released = 0;
        var lost = 0;
        owner.Released += (_, _) => released++;
        owner.Lost += (_, _) => lost++;

        var result = owner.Cleanup();

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "hide", "parent", "style:123", "close" }, log);
        Assert.False(owner.HasResource);
        Assert.Equal(1, released);
        Assert.Equal(0, lost);
    }

    [Fact]
    public void ParentRestoreFailureKeepsOwnershipAndRemainingCleanupStateForRetry()
    {
        var log = new List<string>();
        var operations = new RecordingOperations(log) { RestoreParentResult = false };
        var resource = new RecordingResource(log);
        var owner = Owned(operations, resource);
        owner.MarkStyleChanged((nint)123);
        owner.MarkReparented();

        var failed = owner.Cleanup();

        Assert.False(failed.IsSuccess);
        Assert.Equal("explorer_probe_restore_parent_failed", failed.Error!.Code);
        Assert.True(owner.HasResource);
        Assert.True(owner.IsReparented);
        Assert.True(owner.IsStyleChanged);
        Assert.True(owner.IsCleanupPending);
        Assert.DoesNotContain("close", log);

        operations.RestoreParentResult = true;
        Assert.True(owner.Cleanup().IsSuccess);
        Assert.False(owner.HasResource);
        Assert.False(owner.IsCleanupPending);
    }

    [Fact]
    public void StyleRestoreFailureKeepsOnlyTheUnfinishedStepForRetry()
    {
        var log = new List<string>();
        var operations = new RecordingOperations(log) { RestoreStyleResult = false };
        var owner = Owned(operations, new RecordingResource(log));
        owner.MarkStyleChanged((nint)123);
        owner.MarkReparented();

        var failed = owner.Cleanup();

        Assert.False(failed.IsSuccess);
        Assert.Equal("explorer_probe_restore_style_failed", failed.Error!.Code);
        Assert.False(owner.IsReparented);
        Assert.True(owner.IsStyleChanged);

        operations.RestoreStyleResult = true;
        Assert.True(owner.Cleanup().IsSuccess);
    }

    [Fact]
    public void CloseFailureAndMissingConfirmationBothRetainOwnership()
    {
        var operations = new RecordingOperations([]);
        var throwing = new RecordingResource([]) { CloseError = new InvalidOperationException("close") };
        var owner = Owned(operations, throwing);

        var failed = owner.Cleanup();

        Assert.False(failed.IsSuccess);
        Assert.Equal("explorer_probe_close_failed", failed.Error!.Code);
        Assert.True(owner.HasResource);

        throwing.CloseError = null;
        throwing.RaiseClosedOnClose = false;
        var unconfirmed = owner.Cleanup();
        Assert.False(unconfirmed.IsSuccess);
        Assert.Equal("explorer_probe_close_unconfirmed", unconfirmed.Error!.Code);
        Assert.True(owner.HasResource);

        throwing.RaiseClosed();
        Assert.False(owner.HasResource);
    }

    [Fact]
    public void ZeroHandleWithoutClosedConfirmationRetainsOwnership()
    {
        var resource = new RecordingResource([])
        {
            Handle = 0,
            RaiseClosedOnClose = false,
        };
        var owner = Owned(new RecordingOperations([]), resource);

        var result = owner.Cleanup();

        Assert.False(result.IsSuccess);
        Assert.Equal("explorer_probe_close_unconfirmed", result.Error!.Code);
        Assert.True(owner.HasResource);

        resource.RaiseClosed();
        Assert.False(owner.HasResource);
    }

    [Fact]
    public void UnexpectedCloseRaisesLostButRequestedCloseDoesNot()
    {
        var operations = new RecordingOperations([]);
        var unexpected = new RecordingResource([]);
        var owner = Owned(operations, unexpected);
        var lost = 0;
        owner.Lost += (_, _) => lost++;

        unexpected.RaiseClosed();

        Assert.Equal(1, lost);

        var expected = new RecordingResource([]);
        owner.TakeOwnership(expected);
        Assert.True(owner.Cleanup().IsSuccess);
        Assert.Equal(1, lost);
    }

    private static ExplorerProbeWindowOwner Owned(
        RecordingOperations operations,
        RecordingResource resource)
    {
        var owner = new ExplorerProbeWindowOwner(operations);
        owner.TakeOwnership(resource);
        return owner;
    }

    private sealed class RecordingOperations(List<string> log) : IExplorerWindowOperations
    {
        public bool RestoreParentResult { get; set; } = true;

        public bool RestoreStyleResult { get; set; } = true;

        public bool IsWindow(nint handle) => true;

        public bool Hide(nint handle)
        {
            log.Add("hide");
            return true;
        }

        public bool RestoreParent(nint handle)
        {
            log.Add("parent");
            return RestoreParentResult;
        }

        public bool RestoreStyle(nint handle, nint style)
        {
            log.Add($"style:{style}");
            return RestoreStyleResult;
        }
    }

    private sealed class RecordingResource(List<string> log) : IExplorerProbeWindowResource
    {
        public event EventHandler? Closed;

        public nint Handle { get; set; } = (nint)42;

        public Exception? CloseError { get; set; }

        public bool RaiseClosedOnClose { get; set; } = true;

        public void Close()
        {
            log.Add("close");
            if (CloseError is not null)
            {
                throw CloseError;
            }

            if (RaiseClosedOnClose)
            {
                RaiseClosed();
            }
        }

        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    }
}

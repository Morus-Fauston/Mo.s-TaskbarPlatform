using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class TopLevelControlWindowOwnerTests
{
    [Fact]
    public void ConfirmedCloseReleasesTheWindow()
    {
        var resource = new RecordingResource();
        var owner = new TopLevelControlWindowOwner();
        owner.TakeOwnership(resource);

        var result = owner.Close();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, resource.CloseCount);
        Assert.False(owner.HasResource);
    }

    [Fact]
    public void CloseFailureRetainsTheWindowForRetry()
    {
        var resource = new RecordingResource { CloseError = new InvalidOperationException("close") };
        var owner = new TopLevelControlWindowOwner();
        owner.TakeOwnership(resource);

        var failed = owner.Close();

        Assert.False(failed.IsSuccess);
        Assert.Equal("top_level_control_window_close_failed", failed.Error!.Code);
        Assert.True(owner.HasResource);

        resource.CloseError = null;
        Assert.True(owner.Close().IsSuccess);
        Assert.False(owner.HasResource);
    }

    [Fact]
    public void MissingCloseConfirmationRetainsTheWindowUntilTheEventArrives()
    {
        var resource = new RecordingResource { RaiseClosedOnClose = false };
        var owner = new TopLevelControlWindowOwner();
        owner.TakeOwnership(resource);

        var result = owner.Close();

        Assert.False(result.IsSuccess);
        Assert.Equal("top_level_control_window_close_unconfirmed", result.Error!.Code);
        Assert.True(owner.HasResource);

        resource.RaiseClosed();
        Assert.False(owner.HasResource);
    }

    private sealed class RecordingResource : ITopLevelControlWindowResource
    {
        public event EventHandler? Closed;

        public Exception? CloseError { get; set; }

        public int CloseCount { get; private set; }

        public bool RaiseClosedOnClose { get; set; } = true;

        public void Close()
        {
            CloseCount++;
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

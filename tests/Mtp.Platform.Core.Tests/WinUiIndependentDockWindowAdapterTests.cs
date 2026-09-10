using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class WinUiIndependentDockWindowAdapterTests
{
    [Fact]
    public void InitializationFailureIsOwnedAndCleanedByTheAdapter()
    {
        var resource = new RecordingWindowResource { InitializeError = new InvalidOperationException("initialize") };
        var adapter = CreateAdapter(resource);

        var result = adapter.Show(Component());

        Assert.False(result.IsSuccess);
        Assert.Equal(1, resource.CloseCount);
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void ConfigurationFailureReleasesTheResourceAfterSuccessfulCleanup()
    {
        var resource = new RecordingWindowResource { ConfigureError = new InvalidOperationException("configure") };
        var adapter = CreateAdapter(resource);

        var result = adapter.Show(Component());

        Assert.False(result.IsSuccess);
        Assert.Equal("dock_window_show_failed", result.Error!.Code);
        Assert.Equal(1, resource.CloseCount);
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void ConfigurationFailureRetainsOwnershipWhenCleanupAlsoFails()
    {
        var resource = new RecordingWindowResource
        {
            ConfigureError = new InvalidOperationException("configure"),
            CloseError = new InvalidOperationException("close"),
        };
        var adapter = CreateAdapter(resource);

        var result = adapter.Show(Component());

        Assert.False(result.IsSuccess);
        Assert.True(adapter.IsOpen);
        Assert.Equal(1, resource.CloseCount);

        resource.ConfigureError = null;
        resource.CloseError = null;
        Assert.True(adapter.Close().IsSuccess);
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void ShowFailureWithFailedCleanupCanBeClosedLater()
    {
        var resource = new RecordingWindowResource
        {
            ShowError = new InvalidOperationException("show"),
            CloseError = new InvalidOperationException("close"),
        };
        var adapter = CreateAdapter(resource);

        Assert.False(adapter.Show(Component()).IsSuccess);
        Assert.True(adapter.IsOpen);

        resource.CloseError = null;
        Assert.True(adapter.Close().IsSuccess);
        Assert.False(adapter.IsOpen);
        Assert.Equal(2, resource.CloseCount);
    }

    [Fact]
    public void CloseFailureRetainsTheResourceUntilARetrySucceeds()
    {
        var resource = new RecordingWindowResource();
        var adapter = CreateAdapter(resource);
        Assert.True(adapter.Show(Component()).IsSuccess);
        resource.CloseError = new InvalidOperationException("close");

        var failed = adapter.Close();

        Assert.False(failed.IsSuccess);
        Assert.True(adapter.IsOpen);

        resource.CloseError = null;
        Assert.True(adapter.Close().IsSuccess);
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void CloseWithoutAClosedConfirmationRetainsTheResourceForRetry()
    {
        var resource = new RecordingWindowResource { RaiseClosedOnClose = false };
        var adapter = CreateAdapter(resource);
        Assert.True(adapter.Show(Component()).IsSuccess);

        var result = adapter.Close();

        Assert.False(result.IsSuccess);
        Assert.Equal("dock_window_close_unconfirmed", result.Error!.Code);
        Assert.True(adapter.IsOpen);

        resource.RaiseClosed();
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void AClosedEventFromAnOldResourceCannotReleaseTheCurrentResource()
    {
        var first = new RecordingWindowResource();
        var second = new RecordingWindowResource();
        var resources = new Queue<IIndependentDockWindowResource>(new[] { first, second });
        var adapter = new WinUiIndependentDockWindowAdapter(null, () => resources.Dequeue());
        Assert.True(adapter.Show(Component()).IsSuccess);
        Assert.True(adapter.Close().IsSuccess);
        Assert.True(adapter.Show(Component()).IsSuccess);

        first.RaiseClosed();

        Assert.True(adapter.IsOpen);
        Assert.True(adapter.Close().IsSuccess);
        Assert.Equal(1, second.CloseCount);
    }

    private static WinUiIndependentDockWindowAdapter CreateAdapter(RecordingWindowResource resource) =>
        new(null, () => resource);

    private static HostComponentDisplayModel Component() => HostComponentDisplayModel.From(
        new Component(
            new StableIdentity(new StableId("mtp")).CreateChild(new StableId("widget")),
            CapabilityState.Available),
        true);

    private sealed class RecordingWindowResource : IIndependentDockWindowResource
    {
        public event EventHandler? Closed;

        public Exception? ConfigureError { get; set; }

        public Exception? InitializeError { get; set; }

        public Exception? ShowError { get; set; }

        public Exception? CloseError { get; set; }

        public int CloseCount { get; private set; }

        public bool RaiseClosedOnClose { get; set; } = true;

        public void Initialize()
        {
            if (InitializeError is not null)
            {
                throw InitializeError;
            }
        }

        public void Configure(HostComponentDisplayModel component, Microsoft.UI.Windowing.DisplayArea? displayArea)
        {
            if (ConfigureError is not null)
            {
                throw ConfigureError;
            }
        }

        public void Show()
        {
            if (ShowError is not null)
            {
                throw ShowError;
            }
        }

        public void Close()
        {
            CloseCount++;
            if (CloseError is not null)
            {
                throw CloseError;
            }

            if (RaiseClosedOnClose)
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    }
}

using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class BrokerDisplayTests
{
    [Fact]
    public void Confirmed_reading_reaches_display_and_disconnect_preserves_reading_as_unavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mtp-broker-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(directory, "none.json")),
                new LocalComponentDisplayPreferenceStore(Path.Combine(directory, "visibility.json")));
            var store = new BrokerStateStore(["counter"]);
            store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "counter", SessionId = "session1" });
            var declared = store.Handle(new()
            {
                Kind = MessageKind.Declare,
                ApplicationId = "counter",
                SessionId = "session1",
                Declaration = new("counter", [new("main", [new("counter", [new("activate")])], [new("details", [new("activate")])])]),
                State = new(0, [new("main", "counter", "计数 7", 7)])
            });
            Assert.True(declared.Result!.Accepted);
            display.ApplyBrokerSnapshot(store.GetSnapshot("counter")!);
            var component = Assert.Single(display.CurrentComponents);
            Assert.Equal("计数 7", component.Text);
            Assert.False(component.IsVisible);
            Assert.True(display.SetVisibility(component.Identity, true).IsSuccess);
            Assert.Equal("计数 7", Assert.Single(display.CurrentComponents).Text);
            store.Handle(new() { Kind = MessageKind.Disconnected, ApplicationId = "counter", SessionId = "session1" });
            display.ApplyBrokerSnapshot(store.GetSnapshot("counter")!);
            component = Assert.Single(display.CurrentComponents);
            Assert.Equal("计数 7", component.Text);
            Assert.StartsWith("服务未连接", component.StatusLabel);
            Assert.True(component.IsVisible);
        }
        finally { Directory.Delete(directory, true); }
    }
}

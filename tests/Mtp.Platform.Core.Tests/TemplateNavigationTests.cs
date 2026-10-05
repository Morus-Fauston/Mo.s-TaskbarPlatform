using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class TemplateNavigationTests
{
    [Fact]
    public async Task ValidatedNavigationLeavesTheFixedViewAndItsGenerationOwnedByTheGroup()
    {
        var (store, entry) = Create();
        TemplateNavigationIntent? received = null;
        using var controller = new TemplateInteractionController(store, "app", entry,
            (_, _, _, _) => throw new InvalidOperationException("Navigation is not a business request"), "main",
            intent => { received = intent; return ProtocolResult.Success(); });
        var before = controller.GetSnapshot()!;
        Assert.True((await controller.ActivateAsync("open", expectedGeneration: before.Generation)).Accepted);
        Assert.NotNull(received);
        Assert.Equal(entry, received.Entry);
        Assert.Equal("details", received.Action.TargetId);
        Assert.Equal("session", received.SessionId);
        Assert.Equal("main", controller.GetSnapshot()!.Template.TemplateId);
        Assert.Equal(before.Generation, controller.GetSnapshot()!.Generation);
    }

    [Fact]
    public async Task NavigationRunsOutsideTheControllerLockAndFixedDetailsCanReturnToTheOwner()
    {
        var (store, entry) = Create(); TemplateInteractionController? controller = null;
        using (controller = new(store, "app", entry, (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), "details", intent =>
        {
            Assert.True(Task.Run(() => controller!.GetSnapshot()).Wait(TimeSpan.FromSeconds(1)), "Owner callback must not hold the view lock");
            Assert.Equal(TemplateActionKind.Back, intent.Action.Kind);
            return ProtocolResult.Success();
        }))
        {
            Assert.Equal("details", controller.GetSnapshot()!.Template.TemplateId);
            Assert.True((await controller.ActivateAsync("back")).Accepted);
        }
    }

    [Fact]
    public async Task OldNavigationAndUnavailableControlsNeverReachTheOwner()
    {
        var (store, entry) = Create(); int calls = 0; TemplateNavigationIntent? received = null;
        using var controller = new TemplateInteractionController(store, "app", entry, (_, _, _, _) => Task.FromResult(ProtocolResult.Success()),
            "main", intent => { calls++; received = intent; return ProtocolResult.Success(); });
        var snapshot = controller.GetSnapshot()!;
        await controller.ActivateAsync("open", expectedGeneration: snapshot.Generation);
        Assert.True(controller.IsCurrentNavigation(received!));
        Assert.True(store.Handle(new() { Kind = MessageKind.Disconnected, ApplicationId = "app", SessionId = "session" }).Result!.Accepted);
        Assert.False(controller.IsCurrentNavigation(received!));
        Assert.Equal("StaleTemplate", (await controller.ActivateAsync("open", expectedGeneration: snapshot.Generation)).Code);
        Assert.Equal(1, calls);
    }

    internal static (BrokerStateStore Store, TemplateEntryReference Entry) Create()
    {
        var entry = new TemplateEntryReference("group", TemplateEntryKind.TaskbarFlyout, "flyout");
        var template = new EntryTemplateDeclaration("main",
            [new("main", new("open", TemplateNodeKind.Button, Value: new(new(TemplateValueKind.Text, Text: "Details")), AccessibleName: "Details", Action: new(TemplateActionKind.OpenPanel, "details"))),
             new("details", new("back", TemplateNodeKind.Button, Value: new(new(TemplateValueKind.Text, Text: "Back")), AccessibleName: "Back", Action: new(TemplateActionKind.Back)))], [], Panels: [new("details")]);
        var declaration = new ApplicationDeclaration("app", [new("group", [new("component", [new("primary")])], [new("flyout", [new("primary")], Template: template)])]);
        var store = new BrokerStateStore(["app"]);
        Assert.True(store.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = "session" }).Result!.Accepted);
        var result = store.Handle(new() { Kind = MessageKind.Declare, ApplicationId = "app", SessionId = "session", Declaration = declaration,
            State = new(0, [], TemplateEntries: [new(entry, [])]) }).Result!;
        Assert.True(result.Accepted, result.Message);
        return (store, entry);
    }
}

using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class TemplateInteractionTests
{
    [Fact]
    public void ConfirmedFieldsRefreshWithoutChangingTheViewAndPreviewNeverWritesBusinessState()
    {
        var fixture = new Fixture();
        using var controller = fixture.Controller((_, _, _, _) => Task.FromResult(ProtocolResult.Success()));
        var initial = Assert.IsType<TemplateSurfaceSnapshot>(controller.GetSnapshot());
        Assert.Equal("main", initial.Template.TemplateId);
        Assert.Equal(20, Node(initial, "slider").Confirmed!.Number);
        Assert.True(controller.Preview("slider", new(TemplateValueKind.Number, Number: 37), initial.Generation).Accepted);
        var preview = controller.GetSnapshot()!;
        Assert.Equal(35, Node(preview, "slider").Preview!.Number);
        Assert.Equal(20, Node(preview, "slider").Confirmed!.Number);
        Assert.Equal(20, fixture.Store.GetSnapshot("app")!.State!.TemplateEntries![0].Fields.Single(field => field.FieldId == "level").Value.Number);
        fixture.Publish(1, 40);
        var refreshed = controller.GetSnapshot()!;
        Assert.Equal(initial.Generation, refreshed.Generation);
        Assert.Same(initial.Template, refreshed.Template);
        Assert.Equal(40, Node(refreshed, "slider").Confirmed!.Number);
    }

    [Fact]
    public async Task FailedSliderActionClearsBusyAndRestoresTheLatestConfirmedValue()
    {
        var fixture = new Fixture(); var response = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ActionParameter? sent = null; string? session = null;
        using var controller = fixture.Controller((_, parameter, expected, _) => { sent = parameter; session = expected; return response.Task; });
        var initial = controller.GetSnapshot()!;
        var pending = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 37), expectedGeneration: initial.Generation);
        Assert.Equal(35, sent!.Number); Assert.Equal(fixture.Session, session);
        var busy = Node(controller.GetSnapshot()!, "slider");
        Assert.True(busy.Busy); Assert.False(busy.Enabled); Assert.Equal(35, busy.Preview!.Number); Assert.Equal(20, busy.Confirmed!.Number);
        fixture.Publish(1, 25);
        response.SetResult(ProtocolResult.Reject("Denied", "denied"));
        Assert.False((await pending).Accepted);
        var final = Node(controller.GetSnapshot()!, "slider");
        Assert.False(final.Busy); Assert.True(final.Enabled); Assert.Null(final.Preview); Assert.Equal(25, final.Confirmed!.Number); Assert.Equal("Denied", final.Error);
    }

    [Fact]
    public async Task ConcurrentTargetsKeepOnlyTheLatestPendingValue()
    {
        var fixture = new Fixture();
        var sent = new List<double>();
        var first = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = fixture.Controller((_, parameter, _, _) =>
        {
            lock (sent) sent.Add(parameter.Number!.Value);
            if (parameter.Number == 35) return first.Task;
            secondSent.SetResult(); return second.Task;
        });
        var current = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 35));
        var replaced = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 60));
        var latest = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 80));
        Assert.Equal("Superseded", (await replaced).Code);
        Assert.Single(sent); Assert.False(latest.IsCompleted);
        Assert.Equal(80, Node(controller.GetSnapshot()!, "slider").Preview!.Number);
        first.SetResult(ProtocolResult.Success());
        Assert.True((await current).Accepted);
        await secondSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new double[] { 35, 80 }, sent);
        fixture.Publish(1, 80); second.SetResult(ProtocolResult.Success());
        Assert.True((await latest).Accepted);
        var final = Node(controller.GetSnapshot()!, "slider");
        Assert.False(final.Busy); Assert.Null(final.Preview); Assert.Equal(80, final.Confirmed!.Number);
    }

    [Fact]
    public async Task CancellingPendingTargetRestoresCurrentPreviewAndNeverSendsIt()
    {
        var fixture = new Fixture(); int calls = 0;
        var response = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = fixture.Controller((_, _, _, _) => { calls++; return response.Task; });
        var current = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 35));
        using var cancellation = new CancellationTokenSource();
        var pending = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 80), cancellation.Token);
        cancellation.Cancel();
        Assert.Equal("ActionCancelled", (await pending).Code);
        Assert.Equal(35, Node(controller.GetSnapshot()!, "slider").Preview!.Number);
        response.SetResult(ProtocolResult.Success()); await current;
        Assert.Equal(1, calls); Assert.False(Node(controller.GetSnapshot()!, "slider").Busy);
    }

    [Theory]
    [InlineData("ActionTimedOut")]
    [InlineData("ActionFailed")]
    public async Task SenderFailureOrAuthoritativeTimeoutRollsBackToConfirmedState(string code)
    {
        var fixture = new Fixture();
        using var controller = fixture.Controller((_, _, _, _) => code == "ActionFailed" ?
            throw new InvalidOperationException("private details") : Task.FromResult(ProtocolResult.Reject(code, "timeout")));
        var result = await controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 80));
        Assert.Equal(code, result.Code);
        var node = Node(controller.GetSnapshot()!, "slider");
        Assert.Equal(20, node.Confirmed!.Number); Assert.Null(node.Preview); Assert.False(node.Busy); Assert.Equal(code, node.Error);
    }

    [Fact]
    public async Task ToggleUsesBooleanTargetsAndSuccessRequiresPublishedConfirmation()
    {
        var fixture = new Fixture(); var sent = new List<bool>();
        using var controller = fixture.Controller((_, parameter, _, _) => { sent.Add(parameter.Boolean!.Value); return Task.FromResult(ProtocolResult.Success()); });
        Assert.True((await controller.ActivateAsync("toggle")).Accepted);
        Assert.False(Node(controller.GetSnapshot()!, "toggle").Confirmed!.Boolean);
        fixture.Publish(1, 20, enabled: true);
        Assert.True((await controller.ActivateAsync("toggle")).Accepted);
        Assert.True((await controller.ActivateAsync("toggle", new(TemplateValueKind.Boolean, Boolean: true))).Accepted);
        Assert.Equal(new[] { true, false, true }, sent);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("panel")]
    [InlineData("dispose")]
    public async Task ReplacingViewCancelsCurrentAndPendingAndRejectsStaleEvents(string change)
    {
        var fixture = new Fixture();
        var ignoredCancellation = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = fixture.Controller((_, _, _, _) => ignoredCancellation.Task);
        long oldGeneration = controller.GetSnapshot()!.Generation;
        var current = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 35));
        var pending = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 80));
        if (change == "session") { fixture.Connect(fixture.Declaration, "replacement"); controller.GetSnapshot(); }
        if (change == "panel") Assert.True((await controller.ActivateAsync("open")).Accepted);
        if (change == "dispose") controller.Dispose();
        Assert.Equal("StaleTemplate", (await current.WaitAsync(TimeSpan.FromSeconds(2))).Code);
        Assert.Equal("StaleTemplate", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Code);
        Assert.Equal("StaleTemplate", controller.Preview("slider", new(TemplateValueKind.Number, Number: 5), oldGeneration).Code);
        Assert.Equal("StaleTemplate", (await controller.ActivateAsync("slider", expectedGeneration: oldGeneration)).Code);
        ignoredCancellation.SetResult(ProtocolResult.Reject("LateFailure", "old response"));
        if (change == "dispose") Assert.Null(controller.GetSnapshot());
        else Assert.All(controller.GetSnapshot()!.Nodes, node => { Assert.Null(node.Preview); Assert.Null(node.Error); Assert.False(node.Busy); });
    }

    [Fact]
    public async Task PanelNavigationIsLocalAndBackReturnsToMainWithANewGeneration()
    {
        var fixture = new Fixture(); int calls = 0;
        using var controller = fixture.Controller((_, _, _, _) => { calls++; return Task.FromResult(ProtocolResult.Success()); });
        long generation = controller.GetSnapshot()!.Generation;
        Assert.True((await controller.ActivateAsync("open")).Accepted);
        Assert.Equal("details", controller.GetSnapshot()!.Template.TemplateId);
        Assert.True((await controller.ActivateAsync("back")).Accepted);
        Assert.Equal("main", controller.GetSnapshot()!.Template.TemplateId);
        Assert.True(controller.GetSnapshot()!.Generation > generation); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task InvalidOrMixedTargetsNeverReachTheSender(double number)
    {
        var fixture = new Fixture(); int calls = 0;
        using var controller = fixture.Controller((_, _, _, _) => { calls++; return Task.FromResult(ProtocolResult.Success()); });
        Assert.Equal("InvalidControlValue", controller.Preview("slider", new(TemplateValueKind.Number, Number: number)).Code);
        Assert.Equal("InvalidControlValue", (await controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: number))).Code);
        Assert.Equal("InvalidControlValue", (await controller.ActivateAsync("toggle", new(TemplateValueKind.Boolean, Boolean: true, Number: 1))).Code);
        Assert.Equal("StaleTemplate", (await controller.ActivateAsync("missing")).Code);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(200, 100)]
    [InlineData(37.5, 40)]
    public async Task SliderClampsAndRoundsToTheDeclaredStep(double requested, double expected)
    {
        var fixture = new Fixture(); double? sent = null;
        using var controller = fixture.Controller((_, parameter, _, _) => { sent = parameter.Number; return Task.FromResult(ProtocolResult.Success()); });
        Assert.True((await controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: requested))).Accepted);
        Assert.Equal(expected, sent);
    }

    [Fact]
    public async Task SliderMaximumOutsideTheStepGridUsesTheLastReachableValue()
    {
        var fixture = new Fixture(sliderMaximum: 23); double? sent = null;
        using var controller = fixture.Controller((_, parameter, _, _) => { sent = parameter.Number; return Task.FromResult(ProtocolResult.Success()); });
        Assert.True((await controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 23))).Accepted);
        Assert.Equal(20, sent);
    }

    [Fact]
    public async Task DecimalStepKeepsAnAlignedMaximumDespiteFloatingPointRoundoff()
    {
        var fixture = new Fixture(sliderMaximum: 0.3, sliderStep: 0.1, initialLevel: 0.2); double? sent = null;
        using var controller = fixture.Controller((_, parameter, _, _) => { sent = parameter.Number; return Task.FromResult(ProtocolResult.Success()); });
        Assert.True((await controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 0.3))).Accepted);
        Assert.Equal(0.3, sent!.Value, precision: 8);
    }

    [Fact]
    public async Task CallerCancellationStopsTheWaitEvenIfTheSenderIgnoresItsToken()
    {
        var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var never = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = fixture.Controller((_, _, _, _) => never.Task);
        var result = controller.ActivateAsync("toggle", cancellationToken: caller.Token);
        caller.Cancel();
        Assert.Equal("ActionCancelled", (await result.WaitAsync(TimeSpan.FromSeconds(2))).Code);
        Assert.False(Node(controller.GetSnapshot()!, "toggle").Busy);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AncestorDisabledOrHiddenBlocksAllDescendantActions(bool enabled, bool visible)
    {
        var fixture = new Fixture(rootEnabled: enabled, rootVisible: visible); int calls = 0;
        using var controller = fixture.Controller((_, _, _, _) => { calls++; return Task.FromResult(ProtocolResult.Success()); });
        var slider = Node(controller.GetSnapshot()!, "slider");
        Assert.False(slider.Enabled); Assert.Equal(visible, slider.Visible);
        Assert.Equal("ControlUnavailable", controller.Preview("slider", new(TemplateValueKind.Number, Number: 50)).Code);
        Assert.Equal("ControlUnavailable", (await controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 50))).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DisconnectCancelsRequestsAndKeepsConfirmedDisplayDisabled()
    {
        var fixture = new Fixture();
        var never = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = fixture.Controller((_, _, _, _) => never.Task);
        var current = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 50));
        var pending = controller.ActivateAsync("slider", new(TemplateValueKind.Number, Number: 80));
        Assert.True(fixture.Store.Handle(fixture.Message(MessageKind.Disconnected)).Result!.Accepted);
        var snapshot = controller.GetSnapshot()!;
        Assert.Equal("StaleTemplate", (await current).Code); Assert.Equal("StaleTemplate", (await pending).Code);
        Assert.Equal(20, Node(snapshot, "slider").Confirmed!.Number);
        Assert.All(snapshot.Nodes, node => { Assert.False(node.Enabled); Assert.False(node.Busy); Assert.Null(node.Preview); });
        Assert.Equal("ControlUnavailable", (await controller.ActivateAsync("toggle")).Code);
    }

    private static TemplateNodeSnapshot Node(TemplateSurfaceSnapshot surface, string id) => surface.Nodes.Single(value => value.NodeId == id);

    private sealed class Fixture
    {
        public BrokerStateStore Store { get; } = new(["app"]);
        public string Session { get; private set; } = "session";
        public TemplateEntryReference Entry { get; } = new("group", TemplateEntryKind.Component, "component");
        public ApplicationDeclaration Declaration { get; private set; }
        private readonly double initialLevel;

        public Fixture(bool rootEnabled = true, bool rootVisible = true, double sliderMaximum = 100, double sliderStep = 5, double initialLevel = 20)
        {
            this.initialLevel = initialLevel;
            var fields = new TemplateFieldDeclaration[] { new("enabled", TemplateValueKind.Boolean), new("level", TemplateValueKind.Number), new("title", TemplateValueKind.Text) };
            var template = new EntryTemplateDeclaration("main", [new("main", new("root", TemplateNodeKind.Vertical,
                Enabled: new(new(TemplateValueKind.Boolean, Boolean: rootEnabled)), Visible: new(new(TemplateValueKind.Boolean, Boolean: rootVisible)), Children:
                [new("text", TemplateNodeKind.Text, Value: new(StateFieldId: "title")),
                new("toggle", TemplateNodeKind.Toggle, Value: new(StateFieldId: "enabled"), AccessibleName: "Enable", Action: new(TemplateActionKind.Business, "set-enabled")),
                new("slider", TemplateNodeKind.Slider, Value: new(StateFieldId: "level"), AccessibleName: "Level", Action: new(TemplateActionKind.Business, "set-level"), Minimum: 0, Maximum: sliderMaximum, Step: sliderStep),
                new("open", TemplateNodeKind.Button, Value: new(new(TemplateValueKind.Text, Text: "Details")), AccessibleName: "Details", Action: new(TemplateActionKind.OpenPanel, "details"))])),
                new("details", new("back", TemplateNodeKind.Button, Value: new(new(TemplateValueKind.Text, Text: "Back")), AccessibleName: "Back", Action: new(TemplateActionKind.Back)))], fields,
                Panels: [new("details")]);
            Declaration = new("app", [new("group", [new("component", [new("set-enabled", ActionParameterKind.Boolean), new("set-level", ActionParameterKind.Number)], Template: template)], [new("flyout", [new("activate")])])]);
            Connect(Declaration, Session);
        }

        public TemplateInteractionController Controller(TemplateActionSender sender) => new(Store, "app", Entry, sender);
        public void Connect(ApplicationDeclaration declaration, string session)
        {
            Declaration = declaration; Session = session;
            Assert.True(Store.Handle(Message(MessageKind.Welcome)).Result!.Accepted);
            var result = Store.Handle(Message(MessageKind.Declare) with { Declaration = declaration, State = State(0, initialLevel) }).Result!;
            Assert.True(result.Accepted, result.Code + ": " + result.Message);
        }
        public void Publish(long revision, double level, bool enabled = false)
        {
            var result = Store.Handle(Message(MessageKind.State) with { State = State(revision, level, enabled) }).Result!;
            Assert.True(result.Accepted, result.Code + ": " + result.Message);
        }
        public ProtocolMessage Message(MessageKind kind) => new() { Kind = kind, ApplicationId = "app", SessionId = Session };
        private ApplicationState State(long revision, double level, bool enabled = false) => new(revision, [], TemplateEntries:
            [new(Entry, [new("enabled", new(TemplateValueKind.Boolean, Boolean: enabled)), new("level", new(TemplateValueKind.Number, Number: level)), new("title", new(TemplateValueKind.Text, Text: "Confirmed"))])]);
    }
}

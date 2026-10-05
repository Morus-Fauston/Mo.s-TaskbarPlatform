using Mtp.Contracts;
using Mtp.Host;

public sealed class EventDemoProviderTests
{
    [Fact]
    public async Task Complete_declaration_and_state_cover_twelve_independent_event_channels()
    {
        var provider = new EventDemoProvider("demo");
        var snapshot = await provider.GetSnapshotAsync(default);
        var validated = new DeclarationValidator().Validate(snapshot.Declaration);
        Assert.True(validated.IsSuccess, validated.Error?.ToString());
        var events = validated.Value!.FlyoutEntries.Where(entry => entry.Kind == FlyoutKind.EventGroup).ToArray();
        Assert.Equal(12, events.Length);
        Assert.Single(events, entry => entry.ClosePolicy == EventClosePolicy.Persistent);
        Assert.Equal(24, validated.Value.ActionSlots.Count(slot => slot.Reference.EntryKind == ActionEntryKind.EventChannel));
        var states = new BrokerStateStore(["demo"]);
        states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "demo", SessionId = "session" });
        var result = states.Handle(new() { Kind = MessageKind.Declare, ApplicationId = "demo", SessionId = "session",
            Declaration = snapshot.Declaration, State = snapshot.State }).Result!;
        Assert.True(result.Accepted, result.ToString());
        Assert.Same(snapshot.State, provider.Tick());
    }

    [Fact]
    public async Task Business_success_publishes_complete_confirmation_and_failure_keeps_it()
    {
        var provider = new EventDemoProvider("demo");
        var initial = await provider.GetSnapshotAsync(default);
        var first = await provider.HandleAsync(Event("event0", "confirm"), default);
        Assert.True(first.Result.Accepted);
        Assert.NotNull(first.State);
        Assert.Equal(1, first.State.Revision);
        Assert.Equal("确认次数：1", Field(first.State, "event0", "count"));
        Assert.Equal("确认次数：0", Field(first.State, "event1", "count"));
        Assert.Equal("确认次数：0", Field(initial.State, "event0", "count"));
        Assert.Equal("操作已确认", Field(first.State, "event0", "status"));
        Assert.Equal(14, first.State.TemplateEntries!.Count);
        var failure = await provider.HandleAsync(Event("event0", "fail"), default);
        Assert.Equal("DemoEventFailure", failure.Result.Code);
        Assert.Null(failure.State);
        Assert.Same(first.State, provider.Tick());
        var second = await provider.HandleAsync(Event("event1", "confirm"), default);
        Assert.Equal(2, second.State!.Revision);
        Assert.Equal("确认次数：1", Field(second.State, "event0", "count"));
        Assert.Equal("确认次数：1", Field(second.State, "event1", "count"));
    }

    [Fact]
    public async Task Requests_use_current_revision_and_next_channel_wraps_without_growing()
    {
        var requests = new List<FlyoutRequest>();
        var provider = new EventDemoProvider("demo", (request, _) => { requests.Add(request); return Task.FromResult(ProtocolResult.Success("Queued")); });
        await provider.GetSnapshotAsync(default);
        await provider.HandleAsync(Event("event0", "confirm"), default);
        await provider.HandleAsync(Control("send"), default);
        await provider.HandleAsync(Control("persistent"), default);
        for (int index = 0; index < 11; index++) await provider.HandleAsync(Control("next"), default);
        Assert.Equal(new[] { "event0", "event1" }.Concat(Enumerable.Range(2, 10).Select(index => "event" + index)).Append("event2"),
            requests.Select(request => request.EntryId));
        Assert.All(requests, request => { Assert.Equal(FlyoutKind.EventGroup, request.Kind); Assert.Equal(1, request.StateRevision); });
        Assert.Equal(FlyoutPosition.TopRight, requests[0].Position);
        Assert.All(requests.Skip(1), request => Assert.Equal(FlyoutPosition.Default, request.Position));
    }

    [Fact]
    public async Task Rejected_next_request_does_not_advance_channel()
    {
        var requests = new List<string>();
        var provider = new EventDemoProvider("demo", (request, _) =>
        { requests.Add(request.EntryId); return Task.FromResult(requests.Count == 1 ? ProtocolResult.Reject("Full", "full") : ProtocolResult.Success()); });
        await provider.GetSnapshotAsync(default);
        Assert.False((await provider.HandleAsync(Control("next"), default)).Result.Accepted);
        Assert.True((await provider.HandleAsync(Control("next"), default)).Result.Accepted);
        Assert.True((await provider.HandleAsync(Control("next"), default)).Result.Accepted);
        Assert.Equal(new[] { "event2", "event2", "event3" }, requests);
    }

    [Fact]
    public async Task Outstanding_request_is_bounded_and_reconnect_ignores_its_late_receipt()
    {
        var receipt = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        var provider = new EventDemoProvider("demo", (request, _) => { requests.Add(request.EntryId); return receipt.Task; });
        await provider.GetSnapshotAsync(default);
        var pending = provider.HandleAsync(Control("next"), default);
        Assert.Equal("Busy", (await provider.HandleAsync(Control("next"), default)).Result.Code);
        Assert.Single(requests);
        await provider.GetSnapshotAsync(default);
        receipt.SetResult(ProtocolResult.Success("Queued"));
        Assert.Equal("StaleSession", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Result.Code);
        Assert.Equal(0, provider.Tick().Revision);
        Assert.True((await provider.HandleAsync(Control("next"), default)).Result.Accepted);
        Assert.Equal(new[] { "event2", "event2" }, requests);
    }

    [Fact]
    public async Task Cancellation_releases_pending_request_slot()
    {
        int requests = 0;
        var provider = new EventDemoProvider("demo", async (_, token) =>
        { if (++requests == 1) await Task.Delay(Timeout.Infinite, token); return ProtocolResult.Success("Queued"); });
        await provider.GetSnapshotAsync(default);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.HandleAsync(Control("send"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True((await provider.HandleAsync(Control("send"), default)).Result.Accepted);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("group")]
    [InlineData("entry")]
    [InlineData("kind")]
    [InlineData("slot")]
    [InlineData("parameter")]
    [InlineData("sequence")]
    public async Task Invalid_actions_cannot_change_state(string scenario)
    {
        var provider = new EventDemoProvider("demo");
        var initial = await provider.GetSnapshotAsync(default);
        var invocation = Event("event0", "confirm");
        invocation = scenario switch
        {
            "app" => invocation with { Slot = invocation.Slot with { ApplicationId = "other" } },
            "group" => invocation with { Slot = invocation.Slot with { FeatureGroupId = "other" } },
            "entry" => invocation with { Slot = invocation.Slot with { EntryId = "event12" } },
            "kind" => invocation with { Slot = invocation.Slot with { EntryKind = ActionEntryKind.TaskbarFlyout } },
            "slot" => invocation with { Slot = invocation.Slot with { ActionSlotId = "unknown" } },
            "parameter" => invocation with { Parameter = new(ActionParameterKind.Number, Number: 1) },
            _ => invocation with { Sequence = 0 }
        };
        Assert.False((await provider.HandleAsync(invocation, default)).Result.Accepted);
        Assert.Same(initial.State, provider.Tick());
    }

    private static ActionInvocation Event(string entry, string action) => new("request-id", 1,
        new("demo", "main", ActionEntryKind.EventChannel, entry, action), new());
    private static ActionInvocation Control(string action) => Event("controls", action) with
        { Slot = new("demo", "main", ActionEntryKind.Component, "controls", action) };
    private static string Field(ApplicationState state, string channel, string field) =>
        state.TemplateEntries!.Single(value => value.Entry.Kind == TemplateEntryKind.EventChannel && value.Entry.EntryId == channel)
            .Fields.Single(value => value.FieldId == field).Value.Text!;
}

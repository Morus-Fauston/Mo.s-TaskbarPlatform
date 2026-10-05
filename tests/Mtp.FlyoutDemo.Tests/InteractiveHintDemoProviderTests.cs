using Mtp.Contracts;
using Mtp.Host;

public sealed class InteractiveHintDemoProviderTests
{
    [Fact]
    public async Task Declaration_and_complete_state_are_accepted_and_hint_owns_its_controlled_expansion()
    {
        var provider = new InteractiveHintDemoProvider("demo");
        var snapshot = await provider.GetSnapshotAsync(default);
        var validated = new DeclarationValidator().Validate(snapshot.Declaration);
        Assert.True(validated.IsSuccess, validated.Error?.ToString());
        var hint = Assert.Single(validated.Value!.FlyoutEntries, entry => entry.Kind == FlyoutKind.InteractiveHint);
        Assert.Equal(new("details", "expanded"), hint.Expansion);
        Assert.Equal(2, validated.Value.ActionSlots.Count(slot => slot.Reference.EntryKind == ActionEntryKind.Hint));
        var states = new BrokerStateStore(["demo"]);
        states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "demo", SessionId = "session" });
        var result = states.Handle(new() { Kind = MessageKind.Declare, ApplicationId = "demo", SessionId = "session",
            Declaration = snapshot.Declaration, State = snapshot.State }).Result!;
        Assert.True(result.Accepted, result.ToString());
        Assert.Same(snapshot.State, provider.Tick());
    }

    [Fact]
    public async Task Accepted_slider_value_is_authoritatively_published_and_rejection_retains_last_confirmation()
    {
        var provider = new InteractiveHintDemoProvider("demo");
        await provider.GetSnapshotAsync(default);
        var success = await provider.HandleAsync(Level(55), default);
        Assert.True(success.Result.Accepted);
        Assert.NotNull(success.State);
        Assert.Equal(1, success.State.Revision);
        Assert.Equal(55, LevelValue(success.State));
        var rejected = await provider.HandleAsync(Level(95), default);
        Assert.Equal("DemoLevelRejected", rejected.Result.Code);
        Assert.Null(rejected.State);
        Assert.Same(success.State, provider.Tick());
        Assert.Equal("DemoFailure", (await provider.HandleAsync(Invoke(ActionEntryKind.Hint, "adjust", "fail"), default)).Result.Code);
        Assert.Same(success.State, provider.Tick());
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(37)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Malformed_slider_inputs_do_not_mutate_demo_state(double value)
    {
        var provider = new InteractiveHintDemoProvider("demo");
        var original = await provider.GetSnapshotAsync(default);
        Assert.False((await provider.HandleAsync(Level(value), default)).Result.Accepted);
        Assert.Same(original.State, provider.Tick());
    }

    [Fact]
    public async Task Component_requests_use_current_revision_and_only_declared_hint_or_group_targets()
    {
        var requests = new List<FlyoutRequest>();
        var provider = new InteractiveHintDemoProvider("demo", (request, _) => { requests.Add(request); return Task.FromResult(ProtocolResult.Success("Queued")); });
        await provider.GetSnapshotAsync(default);
        await provider.HandleAsync(Level(40), default);
        Assert.Equal("Queued", (await provider.HandleAsync(Invoke(ActionEntryKind.Component, "controls", "request"), default)).Result.Code);
        Assert.Equal("Queued", (await provider.HandleAsync(Invoke(ActionEntryKind.Component, "controls", "requestGroup"), default)).Result.Code);
        Assert.Equal(new[] { "adjust", "details" }, requests.Select(value => value.EntryId));
        Assert.Equal(new[] { FlyoutKind.InteractiveHint, FlyoutKind.TaskbarGroup }, requests.Select(value => value.Kind));
        Assert.All(requests, value => Assert.Equal(1, value.StateRevision));
        Assert.False((await provider.HandleAsync(Invoke(ActionEntryKind.TaskbarFlyout, "adjust", "fail"), default)).Result.Accepted);
        Assert.False((await provider.HandleAsync(Level(20) with { Slot = new("other", "main", ActionEntryKind.Hint, "adjust", "level") }, default)).Result.Accepted);
    }

    [Fact]
    public async Task Outstanding_request_is_bounded_and_old_session_completion_does_not_become_current()
    {
        var receipt = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        var provider = new InteractiveHintDemoProvider("demo", (_, _) => { requests++; return receipt.Task; });
        await provider.GetSnapshotAsync(default);
        var pending = provider.HandleAsync(Invoke(ActionEntryKind.Component, "controls", "request"), default);
        Assert.Equal("Busy", (await provider.HandleAsync(Invoke(ActionEntryKind.Component, "controls", "request"), default)).Result.Code);
        Assert.Equal(1, requests);
        await provider.GetSnapshotAsync(default);
        receipt.SetResult(ProtocolResult.Success("Queued"));
        Assert.Equal("StaleSession", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Result.Code);
        Assert.Equal(0, provider.Tick().Revision);
        Assert.Equal("Queued", (await provider.HandleAsync(Invoke(ActionEntryKind.Component, "controls", "request"), default)).Result.Code);
    }

    [Fact]
    public async Task Cancellation_releases_the_single_pending_request_slot()
    {
        int requests = 0;
        var provider = new InteractiveHintDemoProvider("demo", async (_, token) =>
        { if (++requests == 1) await Task.Delay(Timeout.Infinite, token); return ProtocolResult.Success("Queued"); });
        await provider.GetSnapshotAsync(default);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.HandleAsync(Invoke(ActionEntryKind.Component, "controls", "request"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("Queued", (await provider.HandleAsync(Invoke(ActionEntryKind.Component, "controls", "request"), default)).Result.Code);
    }

    private static ActionInvocation Level(double value) => Invoke(ActionEntryKind.Hint, "adjust", "level") with
        { Parameter = new(ActionParameterKind.Number, Number: value) };
    private static ActionInvocation Invoke(ActionEntryKind kind, string entry, string action) =>
        new("request-id", 1, new("demo", "main", kind, entry, action), new());
    private static double LevelValue(ApplicationState state) => state.TemplateEntries!.Single(value => value.Entry.Kind == TemplateEntryKind.Hint).Fields[0].Value.Number!.Value;
}

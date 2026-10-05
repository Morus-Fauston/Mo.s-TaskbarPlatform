using Mtp.Contracts;
using Mtp.Host;

public sealed class FlyoutDemoProviderTests
{
    [Fact]
    public async Task Hint_request_uses_registered_notice_and_failure_is_explicit()
    {
        FlyoutRequest? observed = null;
        var provider = new FlyoutDemoProvider("flyout-app", (request, _) => { observed = request; return Task.FromResult(ProtocolResult.Success("Queued")); });
        var initial = await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.True((await provider.HandleAsync(Action("hint"), CancellationToken.None)).Result.Accepted);
        Assert.Equal(FlyoutKind.ShortHint, observed!.Kind);
        Assert.Equal("notice", observed.EntryId);
        Assert.Equal(FlyoutPosition.TopLeft, observed.Position);
        Assert.Equal("DemoFailure", (await provider.HandleAsync(Action("fail", 2, "details", ActionEntryKind.TaskbarFlyout), CancellationToken.None)).Result.Code);
        Assert.Same(initial.State, provider.Tick());
    }

    [Fact]
    public async Task Real_declaration_and_complete_template_and_item_state_pass_production_validators()
    {
        var provider = new FlyoutDemoProvider("flyout-app");
        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);
        var valid = new DeclarationValidator().Validate(snapshot.Declaration);
        Assert.True(valid.IsSuccess, valid.Error?.ToString());
        var templates = new TemplateValidator().ValidateState(valid.Value!, snapshot.State.TemplateEntries);
        Assert.True(templates.IsSuccess, templates.Error?.ToString());
        var items = new DynamicContentValidator().ValidateState(Assert.Single(valid.Value!.DynamicContents),
            Assert.Single(snapshot.State.DynamicEntries!).Content, DateTimeOffset.UtcNow);
        Assert.True(items.IsSuccess, items.Error?.ToString());
        Assert.Throws<NotSupportedException>(() => ((IList<FeatureGroupDeclaration>)snapshot.Declaration.FeatureGroups!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<HostTemplate>)snapshot.Declaration.FeatureGroups![0].TaskbarFlyouts![0].Template!.Templates).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DynamicItemState>)snapshot.State.DynamicEntries![0].Content.Items).Clear());
    }

    [Fact]
    public async Task Sdk_request_uses_confirmed_revision_and_preserves_queued_receipt_without_publishing()
    {
        FlyoutRequest? observed = null;
        var receipt = ProtocolResult.Success("Queued");
        var provider = new FlyoutDemoProvider("flyout-app", (request, _) =>
        { observed = request; return Task.FromResult(receipt); });
        var initial = await provider.GetSnapshotAsync(CancellationToken.None);
        var completion = await provider.HandleAsync(Action("request"), CancellationToken.None);
        Assert.Same(receipt, completion.Result);
        Assert.Null(completion.State);
        Assert.Same(initial.State, provider.Tick());
        Assert.Equal(initial.State.Revision, observed!.StateRevision);
        Assert.Equal("details", observed.EntryId);
        Assert.Equal(FlyoutKind.TaskbarGroup, observed.Kind);
    }

    [Fact]
    public async Task At_most_one_sdk_request_waits_and_completion_releases_capacity()
    {
        var release = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var provider = new FlyoutDemoProvider("flyout-app", (_, _) => { calls++; return release.Task; });
        await provider.GetSnapshotAsync(CancellationToken.None);
        var pending = provider.HandleAsync(Action("request"), CancellationToken.None);
        Assert.False(pending.IsCompleted);
        var busy = await provider.HandleAsync(Action("request", 2), CancellationToken.None);
        Assert.Equal("Busy", busy.Result.Code);
        Assert.Equal(1, calls);
        // Business confirmation can finish without waiting for the request or mutating its revision.
        var confirmed = await provider.HandleAsync(Action("confirm", 3, "details", ActionEntryKind.TaskbarFlyout), CancellationToken.None);
        Assert.Equal("ActionSucceeded", confirmed.Result.Code);
        Assert.Null(confirmed.State);
        release.SetResult(ProtocolResult.Reject("FlyoutUnavailable", "test"));
        Assert.Equal("FlyoutUnavailable", (await pending).Result.Code);
        Assert.Equal("FlyoutUnavailable", (await provider.HandleAsync(Action("request", 4), CancellationToken.None)).Result.Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancellation_releases_request_capacity_and_never_returns_late_success()
    {
        var release = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FlyoutDemoProvider("flyout-app", (_, _) => release.Task);
        await provider.GetSnapshotAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.HandleAsync(Action("request"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        release.SetResult(ProtocolResult.Success("Queued"));
        Assert.Equal("Queued", (await provider.HandleAsync(Action("request", 2), CancellationToken.None)).Result.Code);
        Assert.Equal(0, provider.Tick().Revision);
    }

    [Fact]
    public async Task Late_previous_session_receipt_is_rejected_without_changing_new_snapshot()
    {
        var release = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var provider = new FlyoutDemoProvider("flyout-app", (_, _) => { calls++; return release.Task; });
        await provider.GetSnapshotAsync(CancellationToken.None);
        var pending = provider.HandleAsync(Action("request"), CancellationToken.None);
        var next = await provider.GetSnapshotAsync(CancellationToken.None);
        release.SetResult(ProtocolResult.Success("Queued"));
        Assert.Equal("StaleSession", (await pending).Result.Code);
        Assert.Same(next.State, provider.Tick());
        Assert.Equal(1, calls);
        Assert.Equal("Queued", (await provider.HandleAsync(Action("request", 2), CancellationToken.None)).Result.Code);
    }

    [Fact]
    public async Task Binding_wait_is_bounded_by_cancellation_and_does_not_block_business_confirmation()
    {
        var provider = new FlyoutDemoProvider("flyout-app");
        await provider.GetSnapshotAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.HandleAsync(Action("request"), cancellation.Token);
        Assert.False(pending.IsCompleted);
        Assert.True((await provider.HandleAsync(Action("confirm", 2), CancellationToken.None)).Result.Accepted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Invalid_inputs_do_not_request_or_change_the_state()
    {
        int calls = 0;
        var provider = new FlyoutDemoProvider("flyout-app", (_, _) => { calls++; return Task.FromResult(ProtocolResult.Success("Queued")); });
        Assert.Equal("SessionNotReady", (await provider.HandleAsync(Action("request"), CancellationToken.None)).Result.Code);
        var initial = await provider.GetSnapshotAsync(CancellationToken.None);
        var action = Action("request");
        foreach (var invalid in new[]
        {
            action with { Slot = action.Slot with { ApplicationId = "other" } },
            action with { Slot = action.Slot with { FeatureGroupId = "other" } },
            action with { Slot = action.Slot with { EntryId = "item" } },
            action with { Slot = action.Slot with { EntryKind = ActionEntryKind.TaskbarFlyout } },
            action with { Slot = action.Slot with { ActionSlotId = "open" } },
            action with { Parameter = new(ActionParameterKind.None, Text: "unexpected") },
            action with { Sequence = 0 }, action with { RequestId = new string('x', 257) }
        }) Assert.Equal("ActionNotAvailable", (await provider.HandleAsync(invalid, CancellationToken.None)).Result.Code);
        await provider.OnDisplayPermissionsChangedAsync(new(1, Array.Empty<EntryDisplayPermission>()), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OnDisplayPermissionsChangedAsync(
            new(2, new[] { new EntryDisplayPermission("main", "item", true) }), CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.Same(initial.State, provider.Tick());
    }

    [Fact]
    public async Task Request_exception_releases_capacity()
    {
        int calls = 0;
        var provider = new FlyoutDemoProvider("flyout-app", (_, _) =>
            ++calls == 1 ? throw new IOException("test") : Task.FromResult(ProtocolResult.Success("Queued")));
        await provider.GetSnapshotAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => provider.HandleAsync(Action("request"), CancellationToken.None));
        Assert.Equal("Queued", (await provider.HandleAsync(Action("request", 2), CancellationToken.None)).Result.Code);
    }

    private static ActionInvocation Action(string action, long sequence = 1, string entry = "controls", ActionEntryKind kind = ActionEntryKind.Component) =>
        new("request-" + sequence, sequence, new("flyout-app", "main", kind, entry, action), new());
}

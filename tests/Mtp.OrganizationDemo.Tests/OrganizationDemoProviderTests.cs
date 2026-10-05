using Mtp.Contracts;
using Mtp.Host;

public sealed class OrganizationDemoProviderTests
{
    [Fact]
    public async Task Every_emitted_state_passes_real_declaration_dynamic_and_template_validation()
    {
        var clock = new TestClock();
        var states = new List<ApplicationState>();
        var provider = new OrganizationDemoProvider("organization-app", clock, (value, _) =>
        { states.Add(value); return Task.FromResult(ProtocolResult.Success()); });
        var initial = await provider.GetSnapshotAsync(CancellationToken.None);
        states.Add(initial.State);
        var declaration = new DeclarationValidator().Validate(initial.Declaration);
        Assert.True(declaration.IsSuccess, declaration.Error?.ToString());
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        foreach (string command in new[] { "add", "add", "add", "add", "add", "update", "shared", "end", "reset" })
        {
            var completion = await provider.HandleAsync(Action(command), CancellationToken.None);
            Assert.True(completion.Result.Accepted, completion.Result.ToString());
            states.Add(completion.State!);
        }
        foreach (var state in states)
        {
            var templateResult = new TemplateValidator().ValidateState(declaration.Value!, state.TemplateEntries);
            Assert.True(templateResult.IsSuccess, templateResult.Error?.ToString());
            foreach (var entry in state.DynamicEntries!)
            {
                var schema = declaration.Value!.DynamicContents.Single(value => value.ComponentIdentity.Segments[^1].Value == entry.ComponentId);
                var result = new DynamicContentValidator().ValidateState(schema, entry.Content, clock.GetUtcNow());
                Assert.True(result.IsSuccess, result.Error?.ToString());
            }
        }
        Assert.Throws<NotSupportedException>(() => ((IList<FeatureGroupDeclaration>)initial.Declaration.FeatureGroups!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ActivityState>)Entry(provider.Tick()).Content.Activities).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DynamicItemState>)Entry(provider.Tick()).Content.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)Entry(provider.Tick()).Content.Items[0].ActivityIds).Clear());
    }

    [Fact]
    public async Task Initial_organization_has_reverse_activity_order_and_one_shared_item_without_duplicate_business_ids()
    {
        var provider = await Started(new TestClock());
        var content = Entry(provider.Tick()).Content;
        Assert.Equal(new[] { "a1", "a2", "a3" }, content.Activities.Select(activity => activity.ActivityId));
        Assert.Equal(new[] { 30, 20, 10 }, content.Activities.Select(activity => activity.Order));
        Assert.Equal(new[] { "a1-timer", "a1-status", "a2-timer", "a2-status", "a3-timer", "a3-status", "shared" }, content.Items.Select(item => item.ItemId));
        Assert.Equal(new[] { "a1", "a2" }, content.Items.Single(item => item.ItemId == "shared").ActivityIds);
        var disconnected = await provider.HandleAsync(Action("shared"), CancellationToken.None);
        Assert.Equal(content.Activities, Entry(disconnected.State!).Content.Activities);
        Assert.Equal(content.Items.Take(6).Select(item => (item.ItemId, item.StructureId, item.Fields)),
            Entry(disconnected.State!).Content.Items.Select(item => (item.ItemId, item.StructureId, item.Fields)));
        foreach (var pair in content.Items.Take(6).Zip(Entry(disconnected.State!).Content.Items))
            Assert.Equal(pair.First.ActivityIds, pair.Second.ActivityIds);
        var connected = await provider.HandleAsync(Action("shared", 2), CancellationToken.None);
        Assert.Equal(content.Items.Select(item => item.ItemId), Entry(connected.State!).Content.Items.Select(item => item.ItemId));
        Assert.Equal(content.Activities, Entry(connected.State!).Content.Activities);
    }

    [Fact]
    public async Task Display_off_retains_accepted_updates_but_denies_creation_and_restores_without_renewal()
    {
        var clock = new TestClock();
        var published = new List<ApplicationState>();
        var provider = new OrganizationDemoProvider("organization-app", clock, (state, _) =>
        { published.Add(state); return Task.FromResult(ProtocolResult.Success()); });
        var initial = await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.Empty(Entry(initial.State).Content.Activities);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        var active = provider.Tick();
        Assert.Equal(3, Entry(active).Content.Activities.Count);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, false), CancellationToken.None);
        var denied = await provider.HandleAsync(Action("add"), CancellationToken.None);
        Assert.Equal("DisplayNotAllowed", denied.Result.Code);
        Assert.Null(denied.State);
        var updated = await provider.HandleAsync(Action("update", 2), CancellationToken.None);
        Assert.True(updated.Result.Accepted);
        Assert.Equal(Entry(active).Content.Activities, Entry(updated.State!).Content.Activities);
        clock.Advance(TimeSpan.FromSeconds(30));
        await provider.OnDisplayPermissionsChangedAsync(Permission(3, true), CancellationToken.None);
        Assert.Equal(Entry(active).Content.Activities, Entry(provider.Tick()).Content.Activities);
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public async Task New_activity_budget_is_eight_and_explicit_reset_allocates_new_ids()
    {
        var provider = await Started(new TestClock());
        for (int i = 0; i < 5; i++) Assert.True((await provider.HandleAsync(Action("add", i + 1), CancellationToken.None)).Result.Accepted);
        var full = provider.Tick();
        Assert.Equal(8, Entry(full).Content.Activities.Count);
        var rejected = await provider.HandleAsync(Action("add", 6), CancellationToken.None);
        Assert.Equal("DemoActivityLimit", rejected.Result.Code);
        Assert.Same(full, provider.Tick());
        var ended = await provider.HandleAsync(Action("end", 7), CancellationToken.None);
        Assert.Equal("a8", Assert.Single(Entry(ended.State!).Content.Activities, activity => activity.Ended).ActivityId);
        Assert.DoesNotContain(Entry(ended.State!).Content.Items, item => item.ActivityIds.Contains("a8"));
        Assert.True((await provider.HandleAsync(Action("add", 8), CancellationToken.None)).Result.Accepted);
        Assert.Equal(8, Entry(provider.Tick()).Content.Activities.Count);
        var oldIds = Entry(provider.Tick()).Content.Activities.Select(activity => activity.ActivityId).ToHashSet();
        var reset = await provider.HandleAsync(Action("reset", 9), CancellationToken.None);
        Assert.Equal(3, Entry(reset.State!).Content.Activities.Count);
        Assert.DoesNotContain(Entry(reset.State!).Content.Activities, activity => oldIds.Contains(activity.ActivityId));
        Assert.Equal(new[] { 30, 20, 10 }, Entry(reset.State!).Content.Activities.Select(activity => activity.Order));
    }

    [Fact]
    public async Task Reconnect_preserves_valid_basis_and_expiry_then_never_revives_expired_business()
    {
        var clock = new TestClock();
        var provider = await Started(clock);
        var first = Entry(provider.Tick()).Content;
        clock.Advance(TimeSpan.FromSeconds(40));
        var reconnect = await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.Empty(Entry(reconnect.State).Content.Activities);
        Assert.Equal("ActivityNotConfirmed", (await provider.HandleAsync(Action("update"), CancellationToken.None)).Result.Code);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, false), CancellationToken.None);
        Assert.Empty(Entry(provider.Tick()).Content.Activities);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, true), CancellationToken.None);
        Assert.Equal(first.Activities, Entry(provider.Tick()).Content.Activities);
        Assert.Equal(first.Items.Select(item => item.Fields.Timer), Entry(provider.Tick()).Content.Items.Select(item => item.Fields.Timer));
        clock.Advance(TimeSpan.FromMinutes(6));
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.Empty(Entry(provider.Tick()).Content.Activities);
        Assert.Empty(Entry(provider.Tick()).Content.Items);
        Assert.Equal("NoActiveActivity", (await provider.HandleAsync(Action("update", 2), CancellationToken.None)).Result.Code);
        var explicitNew = await provider.HandleAsync(Action("add", 3), CancellationToken.None);
        Assert.True(explicitNew.Result.Accepted);
        Assert.Equal("a4", Assert.Single(Entry(explicitNew.State!).Content.Activities).ActivityId);
    }

    [Fact]
    public async Task Ended_business_stays_ended_when_permissions_return_and_after_reconnect()
    {
        var provider = await Started(new TestClock());
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, false), CancellationToken.None);
        Assert.Equal("DisplayNotAllowed", (await provider.HandleAsync(Action("reset"), CancellationToken.None)).Result.Code);
        for (int i = 0; i < 3; i++) Assert.True((await provider.HandleAsync(Action("end", i + 1), CancellationToken.None)).Result.Accepted);
        Assert.Empty(Entry(provider.Tick()).Content.Items);
        Assert.Equal("AlreadyEnded", (await provider.HandleAsync(Action("end", 4), CancellationToken.None)).Result.Code);
        await provider.OnDisplayPermissionsChangedAsync(Permission(3, true), CancellationToken.None);
        Assert.Empty(Entry(provider.Tick()).Content.Items);
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.Empty(Entry(provider.Tick()).Content.Items);
        Assert.DoesNotContain(Entry(provider.Tick()).Content.Activities, activity => !activity.Ended);
    }

    [Fact]
    public async Task Tick_and_duplicate_permissions_do_not_publish_or_extend_activities()
    {
        var clock = new TestClock();
        int calls = 0;
        var provider = new OrganizationDemoProvider("organization-app", clock, (_, _) => { calls++; return Task.FromResult(ProtocolResult.Success()); });
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, false), CancellationToken.None);
        Assert.Equal(0, calls);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, true), CancellationToken.None);
        var retained = provider.Tick();
        clock.Advance(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 100; i++) Assert.Same(retained, provider.Tick());
        await provider.OnDisplayPermissionsChangedAsync(Permission(3, true), CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, false), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Same(retained, provider.Tick());
    }

    [Fact]
    public async Task Publication_must_be_accepted_before_business_actions_can_run()
    {
        var release = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new OrganizationDemoProvider("organization-app", new TestClock(), (_, _) => release.Task);
        await provider.GetSnapshotAsync(CancellationToken.None);
        var pending = provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Empty(Entry(provider.Tick()).Content.Activities);
        Assert.Equal("ActivityNotConfirmed", (await provider.HandleAsync(Action("update"), CancellationToken.None)).Result.Code);
        release.SetResult(ProtocolResult.Reject("Unavailable", "test"));
        await pending;
        Assert.Empty(Entry(provider.Tick()).Content.Activities);
        Assert.Equal("ActivityNotConfirmed", (await provider.HandleAsync(Action("update", 2), CancellationToken.None)).Result.Code);
    }

    [Fact]
    public async Task Activity_rejections_are_not_promoted_to_confirmed_business()
    {
        int calls = 0;
        var provider = new OrganizationDemoProvider("organization-app", new TestClock(), (state, _) =>
            Task.FromResult(++calls == 1 ? new ProtocolResult(true, "AcceptedWithActivityRejections", "test",
                ActivityRejections: Entry(state).Content.Activities.Select(activity =>
                    new ActivityAdmissionRejection("organization-app", "main", "islands", activity.ActivityId, "DisplayNotAllowed")).ToArray())
                : ProtocolResult.Success()));
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        Assert.Empty(Entry(provider.Tick()).Content.Activities);
        Assert.Equal("ActivityNotConfirmed", (await provider.HandleAsync(Action("update"), CancellationToken.None)).Result.Code);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, true), CancellationToken.None);
        Assert.Equal(3, Entry(provider.Tick()).Content.Activities.Count);
    }

    [Fact]
    public async Task Late_old_epoch_publication_cannot_overwrite_new_epoch_state()
    {
        var oldReceipt = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newReceipt = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var provider = new OrganizationDemoProvider("organization-app", new TestClock(), (_, _) => ++calls == 1 ? oldReceipt.Task : newReceipt.Task);
        await provider.GetSnapshotAsync(CancellationToken.None);
        var old = provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        await provider.GetSnapshotAsync(CancellationToken.None);
        var current = provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        newReceipt.SetResult(ProtocolResult.Success());
        await current;
        var retained = provider.Tick();
        Assert.Equal(new[] { "a4", "a5", "a6" }, Entry(retained).Content.Activities.Select(activity => activity.ActivityId));
        oldReceipt.SetResult(ProtocolResult.Success());
        await old;
        Assert.Same(retained, provider.Tick());
        Assert.True((await provider.HandleAsync(Action("update"), CancellationToken.None)).Result.Accepted);
    }

    [Fact]
    public async Task Cancelled_binding_or_publication_does_not_create_business_and_releases_admission()
    {
        var unbound = new OrganizationDemoProvider("organization-app", new TestClock());
        await unbound.GetSnapshotAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var wait = unbound.OnDisplayPermissionsChangedAsync(Permission(1, true), cancellation.Token);
        Assert.False(wait.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Empty(Entry(unbound.Tick()).Content.Activities);

        var release = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var provider = new OrganizationDemoProvider("organization-app", new TestClock(), (_, _) =>
            ++calls == 1 ? release.Task : Task.FromResult(ProtocolResult.Success()));
        await provider.GetSnapshotAsync(CancellationToken.None);
        using var cancelledPublication = new CancellationTokenSource();
        var pending = provider.OnDisplayPermissionsChangedAsync(Permission(1, true), cancelledPublication.Token);
        cancelledPublication.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        release.SetResult(ProtocolResult.Success());
        Assert.Empty(Entry(provider.Tick()).Content.Activities);
        await provider.OnDisplayPermissionsChangedAsync(Permission(2, true), CancellationToken.None);
        Assert.Equal(3, Entry(provider.Tick()).Content.Activities.Count);
    }

    [Fact]
    public async Task Malformed_actions_permissions_and_cancelled_actions_leave_state_unchanged()
    {
        var provider = await Started(new TestClock());
        var retained = provider.Tick();
        var action = Action("update");
        foreach (var invalid in new[]
        {
            action with { Slot = action.Slot with { ApplicationId = "other" } },
            action with { Slot = action.Slot with { FeatureGroupId = "other" } },
            action with { Slot = action.Slot with { EntryId = "other" } },
            action with { Slot = action.Slot with { ActionSlotId = "unknown" } },
            action with { Sequence = 0 }, action with { RequestId = new string('x', 257) },
            action with { Parameter = new(ActionParameterKind.None, Text: "unexpected") }
        }) Assert.False((await provider.HandleAsync(invalid, CancellationToken.None)).Result.Accepted);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OnDisplayPermissionsChangedAsync(new(2,
            [new("main", "islands", true), new("main", "islands", true), new("main", "islands", true)]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OnDisplayPermissionsChangedAsync(new(2, []), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.HandleAsync(action, cancelled.Token));
        Assert.Same(retained, provider.Tick());
    }

    private static DynamicEntryState Entry(ApplicationState state) => state.DynamicEntries!.Single(entry => entry.ComponentId == "islands");
    private static ActionInvocation Action(string command, long sequence = 1) => new("request-" + sequence, sequence,
        new("organization-app", "main", ActionEntryKind.Component, "controls", command), new());
    private static DisplayPermissionSnapshot Permission(long revision, bool allowed) => new(revision,
        [new("main", "islands", allowed), new("main", "together", true), new("main", "separate", true)]);
    private static async Task<OrganizationDemoProvider> Started(TestClock clock)
    {
        var provider = new OrganizationDemoProvider("organization-app", clock, (_, _) => Task.FromResult(ProtocolResult.Success()));
        await provider.GetSnapshotAsync(CancellationToken.None);
        await provider.OnDisplayPermissionsChangedAsync(Permission(1, true), CancellationToken.None);
        return provider;
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}

using Mtp.Contracts;
using Mtp.Host;
using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class FlyoutRequestRouterTests
{
    [Fact]
    public void Registered_current_request_is_received_without_claiming_display()
    {
        var fixture = CreateFixture();
        var router = new FlyoutRequestRouter();
        var result = router.Handle("app", "session", Request(), fixture.Snapshot, fixture.Entries);
        Assert.True(result.Accepted);
        Assert.Equal("Received", result.Code);
        Assert.Equal("Received", router.GetLastResult("app")!.Result.Code);
    }

    [Fact]
    public void Host_can_disable_a_validated_entry_before_its_first_request()
    {
        var fixture = CreateFixture();
        var router = new FlyoutRequestRouter();
        Assert.True(router.SynchronizeDeclaration(fixture.Snapshot, fixture.Entries).Accepted);
        Assert.True(router.SetEntryEnabled(fixture.Entries[0], false).Accepted);
        Assert.Equal("EntryDisabled", router.Handle("app", "session", Request(), fixture.Snapshot, fixture.Entries).Code);
    }

    [Fact]
    public void Declaration_synchronization_preserves_host_permission_across_new_sessions()
    {
        var fixture = CreateFixture();
        var router = new FlyoutRequestRouter();
        Assert.True(router.SynchronizeDeclaration(fixture.Snapshot, fixture.Entries).Accepted);
        Assert.True(router.SetEntryEnabled(fixture.Entries[0], false).Accepted);
        Assert.Equal("EntryDisabled", router.Handle("app", "session", Request(30), fixture.Snapshot, fixture.Entries).Code);
        var next = CreateFixture(session: "new-session");
        Assert.True(router.SynchronizeDeclaration(next.Snapshot, next.Entries).Accepted);
        Assert.Equal("EntryDisabled", router.Handle("app", "new-session", Request(), next.Snapshot, next.Entries).Code);
        Assert.True(router.SetEntryEnabled(next.Entries[0], true).Accepted);
        Assert.Equal("Received", router.Handle("app", "new-session", Request(2), next.Snapshot, next.Entries).Code);
    }

    [Fact]
    public void Unconfirmed_declarations_and_foreign_entries_do_not_register_policy()
    {
        var fixture = CreateFixture();
        var foreign = CreateFixture("foreign");
        var router = new FlyoutRequestRouter();
        Assert.Equal("DeclarationRequired", router.SynchronizeDeclaration(fixture.Snapshot with { IsInteractive = false }, fixture.Entries).Code);
        Assert.Equal("UnknownEntry", router.SetEntryEnabled(fixture.Entries[0], false).Code);
        Assert.Equal("InvalidDeclaration", router.SynchronizeDeclaration(fixture.Snapshot, foreign.Entries).Code);
        Assert.Equal("UnknownEntry", router.SetEntryEnabled(fixture.Entries[0], false).Code);
    }

    [Theory]
    [InlineData("details", FlyoutKind.TaskbarGroup)]
    [InlineData("hint", FlyoutKind.ShortHint)]
    [InlineData("interactive", FlyoutKind.InteractiveHint)]
    [InlineData("event", FlyoutKind.EventGroup)]
    public void Each_declared_kind_is_admitted(string entry, FlyoutKind kind)
    {
        var fixture = CreateFixture();
        Assert.Equal("Received", new FlyoutRequestRouter().Handle("app", "session", Request(entry: entry, kind: kind), fixture.Snapshot, fixture.Entries).Code);
    }

    [Fact]
    public void Rejection_consumes_sequence_and_does_not_replace_application_state()
    {
        var fixture = CreateFixture();
        var router = new FlyoutRequestRouter();
        Assert.Equal("UnknownEntry", router.Handle("app", "session", Request(entry: "unknown"), fixture.Snapshot, fixture.Entries).Code);
        Assert.Equal("DuplicateRequest", router.Handle("app", "session", Request(), fixture.Snapshot, fixture.Entries).Code);
        Assert.Equal("Received", router.Handle("app", "session", Request(2), fixture.Snapshot, fixture.Entries).Code);
        Assert.Equal(5, fixture.Snapshot.State!.Revision);
        Assert.Equal("5", fixture.Snapshot.State.Components[0].Text);
    }

    [Fact]
    public void Stale_session_cannot_poison_current_sequence()
    {
        var fixture = CreateFixture();
        var router = new FlyoutRequestRouter();
        Assert.Equal("StaleSession", router.Handle("app", "old", Request(long.MaxValue), fixture.Snapshot, fixture.Entries).Code);
        Assert.Equal("Received", router.Handle("app", "session", Request(), fixture.Snapshot, fixture.Entries).Code);
        Assert.Equal("UnknownApplication", router.Handle("other", "session", Request(), fixture.Snapshot, fixture.Entries).Code);
    }

    [Fact]
    public void Disconnected_and_unconfirmed_applications_are_rejected()
    {
        var fixture = CreateFixture();
        var router = new FlyoutRequestRouter();
        Assert.Equal("Disconnected", router.Handle("app", "session", Request(), fixture.Snapshot with { IsConnected = false }, fixture.Entries).Code);
        Assert.Equal("DeclarationRequired", router.Handle("app", "session", Request(2), fixture.Snapshot with { IsInteractive = false }, fixture.Entries).Code);
        Assert.Equal("Received", router.Handle("app", "session", Request(3), fixture.Snapshot, fixture.Entries).Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(6)]
    public void Only_current_state_revision_is_accepted(long revision)
    {
        var fixture = CreateFixture();
        Assert.Equal("StaleState", new FlyoutRequestRouter().Handle("app", "session", Request(revision: revision), fixture.Snapshot, fixture.Entries).Code);
    }

    [Fact]
    public void Entry_type_and_controlled_preferences_cannot_be_forged()
    {
        var fixture = CreateFixture();
        var router = new FlyoutRequestRouter();
        Assert.Equal("KindMismatch", router.Handle("app", "session", Request(kind: FlyoutKind.EventGroup), fixture.Snapshot, fixture.Entries).Code);
        Assert.Equal("InvalidRequest", router.Handle("app", "session", Request(2) with { Screen = (FlyoutScreen)99 }, fixture.Snapshot, fixture.Entries).Code);
        Assert.Equal("InvalidRequest", router.Handle("app", "session", Request(3) with { Position = (FlyoutPosition)99 }, fixture.Snapshot, fixture.Entries).Code);
        Assert.Equal("InvalidRequest", router.Handle("app", "session", Request(4) with { RequestId = new string('x', 257) }, fixture.Snapshot, fixture.Entries).Code);
        Assert.Null(router.GetLastResult("app")!.RequestId);
        Assert.Equal("Received", router.Handle("app", "session", Request(5) with { Screen = FlyoutScreen.Primary }, fixture.Snapshot, fixture.Entries).Code);
    }

    [Fact]
    public void Fixed_window_is_monotonic_per_application_and_rate_rejection_consumes_sequence()
    {
        var clock = new ManualClock();
        var router = new FlyoutRequestRouter(clock);
        var a = CreateFixture();
        for (var sequence = 1; sequence <= 10; sequence++)
            Assert.Equal("Received", router.Handle("app", "session", Request(sequence), a.Snapshot, a.Entries).Code);
        clock.UtcNow = clock.UtcNow.AddDays(1);
        Assert.Equal("RateLimited", router.Handle("app", "session", Request(11), a.Snapshot, a.Entries).Code);
        var b = CreateFixture("other");
        Assert.Equal("Received", router.Handle("other", "session", Request(), b.Snapshot, b.Entries).Code);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal("RateLimited", router.Handle("app", "session", Request(12), a.Snapshot, a.Entries).Code);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("DuplicateRequest", router.Handle("app", "session", Request(12), a.Snapshot, a.Entries).Code);
        Assert.Equal("Received", router.Handle("app", "session", Request(13), a.Snapshot, a.Entries).Code);
    }

    [Fact]
    public void New_session_resets_sequence_but_cannot_evade_application_rate_limit()
    {
        var router = new FlyoutRequestRouter(new ManualClock());
        var fixture = CreateFixture();
        for (var sequence = 1; sequence <= 10; sequence++)
            Assert.True(router.Handle("app", "session", Request(sequence), fixture.Snapshot, fixture.Entries).Accepted);
        var reconnected = fixture.Snapshot with { SessionId = "new-session" };
        Assert.Equal("RateLimited", router.Handle("app", "new-session", Request(), reconnected, fixture.Entries).Code);
        Assert.Equal("StaleSession", router.Handle("app", "session", Request(100), reconnected, fixture.Entries).Code);
    }

    [Fact]
    public void Requests_and_diagnostics_are_bounded_to_sixteen_applications()
    {
        var router = new FlyoutRequestRouter();
        for (var index = 0; index < 16; index++)
        {
            var application = "app-" + index;
            var fixture = CreateFixture(application);
            Assert.True(router.Handle(application, "session", Request(), fixture.Snapshot, fixture.Entries).Accepted);
        }
        var extra = CreateFixture("extra");
        Assert.Equal("Busy", router.Handle("extra", "session", Request(), extra.Snapshot, extra.Entries).Code);
        Assert.Null(router.GetLastResult("extra"));
    }

    [Fact]
    public void Cancellation_does_not_create_a_pending_request_or_consume_sequence()
    {
        var fixture = CreateFixture();
        var router = new FlyoutRequestRouter();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => router.Handle("app", "session", Request(), fixture.Snapshot, fixture.Entries, cancelled.Token));
        Assert.Null(router.GetLastResult("app"));
        Assert.Equal("Received", router.Handle("app", "session", Request(), fixture.Snapshot, fixture.Entries).Code);
    }

    private static FlyoutRequest Request(long sequence = 1, string entry = "hint", FlyoutKind kind = FlyoutKind.ShortHint, long revision = 5) =>
        new("request-" + sequence, sequence, "main", entry, kind, revision);

    private static (BrokerApplicationSnapshot Snapshot, IReadOnlyList<ValidatedFlyoutEntry> Entries) CreateFixture(string application = "app", string session = "session")
    {
        var declaration = new ApplicationDeclaration(application,
            [new FeatureGroupDeclaration("main", [new ComponentDeclaration("counter", [new("activate")])], [new TaskbarFlyoutDeclaration("details", [new("activate")])])]);
        var validated = new DeclarationValidator().Validate(declaration).Value!;
        var snapshot = new BrokerApplicationSnapshot(application, session, validated,
            new ApplicationState(5, [new ComponentReading("main", "counter", "5", 5)]), true, true, null);
        var parent = new StableIdentity(new StableId(application)).CreateChild(new StableId("main"));
        return (snapshot, [new ValidatedFlyoutEntry(parent.CreateChild(new StableId("hint")), FlyoutKind.ShortHint),
            new ValidatedFlyoutEntry(parent.CreateChild(new StableId("details")), FlyoutKind.TaskbarGroup),
            new ValidatedFlyoutEntry(parent.CreateChild(new StableId("interactive")), FlyoutKind.InteractiveHint),
            new ValidatedFlyoutEntry(parent.CreateChild(new StableId("event")), FlyoutKind.EventGroup, EventClosePolicy.AutoClose)]);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public DateTimeOffset UtcNow = DateTimeOffset.Parse("2026-10-05T00:00:00Z");
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}

using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class HostFlyoutReplacementTrackerTests
{
    [Fact]
    public void Different_applications_replacing_one_screen_both_receive_terminal_results()
    {
        var router = new FlyoutRequestRouter();
        var tracker = new HostFlyoutReplacementTracker(router);
        var first = Admit(router, "first");
        tracker.Track("screen", first);
        var second = Admit(router, "second");
        tracker.Track("screen", second);

        Assert.Equal("FlyoutSuperseded", router.GetLastResult("first")!.Result.Code);
        Assert.False(router.GetLastResult("first")!.Result.Accepted);
        Assert.Equal("Replacing", router.GetLastResult("second")!.Result.Code);
        Assert.Same(second, Assert.Single(tracker.Snapshot()).Value);
        Assert.True(tracker.Complete("screen", second, ProtocolResult.Success("Displayed")));
        Assert.Equal("Displayed", router.GetLastResult("second")!.Result.Code);
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void Completion_from_a_superseded_request_cannot_remove_the_current_replacement()
    {
        var router = new FlyoutRequestRouter();
        var tracker = new HostFlyoutReplacementTracker(router);
        var first = Admit(router, "first");
        var second = Admit(router, "second");
        tracker.Track("screen", first);
        tracker.Track("screen", second);
        Assert.False(tracker.Complete("screen", first, ProtocolResult.Success("Displayed")));
        Assert.Same(second, Assert.Single(tracker.Snapshot()).Value);
        Assert.Equal("FlyoutSuperseded", router.GetLastResult("first")!.Result.Code);
        tracker.Clear();
        tracker.Clear();
        Assert.Empty(tracker.Snapshot());
        Assert.Equal("HostClosed", router.GetLastResult("second")!.Result.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Supersession_cannot_overwrite_a_newer_request_or_reconnected_session(bool reconnect)
    {
        var router = new FlyoutRequestRouter();
        var tracker = new HostFlyoutReplacementTracker(router);
        tracker.Track("screen", Admit(router, "first"));
        var newer = Admit(router, "first", reconnect ? "new-session" : "session", reconnect ? 1 : 2);
        Assert.True(router.RecordPresentationResult("first", newer.SessionId, newer.Flyout!, ProtocolResult.Success("Displayed")));
        tracker.Track("screen", Admit(router, "second"));
        var receipt = router.GetLastResult("first")!;
        Assert.Equal(newer.Flyout!.RequestSequence, receipt.RequestSequence);
        Assert.Equal("Displayed", receipt.Result.Code);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Displayed")]
    [InlineData("Closed")]
    public void Completion_and_supersession_only_change_a_still_replacing_receipt(string newerCode)
    {
        var router = new FlyoutRequestRouter();
        var tracker = new HostFlyoutReplacementTracker(router);
        var first = Admit(router, "first");
        tracker.Track("screen", first);
        Assert.True(router.RecordPresentationResult("first", "session", first.Flyout!, ProtocolResult.Success(newerCode)));
        tracker.Track("screen", Admit(router, "second"));
        Assert.Equal(newerCode, router.GetLastResult("first")!.Result.Code);
        Assert.False(tracker.Complete("screen", first, ProtocolResult.Reject("FlyoutReplacementFailed", "late")));
    }

    [Fact]
    public void Screens_are_isolated_and_pending_screen_count_is_bounded()
    {
        var router = new FlyoutRequestRouter();
        var tracker = new HostFlyoutReplacementTracker(router);
        var first = Admit(router, "first");
        for (int index = 0; index < ItemPresentationLimits.MaximumScreens; index++) tracker.Track("screen-" + index, first);
        Assert.Throws<InvalidOperationException>(() => tracker.Track("extra", first));
        Assert.Equal(ItemPresentationLimits.MaximumScreens, tracker.Snapshot().Count);
        tracker.Track("screen-0", Admit(router, "second"));
        Assert.Same(first, tracker.Snapshot().Single(x => x.Key == "screen-1").Value);
        Assert.Equal(ItemPresentationLimits.MaximumScreens, tracker.Snapshot().Count);
    }

    private static ProtocolMessage Admit(FlyoutRequestRouter router, string application, string session = "session", long sequence = 1)
    {
        var validated = new DeclarationValidator().Validate(new(application,
            [new FeatureGroupDeclaration("main", [new ComponentDeclaration("component", [new("activate")])], [new TaskbarFlyoutDeclaration("details", [new("activate")])])]));
        Assert.True(validated.IsSuccess, validated.Error?.ToString());
        var declaration = validated.Value!;
        var snapshot = new BrokerApplicationSnapshot(application, session, declaration,
            new ApplicationState(0, [new ComponentReading("main", "component", "value")]), true, true, null);
        var request = new FlyoutRequest("request-" + sequence, sequence, "main", "details", FlyoutKind.TaskbarGroup, 0);
        Assert.True(router.Handle(application, session, request, snapshot, declaration.FlyoutEntries).Accepted);
        Assert.True(router.RecordPresentationResult(application, session, request, ProtocolResult.Success("Replacing")));
        return new() { Kind = MessageKind.FlyoutRequest, ApplicationId = application, SessionId = session, RequestId = request.RequestId, Flyout = request };
    }
}

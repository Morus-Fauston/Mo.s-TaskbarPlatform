using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class ActivityDisplayPreferenceTests
{
    [Fact]
    public void RebindingAHostStoreRestoresSavedPermissionAndStopsWritingTheOldStore()
    {
        var original = Ready();
        var display = Display(new PreferenceStore(), original);
        var identity = Assert.Single(display.CurrentComponents).Identity;
        Assert.True(display.SetVisibility(identity, true).IsSuccess);
        var replacement = Ready();
        display.BindActivityPermissions(replacement);
        display.ApplyBrokerSnapshots(replacement.Snapshots);
        Assert.True(replacement.GetDisplayPermissions("app")!.Entries[0].Allowed);
        Assert.True(display.SetVisibility(identity, false).IsSuccess);
        Assert.False(replacement.GetDisplayPermissions("app")!.Entries[0].Allowed);
        Assert.True(original.GetDisplayPermissions("app")!.Entries[0].Allowed);
    }

    [Fact]
    public void SavedVisibilityControlsAdmissionAndSurvivesHostRestartWithoutRestoringActivities()
    {
        var preferences = new PreferenceStore();
        var states = Ready();
        var display = Display(preferences, states);
        Assert.False(states.GetDisplayPermissions("app")!.Entries[0].Allowed);
        var identity = Assert.Single(display.CurrentComponents).Identity;
        Assert.True(display.SetVisibility(identity, true).IsSuccess);
        Assert.True(states.GetDisplayPermissions("app")!.Entries[0].Allowed);

        var restarted = Ready();
        var next = Display(preferences, restarted);
        Assert.True(Assert.Single(next.CurrentComponents).IsVisible);
        Assert.True(restarted.GetDisplayPermissions("app")!.Entries[0].Allowed);
        Assert.Empty(restarted.GetSnapshot("app")!.State!.DynamicEntries ?? []);
    }

    [Fact]
    public void FailedPreferenceSaveDoesNotChangeAdmissionOrNotifyANewPermissionRevision()
    {
        var preferences = new PreferenceStore();
        var states = Ready();
        var display = Display(preferences, states);
        var identity = Assert.Single(display.CurrentComponents).Identity;
        Assert.True(display.SetVisibility(identity, true).IsSuccess);
        var before = states.GetDisplayPermissions("app")!;
        preferences.FailSave = true;
        Assert.False(display.SetVisibility(identity, false).IsSuccess);
        Assert.True(Assert.Single(display.CurrentComponents).IsVisible);
        Assert.Equal(before, states.GetDisplayPermissions("app"));
    }

    private static HostDisplayController Display(PreferenceStore preferences, BrokerStateStore states)
    {
        var display = new HostDisplayController(new UnusedSource(), preferences);
        display.BindActivityPermissions(states);
        display.ApplyBrokerSnapshots(states.Snapshots);
        return display;
    }

    private static BrokerStateStore Ready()
    {
        var states = new BrokerStateStore(["app"]);
        states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = "session" });
        var declaration = new ApplicationDeclaration("app", [new FeatureGroupDeclaration("main",
            [new ComponentDeclaration("island", [new("activate")], new(DynamicContentKind.LiveIsland,
                [new("item", true, new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small)))]))],
            [new TaskbarFlyoutDeclaration("details", [new("activate")])])]);
        Assert.True(states.Handle(new()
        {
            Kind = MessageKind.Declare,
            ApplicationId = "app",
            SessionId = "session",
            Declaration = declaration,
            State = new(0, [new("main", "island", "ready")])
        }).Result!.Accepted);
        return states;
    }

    private sealed class UnusedSource : IDeclarationSource
    {
        public CoreResult<string> Read() => throw new InvalidOperationException("Broker-only test");
    }

    private sealed class PreferenceStore : IComponentDisplayPreferenceStore
    {
        private ComponentDisplayPreferences current = new();
        public bool FailSave { get; set; }
        public ComponentDisplayPreferenceLoadResult Load() => new(current, null, ComponentDisplayPreferenceLoadState.Loaded);
        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool visible)
        {
            if (FailSave) return CoreResult<ComponentDisplayPreferences>.Failure(new("test_save_failed", "Save failed"));
            current = current.WithVisibility(identity, visible);
            return CoreResult<ComponentDisplayPreferences>.Success(current);
        }
    }
}

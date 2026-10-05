using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class HostEventSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-event-settings-" + Guid.NewGuid().ToString("N"));
    public HostEventSettingsTests() => Directory.CreateDirectory(directory);
    private string SettingsPath => Path.Combine(directory, "settings.json");

    [Fact]
    public void Saved_event_preferences_and_visibility_are_loaded_without_rewriting_the_file()
    {
        var json = JsonSerializer.Serialize(new
        {
            Appearance = new HostAppearancePreferences(), ComponentOrder = Array.Empty<string>(),
            Events = new { DefaultPosition = FlyoutPosition.BottomLeft, AllowApplicationPosition = true, MaximumGroupsPerScreen = 10 },
            EventVisibility = new Dictionary<string, bool> { ["[\"app\",\"group\",\"event\"]"] = false }
        });
        File.WriteAllText(SettingsPath, json);
        var loaded = new LocalHostSettingsPreferenceStore(SettingsPath).Load();
        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(new HostEventPreferences(FlyoutPosition.BottomLeft, true, 10), loaded.Value!.Events);
        Assert.False(Assert.Single(loaded.Value.EventVisibility!).Value);
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Event_commits_preserve_hint_grouping_order_and_appearance_preferences(int limit)
    {
        var first = new LocalHostSettingsPreferenceStore(SettingsPath);
        var second = new LocalHostSettingsPreferenceStore(SettingsPath);
        var appearance = new HostAppearancePreferences(HostTheme.Dark, MaterialKind.Solid, 0.4);
        var hint = new HostHintPreferences(FlyoutPosition.TopRight, true);
        var order = HostSettingsController.IdentityKey(Identity("app", "component"));
        Assert.True(first.CommitAppearance(appearance).IsSuccess);
        Assert.True(first.CommitOrder([order]).IsSuccess);
        Assert.True(first.CommitGrouping(Identity("app", "island"), DynamicGrouping.Separate).IsSuccess);
        Assert.True(first.CommitHints(hint).IsSuccess);
        Assert.True(first.CommitHintVisibility(Identity("app", "hint"), false).IsSuccess);
        Assert.True(second.CommitEvents(new(FlyoutPosition.BottomRight, true, limit)).IsSuccess);
        Assert.True(first.CommitEventVisibility(Identity("app", "event"), false).IsSuccess);
        var saved = second.Load().Value!;
        Assert.Equal(new HostEventPreferences(FlyoutPosition.BottomRight, true, limit), saved.Events);
        Assert.False(saved.EventVisibility![HostSettingsController.IdentityKey(Identity("app", "event"))]);
        Assert.Equal(appearance, saved.Appearance);
        Assert.Equal(hint, saved.Hints);
        Assert.Equal(order, Assert.Single(saved.ComponentOrder));
        Assert.Equal(DynamicGrouping.Separate, Assert.Single(saved.IslandGrouping!).Value);
        Assert.False(Assert.Single(saved.HintVisibility!).Value);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, bool>)saved.EventVisibility).Clear());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void Controller_projects_only_current_declared_event_channels_and_publishes_committed_preferences()
    {
        var states = Ready("app", "other");
        using var display = CreateDisplay();
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        var observations = new List<HostSettingsSnapshot>();
        HostSettingsController? current = null;
        using var controller = Controller(display, store, () => states.Snapshots, () => observations.Add(current!.GetSnapshot()));
        current = controller;
        var initial = controller.GetSnapshot();
        Assert.Equal(new HostEventPreferences(), initial.Preferences.Events ?? new());
        Assert.Equal(4, initial.EventEntries.Count);
        Assert.All(initial.EventEntries, row => Assert.True(row.IsAvailable && row.IsVisible));
        Assert.Contains(initial.EventEntries, row => row.ClosePolicy == EventClosePolicy.Persistent);
        Assert.DoesNotContain(initial.EventEntries, row => row.Identity.LocalId.Value == "hint");
        Assert.True(controller.SetEvents(new(FlyoutPosition.TopCenter, true, 1)).IsSuccess);
        Assert.True(controller.SetEventVisibility(Identity("app", "event"), false).IsSuccess);
        Assert.Equal(1, observations[0].Preferences.Events!.MaximumGroupsPerScreen);
        Assert.False(observations[1].EventEntries.Single(row => row.Identity == Identity("app", "event")).IsVisible);
        Assert.True(controller.GetSnapshot().EventEntries.Single(row => row.Identity == Identity("other", "event")).IsVisible);
        Assert.False(controller.SetEventVisibility(Identity("app", "hint"), false).IsSuccess);
        Assert.False(controller.SetEventVisibility(Identity("app", "unknown"), false).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Invalid_group_limits_and_positions_do_not_change_the_last_saved_settings(int limit)
    {
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.True(store.CommitEvents(new()).IsSuccess);
        var saved = File.ReadAllBytes(SettingsPath);
        Assert.False(store.CommitEvents(new(MaximumGroupsPerScreen: limit)).IsSuccess);
        Assert.False(store.CommitEvents(new(FlyoutPosition.Default)).IsSuccess);
        Assert.False(store.CommitEvents(new((FlyoutPosition)999)).IsSuccess);
        Assert.False(store.CommitEvents(null!).IsSuccess);
        Assert.False(store.CommitEventVisibility(null!, true).IsSuccess);
        Assert.Equal(saved, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void Event_identity_budget_is_shared_with_existing_preferences_and_freezes_values()
    {
        var keys = Enumerable.Range(0, LocalHostSettingsPreferenceStore.MaximumIdentities)
            .Select(i => HostSettingsController.IdentityKey(Identity("app", "entry" + i))).ToArray();
        var prefs = new HostSettingsPreferences(new(), keys, EventVisibility: new Dictionary<string, bool> { [keys[0]] = false });
        var valid = LocalHostSettingsPreferenceStore.Validate(prefs);
        Assert.True(valid.IsSuccess);
        Assert.False(LocalHostSettingsPreferenceStore.Validate(prefs with { EventVisibility = new Dictionary<string, bool>
            { [HostSettingsController.IdentityKey(Identity("app", "extra"))] = false } }).IsSuccess);
        Assert.False(LocalHostSettingsPreferenceStore.Validate(new(new(), [], EventVisibility: new Dictionary<string, bool>
            { ["[\"app\",\"group\",\"event\"]"] = true, ["[ \"app\", \"group\", \"event\" ]"] = false })).IsSuccess);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, bool>)valid.Value!.EventVisibility!).Clear());
    }

    [Theory]
    [InlineData(FlyoutPosition.TopLeft)]
    [InlineData(FlyoutPosition.TopCenter)]
    [InlineData(FlyoutPosition.TopRight)]
    [InlineData(FlyoutPosition.BottomLeft)]
    [InlineData(FlyoutPosition.BottomCenter)]
    [InlineData(FlyoutPosition.BottomRight)]
    [InlineData(FlyoutPosition.Center)]
    [InlineData(FlyoutPosition.LowerCenter)]
    public void Supported_positions_round_trip_as_concrete_user_preferences(FlyoutPosition position)
    {
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.True(store.CommitEvents(new(position, true, 5)).IsSuccess);
        Assert.Equal(new HostEventPreferences(position, true, 5), new LocalHostSettingsPreferenceStore(SettingsPath).Load().Value!.Events);
    }

    [Fact]
    public void Failed_save_keeps_published_values_and_successful_retry_clears_error()
    {
        var states = Ready("app");
        using var display = CreateDisplay();
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        using var controller = Controller(display, store, () => states.Snapshots);
        var preferences = new HostEventPreferences(FlyoutPosition.TopRight, true, 10);
        var id = Identity("app", "event");
        Assert.True(controller.SetEvents(preferences).IsSuccess);
        Assert.True(controller.SetEventVisibility(id, false).IsSuccess);
        byte[] original = File.ReadAllBytes(SettingsPath);
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(controller.SetEvents(new()).IsSuccess);
            Assert.False(controller.SetEventVisibility(id, true).IsSuccess);
            var snapshot = controller.GetSnapshot();
            Assert.Equal(preferences, snapshot.Preferences.Events);
            Assert.False(snapshot.EventEntries.Single(row => row.Identity == id).IsVisible);
            Assert.NotNull(snapshot.Error);
        }
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.True(controller.SetEvents(new()).IsSuccess);
        Assert.True(controller.SetEventVisibility(id, true).IsSuccess);
        Assert.Null(controller.GetSnapshot().Error);
    }

    [Fact]
    public void Missing_disconnected_and_disposed_entries_cannot_lose_stable_preferences()
    {
        var states = Ready("app");
        IReadOnlyList<BrokerApplicationSnapshot> current = states.Snapshots;
        using var display = CreateDisplay();
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        using var controller = Controller(display, store, () => current);
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal(new HostEventPreferences(), controller.GetSnapshot().Preferences.Events ?? new());
        var id = Identity("app", "event");
        Assert.True(controller.SetEventVisibility(id, false).IsSuccess);
        current = [];
        Assert.Empty(controller.GetSnapshot().EventEntries);
        Assert.False(controller.SetEventVisibility(id, true).IsSuccess);
        Assert.False(store.Load().Value!.EventVisibility![HostSettingsController.IdentityKey(id)]);
        states.Handle(new() { Kind = MessageKind.Disconnected, ApplicationId = "app", SessionId = "session" });
        current = states.Snapshots;
        Assert.All(controller.GetSnapshot().EventEntries, row => Assert.False(row.IsAvailable));
        Assert.True(controller.SetEventVisibility(id, true).IsSuccess);
        byte[] saved = File.ReadAllBytes(SettingsPath);
        controller.Dispose();
        Assert.False(controller.SetEvents(new()).IsSuccess);
        Assert.False(controller.SetEventVisibility(id, false).IsSuccess);
        Assert.Equal(saved, File.ReadAllBytes(SettingsPath));
    }

    [Theory]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"Events\":{\"DefaultPosition\":4,\"Unknown\":true}}")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"Events\":{\"MaximumGroupsPerScreen\":11}}")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"Events\":{\"MaximumGroupsPerScreen\":1.5}}")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"EventVisibility\":{\"[\\\"app\\\",\\\"group\\\",\\\"event\\\"]\":\"false\"}}")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"EventVisibility\":{\"[\\\"app\\\",\\\"event\\\"]\":false}}")]
    public void Malformed_event_settings_block_commits_without_overwriting_the_original(string json)
    {
        File.WriteAllText(SettingsPath, json);
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.False(store.Load().IsSuccess);
        Assert.False(store.CommitEvents(new()).IsSuccess);
        Assert.False(store.CommitEventVisibility(Identity("app", "event"), false).IsSuccess);
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Oversized_and_invalid_utf8_files_cannot_be_replaced_by_event_changes()
    {
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        foreach (var bytes in new[] { new byte[] { 0xff, 0xfe }, new byte[LocalHostSettingsPreferenceStore.MaximumBytes + 1] })
        {
            File.WriteAllBytes(SettingsPath, bytes);
            Assert.False(store.Load().IsSuccess);
            Assert.False(store.CommitEvents(new()).IsSuccess);
            Assert.False(store.CommitEventVisibility(Identity("app", "event"), false).IsSuccess);
            Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
        }
    }

    private HostSettingsController Controller(HostDisplayController display, IHostSettingsPreferenceStore store,
        Func<IReadOnlyList<BrokerApplicationSnapshot>> states, Action? changed = null) =>
        new(display, store, states, (_, _) => Task.FromResult(ProtocolResult.Success()), () => "none", _ => { }, changed ?? (() => { }));
    private static BrokerStateStore Ready(params string[] apps)
    {
        var states = new BrokerStateStore(apps);
        foreach (var app in apps)
        {
            states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = app, SessionId = "session" });
            var result = states.Handle(new()
            {
                Kind = MessageKind.Declare, ApplicationId = app, SessionId = "session",
                Declaration = new(app, [new("group", [new("component", [new("activate")])], [new("panel", [new("activate")])],
                    Hints: [new("hint", FlyoutKind.ShortHint)],
                    EventChannels: [new("event", EventClosePolicy.AutoClose), new("persistent", EventClosePolicy.Persistent)])]),
                State = new(0, [new("group", "component", "value")])
            }).Result!;
            Assert.True(result.Accepted, result.Message);
        }
        return states;
    }
    private static StableIdentity Identity(string app, string entry) => new StableIdentity(new StableId(app)).CreateChild(new("group")).CreateChild(new(entry));
    private static HostDisplayController CreateDisplay() => new(new UnusedDeclaration(), new MemoryDisplayStore());
    private sealed class UnusedDeclaration : IDeclarationSource
    { public CoreResult<string> Read() => throw new InvalidOperationException("Broker-only fixture"); }
    private sealed class MemoryDisplayStore : IComponentDisplayPreferenceStore
    {
        private ComponentDisplayPreferences current = new();
        public ComponentDisplayPreferenceLoadResult Load() => new(current, null, ComponentDisplayPreferenceLoadState.Loaded);
        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool visible) =>
            CoreResult<ComponentDisplayPreferences>.Success(current = current.WithVisibility(identity, visible));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

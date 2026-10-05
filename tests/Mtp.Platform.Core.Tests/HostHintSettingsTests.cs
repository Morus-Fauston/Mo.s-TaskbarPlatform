using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class HostHintSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-hint-settings-" + Guid.NewGuid().ToString("N"));
    public HostHintSettingsTests() => Directory.CreateDirectory(directory);
    private string SettingsPath => Path.Combine(directory, "host-settings.json");

    [Fact]
    public void Saved_hint_preferences_and_complete_visibility_identities_are_accepted()
    {
        var contents = JsonSerializer.Serialize(new
        {
            Appearance = new HostAppearancePreferences(), ComponentOrder = Array.Empty<string>(),
            Hints = new { DefaultPosition = 8, AllowApplicationPosition = true },
            HintVisibility = new Dictionary<string, bool> { ["[\"app\",\"group\",\"hint\"]"] = false }
        });
        File.WriteAllText(SettingsPath, contents);
        var loaded = new LocalHostSettingsPreferenceStore(SettingsPath).Load();
        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
        Assert.Equal(new HostHintPreferences(FlyoutPosition.LowerCenter, true), loaded.Value!.Hints);
        Assert.False(loaded.Value.HintVisibility![HostSettingsController.IdentityKey(Identity("app", "hint"))]);
        Assert.Equal(contents, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Snapshot_projects_only_declared_hint_entries_using_full_identity_preferences()
    {
        var states = Ready("app", "other");
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.True(store.CommitHintVisibility(Identity("app", "hint"), false).IsSuccess);
        using var display = CreateDisplay();
        using var controller = Controller(display, store, () => states.Snapshots);
        var rows = controller.GetSnapshot().HintEntries;
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsAvailable));
        Assert.False(rows.Single(row => row.Identity == Identity("app", "hint")).IsVisible);
        Assert.True(rows.Single(row => row.Identity == Identity("other", "hint")).IsVisible);
        Assert.DoesNotContain(rows, row => row.Kind is FlyoutKind.TaskbarGroup or FlyoutKind.EventGroup);
        Assert.False(File.Exists(Path.Combine(directory, "display.json")));
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
    public void Eight_concrete_positions_round_trip_without_overwriting_other_preferences(FlyoutPosition position)
    {
        var first = new LocalHostSettingsPreferenceStore(SettingsPath);
        var second = new LocalHostSettingsPreferenceStore(SettingsPath);
        var id = Identity("app", "missing-hint");
        var appearance = new HostAppearancePreferences(HostTheme.Dark, MaterialKind.Mica, 0.4);
        string order = HostSettingsController.IdentityKey(Identity("app", "component"));
        Assert.True(first.CommitAppearance(appearance).IsSuccess);
        Assert.True(first.CommitOrder([order]).IsSuccess);
        Assert.True(second.CommitGrouping(Identity("app", "island"), DynamicGrouping.Separate).IsSuccess);
        Assert.True(first.CommitHintVisibility(id, false).IsSuccess);
        var desired = new HostHintPreferences(position, true);
        Assert.True(second.CommitHints(desired).IsSuccess);
        var restored = new LocalHostSettingsPreferenceStore(SettingsPath).Load().Value!;
        Assert.Equal(desired, restored.Hints);
        Assert.Equal(appearance, restored.Appearance);
        Assert.Equal(order, Assert.Single(restored.ComponentOrder));
        Assert.Equal(DynamicGrouping.Separate, Assert.Single(restored.IslandGrouping!).Value);
        Assert.False(restored.HintVisibility![HostSettingsController.IdentityKey(id)]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, bool>)restored.HintVisibility).Clear());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(FlyoutPosition.Default)]
    [InlineData((FlyoutPosition)(-1))]
    [InlineData((FlyoutPosition)99)]
    public void Invalid_default_positions_are_rejected_without_changing_saved_state(FlyoutPosition position)
    {
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.True(store.CommitHints(new()).IsSuccess);
        var original = File.ReadAllBytes(SettingsPath);
        Assert.False(store.CommitHints(new(position)).IsSuccess);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void Controller_notifies_with_committed_values_and_preserves_values_when_save_fails()
    {
        var states = Ready("app");
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        using var display = CreateDisplay();
        HostSettingsController? observing = null;
        var observed = new List<HostSettingsSnapshot>();
        using var controller = Controller(display, store, () => states.Snapshots, () => observed.Add(observing!.GetSnapshot()));
        observing = controller;
        var id = Identity("app", "hint");
        var hints = new HostHintPreferences(FlyoutPosition.TopRight, true);
        Assert.True(controller.SetHints(hints).IsSuccess);
        Assert.True(controller.SetHintVisibility(id, false).IsSuccess);
        Assert.Equal(hints, observed[0].Preferences.Hints);
        Assert.False(observed[1].HintEntries.Single(row => row.Identity == id).IsVisible);
        var original = File.ReadAllBytes(SettingsPath);
        using (var blocked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(controller.SetHints(new()).IsSuccess);
            Assert.False(controller.SetHintVisibility(id, true).IsSuccess);
            var retained = controller.GetSnapshot();
            Assert.Equal(hints, retained.Preferences.Hints);
            Assert.False(retained.HintEntries.Single(row => row.Identity == id).IsVisible);
            Assert.NotNull(retained.Error);
        }
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.True(controller.SetHintVisibility(id, true).IsSuccess);
        Assert.True(controller.GetSnapshot().HintEntries.Single(row => row.Identity == id).IsVisible);
        Assert.Null(controller.GetSnapshot().Error);
    }

    [Fact]
    public void Missing_and_disconnected_entries_preserve_preferences_without_showing_event_controls()
    {
        var states = Ready("app");
        IReadOnlyList<BrokerApplicationSnapshot> current = states.Snapshots;
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        using var display = CreateDisplay();
        using var controller = Controller(display, store, () => current);
        var id = Identity("app", "hint");
        Assert.True(controller.SetHintVisibility(id, false).IsSuccess);
        current = [];
        Assert.Empty(controller.GetSnapshot().HintEntries);
        Assert.False(controller.SetHintVisibility(id, true).IsSuccess);
        Assert.False(controller.SetHintVisibility(Identity("app", "event"), false).IsSuccess);
        Assert.False(controller.SetHintVisibility(Identity("app", "panel"), false).IsSuccess);
        current = states.Snapshots;
        Assert.False(controller.GetSnapshot().HintEntries.Single(row => row.Identity == id).IsVisible);
        states.Handle(new() { Kind = MessageKind.Disconnected, ApplicationId = "app", SessionId = "session" });
        current = states.Snapshots;
        Assert.All(controller.GetSnapshot().HintEntries, row => Assert.False(row.IsAvailable));
        Assert.True(controller.SetHintVisibility(id, true).IsSuccess);
        Assert.True(controller.GetSnapshot().HintEntries.Single(row => row.Identity == id).IsVisible);
    }

    [Fact]
    public void Defaults_and_disposal_do_not_create_or_overwrite_a_settings_file()
    {
        using var display = CreateDisplay();
        var states = Ready("app");
        using var controller = Controller(display, new LocalHostSettingsPreferenceStore(SettingsPath), () => states.Snapshots);
        Assert.Equal(new HostHintPreferences(), controller.GetSnapshot().Preferences.Hints ?? new());
        Assert.All(controller.GetSnapshot().HintEntries, row => Assert.True(row.IsVisible));
        Assert.False(File.Exists(SettingsPath));
        controller.Dispose();
        Assert.False(controller.SetHints(new()).IsSuccess);
        Assert.False(controller.SetHintVisibility(Identity("app", "hint"), false).IsSuccess);
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"Hints\":{\"DefaultPosition\":8,\"Unknown\":true}}")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"Hints\":{\"DefaultPosition\":99}}")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"HintVisibility\":{\"[\\\"app\\\",\\\"group\\\",\\\"hint\\\"]\":\"false\"}}")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"HintVisibility\":{\"[\\\"app\\\",\\\"hint\\\"]\":false}}")]
    public void Malformed_hint_settings_cannot_be_overwritten_by_either_commit(string contents)
    {
        File.WriteAllText(SettingsPath, contents);
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.False(store.Load().IsSuccess);
        Assert.False(store.CommitHints(new()).IsSuccess);
        Assert.False(store.CommitHintVisibility(Identity("app", "hint"), false).IsSuccess);
        Assert.Equal(contents, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Identity_budget_is_shared_and_equivalent_keys_cannot_duplicate_visibility()
    {
        var order = Enumerable.Range(0, LocalHostSettingsPreferenceStore.MaximumIdentities)
            .Select(index => HostSettingsController.IdentityKey(Identity("app", "item-" + index))).ToArray();
        var prefs = new HostSettingsPreferences(new(), order, HintVisibility: new Dictionary<string, bool> { [order[0]] = false });
        Assert.True(LocalHostSettingsPreferenceStore.Validate(prefs).IsSuccess);
        Assert.False(LocalHostSettingsPreferenceStore.Validate(prefs with
        { HintVisibility = new Dictionary<string, bool> { [HostSettingsController.IdentityKey(Identity("app", "extra"))] = false } }).IsSuccess);
        Assert.False(LocalHostSettingsPreferenceStore.Validate(new(new(), [], HintVisibility: new Dictionary<string, bool>
        { ["[\"app\",\"group\",\"hint\"]"] = true, ["[ \"app\", \"group\", \"hint\" ]"] = false })).IsSuccess);
    }

    [Fact]
    public void Oversized_or_invalid_utf8_settings_remain_unchanged()
    {
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        foreach (var bytes in new[] { new byte[] { 0xff, 0xfe }, new byte[LocalHostSettingsPreferenceStore.MaximumBytes + 1] })
        {
            File.WriteAllBytes(SettingsPath, bytes);
            Assert.False(store.Load().IsSuccess);
            Assert.False(store.CommitHints(new()).IsSuccess);
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
                    Hints: [new("hint", FlyoutKind.ShortHint), new("interactive", FlyoutKind.InteractiveHint)],
                    EventChannels: [new("event", EventClosePolicy.AutoClose)])]),
                State = new(0, [new("group", "component", "value")])
            }).Result!;
            Assert.True(result.Accepted, result.ToString());
        }
        return states;
    }

    private static StableIdentity Identity(string app, string entry) => new StableIdentity(new StableId(app)).CreateChild(new("group")).CreateChild(new(entry));
    private static HostDisplayController CreateDisplay() => new(new UnusedDeclaration(), new MemoryDisplayStore());
    private sealed class UnusedDeclaration : IDeclarationSource
    {
        public CoreResult<string> Read() => throw new InvalidOperationException("Broker-only fixture");
    }
    private sealed class MemoryDisplayStore : IComponentDisplayPreferenceStore
    {
        private ComponentDisplayPreferences current = new();
        public ComponentDisplayPreferenceLoadResult Load() => new(current, null, ComponentDisplayPreferenceLoadState.Loaded);
        public CoreResult<ComponentDisplayPreferences> CommitVisibility(StableIdentity identity, bool visible) =>
            CoreResult<ComponentDisplayPreferences>.Success(current = current.WithVisibility(identity, visible));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

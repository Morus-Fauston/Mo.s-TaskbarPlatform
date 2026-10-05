using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class IslandOrganizationTests
{
    private static readonly TaskbarComponentKey Entry = new("app", "group", "island");

    [Fact]
    public void RemovingSharedItemSplitsConnectionsWithoutChangingOtherItemHandles()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Settings.SetVisibility(fixture.Identity, true).IsSuccess);
        Assert.True(fixture.Settings.SetGrouping(fixture.Identity, DynamicGrouping.Separate).IsSuccess);
        fixture.Publish(1); fixture.Display.ItemPresentations!.UpdateScreens(["screen"]);
        var before = fixture.Build();
        Assert.Equal(2, before.Instances.Count);
        var one = before.ItemsByKey.Single(value => value.Key.ItemId == "one");
        fixture.Display.ItemPresentations.Toggle(one.Value.Handle);
        fixture.Publish(2, shared: false);
        var after = fixture.Build();
        Assert.Equal(new[] { "c", "b", "a" }, after.Instances.Select(value => value.InstanceId));
        Assert.Equal(one.Value.Handle, after.ItemsByKey[one.Key].Handle);
        Assert.True(after.ItemsByKey[one.Key].Expanded);
        Assert.Null(fixture.Display.ItemPresentations.Resolve(before.Items.Single(value => value.Item.ItemId == "shared").Handle));
    }

    [Fact]
    public async Task ConcurrentPreferenceFieldsKeepGroupingAndFailedReadDoesNotOverwriteIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-org-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var identity = new StableIdentity(new StableId("app")).CreateChild(new("group")).CreateChild(new("island"));
            var results = await Task.WhenAll(
                Task.Run(() => new LocalHostSettingsPreferenceStore(path).CommitGrouping(identity, DynamicGrouping.Separate)),
                Task.Run(() => new LocalHostSettingsPreferenceStore(path).CommitOrder([HostSettingsController.IdentityKey(identity)])),
                Task.Run(() => new LocalHostSettingsPreferenceStore(path).CommitAppearance(new(HostTheme.Dark))));
            Assert.All(results, result => Assert.True(result.IsSuccess));
            var store = new LocalHostSettingsPreferenceStore(path);
            var persisted = store.Load().Value!;
            Assert.Single(persisted.ComponentOrder); Assert.Single(persisted.IslandGrouping!);
            Assert.Equal(HostTheme.Dark, persisted.Appearance.Theme);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, DynamicGrouping>)persisted.IslandGrouping!).Clear());
            var original = File.ReadAllText(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(store.CommitGrouping(identity, DynamicGrouping.Together).IsSuccess);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void GroupingStorageRejectsInvalidModesAndCanonicalDuplicates()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-org-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var values = new Dictionary<string, DynamicGrouping>
            { ["[\"app\",\"group\",\"island\"]"] = DynamicGrouping.Separate, ["[ \"app\", \"group\", \"island\" ]"] = DynamicGrouping.Together };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new HostSettingsPreferences(new(), [], values)));
            var store = new LocalHostSettingsPreferenceStore(path);
            Assert.False(store.Load().IsSuccess);
            values.Remove(values.Keys.Last()); values[values.Keys.Single()] = DynamicGrouping.UserChoice;
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new HostSettingsPreferences(new(), [], values)));
            var original = File.ReadAllText(path);
            Assert.False(store.Load().IsSuccess); Assert.False(store.CommitOrder([]).IsSuccess);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(DynamicGrouping.Together, DynamicGrouping.Separate, 1)]
    [InlineData(DynamicGrouping.Separate, DynamicGrouping.Together, 2)]
    [InlineData(DynamicGrouping.UserChoice, DynamicGrouping.Together, 1)]
    public void FixedProviderCapabilityWinsStoredPreference(DynamicGrouping capability, DynamicGrouping preference, int expected)
    {
        var result = HostIslandOrganization.Project(Entry, capability, preference,
            [Activity("a", 0), Activity("b", 0)], [Item("one", "a"), Item("two", "b")]);
        Assert.Equal(expected, result.Count);
        Assert.Equal(new[] { "one", "two" }, result.SelectMany(value => value.Items).Select(value => value.Item.ItemId));
    }

    [Fact]
    public void TransitiveSharedActivitiesStayTogetherAndTieUsesProviderSequence()
    {
        var result = HostIslandOrganization.Project(Entry, DynamicGrouping.Separate, null,
            [Activity("z", 1), Activity("a", 1), Activity("b", 4), Activity("c", 8), Activity("empty", -1)],
            [Item("ab", "a", "b"), Item("bc", "b", "c"), Item("z-item", "z")]);
        Assert.Equal(new[] { "z", "a" }, result.Select(value => value.InstanceId));
        Assert.Equal(new[] { "ab", "bc" }, result[1].Items.Select(value => value.Item.ItemId));
        Assert.Throws<NotSupportedException>(() => ((IList<HostIslandInstance>)result).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<HostItemPresentation>)result[1].Items).Clear());
    }

    [Fact]
    public void InvalidReferencesIdentityDuplicatesAndBudgetsAreRejectedWithoutState()
    {
        Assert.Throws<ArgumentException>(() => HostIslandOrganization.Project(Entry, DynamicGrouping.Together, null,
            [Activity("a", 0)], [Item("one", "missing")]));
        Assert.Throws<ArgumentException>(() => HostIslandOrganization.Project(Entry, DynamicGrouping.Together, null,
            [Activity("a", 0)], [Item("one", "a"), Item("one", "a")]));
        Assert.Throws<ArgumentException>(() => HostIslandOrganization.Project(Entry with { ApplicationId = "other" }, DynamicGrouping.Together, null,
            [Activity("a", 0)], [Item("one", "a")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => HostIslandOrganization.Project(Entry, DynamicGrouping.Together, null,
            Enumerable.Range(0, 65).Select(i => Activity(i.ToString(), i)).ToArray(), []));
        Assert.Empty(HostIslandOrganization.Project(Entry, DynamicGrouping.Separate, null, [], []));
    }

    [Fact]
    public void LayoutRequiresContiguousInstancesButKeepsOriginalItemKeys()
    {
        TaskbarMeasuredItem Measure(string id, string instance) => new(new(Entry, id, 1), 32, InstanceId: instance);
        var layout = TaskbarGroupLayout.Calculate([new(Entry, Items: [Measure("one", "a"), Measure("two", "a"), Measure("three", "b")])], 32);
        Assert.Equal(116, layout.WidthDip);
        Assert.Equal(4, layout.Items[1].Bounds.X - layout.Items[0].Bounds.Right);
        Assert.Equal(8, layout.Items[2].Bounds.X - layout.Items[1].Bounds.Right);
        Assert.All(layout.Items, item => Assert.Equal(Entry, item.Key.Component));
        Assert.Throws<ArgumentException>(() => TaskbarGroupLayout.Calculate([new(Entry, Items: [Measure("one", "a"), Measure("two", "b"), Measure("three", "a")])], 32));
    }

    [Fact]
    public void RegroupingChangesOnlyGeometryAndRetainsItemExpansionAndBusinessIdentity()
    {
        using var fixture = new Fixture();
        foreach (var component in fixture.Display.CurrentComponents) Assert.True(fixture.Settings.SetVisibility(component.Identity, true).IsSuccess);
        fixture.Publish(1);
        var presentations = fixture.Display.ItemPresentations!;
        Assert.True(presentations.UpdateScreens(["screen"]).Accepted);
        var together = fixture.Build();
        Assert.Single(together.Instances);
        var original = together.ItemsByKey.Single(pair => pair.Key.ItemId == "one");
        Assert.True(presentations.Toggle(original.Value.Handle).Result.Accepted);
        var expanded = fixture.Build();
        var business = fixture.States.GetSnapshot("app")!.State;
        Assert.True(fixture.Settings.SetGrouping(fixture.Identity, DynamicGrouping.Separate).IsSuccess);
        var split = fixture.Build();
        Assert.Equal(new[] { "c", "a" }, split.Instances.Select(value => value.InstanceId));
        Assert.Equal(expanded.Layout.WidthDip + 4, split.Layout.WidthDip);
        Assert.Equal(original.Value.Handle, split.ItemsByKey[original.Key].Handle);
        Assert.True(split.ItemsByKey[original.Key].Expanded);
        Assert.Same(business, fixture.States.GetSnapshot("app")!.State);
        Assert.Equal(4, split.ItemsByKey.Count);
        Assert.Equal(expanded.Layout.Components[^1].Bounds, split.Layout.Components[^1].Bounds);
        Assert.True(fixture.Settings.SetVisibility(fixture.Identity, false).IsSuccess);
        Assert.Empty(fixture.Build().Instances);
        fixture.Publish(2);
        Assert.True(fixture.Settings.SetVisibility(fixture.Identity, true).IsSuccess);
        Assert.True(fixture.Build().ItemsByKey[original.Key].Expanded);
    }

    [Fact]
    public void SettingsOnlyAllowsDeclaredOrganizationAndUserOrderWinsDefaultIslandPlacement()
    {
        using var fixture = new Fixture();
        var initial = fixture.Settings.GetSnapshot();
        Assert.Equal(new[] { "island", "fixed", "left", "right" }, initial.Components.Select(value => value.Identity.LocalId.Value));
        Assert.Equal(DynamicGrouping.Together, initial.Groupings.Single(value => value.Identity == fixture.Identity).Effective);
        Assert.True(fixture.Settings.SetGrouping(fixture.Identity, DynamicGrouping.Separate).IsSuccess);
        Assert.Equal(DynamicGrouping.Separate, fixture.Settings.GetSnapshot().Groupings.Single(value => value.Identity == fixture.Identity).Effective);
        Assert.False(fixture.Settings.SetGrouping(fixture.FixedIdentity, DynamicGrouping.Separate).IsSuccess);
        Assert.False(fixture.Settings.SetGrouping(fixture.Identity, DynamicGrouping.UserChoice).IsSuccess);
        Assert.True(fixture.Settings.MoveComponent(fixture.Identity, 1).IsSuccess);
        Assert.Equal(new[] { "fixed", "island", "left", "right" }, fixture.Settings.GetSnapshot().Components.Select(value => value.Identity.LocalId.Value));
    }

    [Fact]
    public void GroupingIsPersistedIndependentlyAndInvalidChoiceCannotReplaceIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-organization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var identity = new StableIdentity(new StableId("app")).CreateChild(new("group")).CreateChild(new("island"));
            var path = Path.Combine(directory, "settings.json");
            var store = new LocalHostSettingsPreferenceStore(path);
            Assert.True(store.CommitGrouping(identity, DynamicGrouping.Separate).IsSuccess);
            Assert.True(store.CommitAppearance(new(HostTheme.Dark)).IsSuccess);
            var restarted = new LocalHostSettingsPreferenceStore(path).Load().Value!;
            Assert.Equal(DynamicGrouping.Separate, restarted.IslandGrouping![HostSettingsController.IdentityKey(identity)]);
            Assert.Equal(HostTheme.Dark, restarted.Appearance.Theme);
            var before = File.ReadAllText(path);
            Assert.False(store.CommitGrouping(identity, DynamicGrouping.UserChoice).IsSuccess);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SeparateKeepsSharedItemsUniqueAndOrdersConnectedActivitiesByProviderOrder()
    {
        var items = new[] { Item("one", "a"), Item("shared", "a", "b"), Item("two", "b"), Item("three", "c") };
        var activities = new[] { Activity("a", 30), Activity("b", 20), Activity("c", 10) };
        var result = HostIslandOrganization.Project(Entry, DynamicGrouping.UserChoice, DynamicGrouping.Separate, activities, items);
        Assert.Equal(new[] { "c", "a" }, result.Select(value => value.InstanceId));
        Assert.Equal(new[] { "three", "one", "shared", "two" }, result.SelectMany(value => value.Items).Select(value => value.Item.ItemId));
        Assert.Same(items[1], result[1].Items[1]);
        Assert.Equal(4, result.SelectMany(value => value.Items).Select(value => value.Handle).Distinct().Count());
    }

    private static ActivityState Activity(string id, int order) => new(id, DateTimeOffset.UtcNow.AddMinutes(1), Order: order);
    private static HostItemPresentation Item(string id, params string[] activities)
    {
        var presentation = new ItemPresentation(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small));
        return new(new(Guid.Empty, "screen", new("app", "group", "island", id), 1), "session", DynamicContentKind.LiveIsland,
            new("status", true, presentation), new(id, "status", activities, new(Status: new(id))), false, presentation, true, 1);
    }

    private sealed class Fixture : IDisposable
    {
        public BrokerStateStore States { get; } = new(["app"]);
        public HostDisplayController Display { get; }
        public HostSettingsController Settings { get; }
        public StableIdentity Identity => Display.CurrentComponents.Single(value => value.Identity.LocalId.Value == "island").Identity;
        public StableIdentity FixedIdentity => Display.CurrentComponents.Single(value => value.Identity.LocalId.Value == "fixed").Identity;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-org-fixture-" + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Display = new(new LocalJsonDeclarationSource(Path.Combine(directory, "unused.json")), new LocalComponentDisplayPreferenceStore(Path.Combine(directory, "display.json")));
            var normal = new ItemPresentation(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small));
            var dynamic = new DynamicContentDeclaration(DynamicContentKind.LiveIsland,
                [new("status", true, normal, normal with { Width = new(WidthTier.Large) })], DynamicGrouping.UserChoice);
            States.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = "session" });
            Assert.True(States.Handle(new()
            {
                Kind = MessageKind.Declare, ApplicationId = "app", SessionId = "session",
                Declaration = new("app", [new("group", [new("left", [new("activate")]), new("island", [new("activate")], dynamic),
                    new("fixed", [new("activate")], dynamic with { Grouping = DynamicGrouping.Together }), new("right", [new("activate")])], [new("panel", [new("open")])])]),
                State = new(0, [], [new("group", "island", new([], [])), new("group", "fixed", new([], []))])
            }).Result!.Accepted);
            Display.ApplyBrokerSnapshots(States.Snapshots);
            Display.BindActivityPermissions(States);
            Settings = new(Display, new LocalHostSettingsPreferenceStore(Path.Combine(directory, "settings.json")), () => States.Snapshots,
                (_, _) => Task.FromResult(ProtocolResult.Success()), () => "none", _ => { }, () => { });
        }
        public void Publish(long revision, bool shared = true) => Assert.True(States.Handle(new()
        {
            Kind = MessageKind.State, ApplicationId = "app", SessionId = "session",
            State = new(revision, [], [new("group", "island", new([Activity("a", 30), Activity("b", 20), Activity("c", 10)],
                new[] { Item("one", "a").Item, Item("shared", "a", "b").Item, Item("two", "b").Item, Item("three", "c").Item }
                    .Where(value => shared || value.ItemId != "shared").ToArray()))])
        }).Result!.Accepted);
        public HostGroupPresentationSnapshot Build() => HostGroupPresentation.Build(Settings.GetSnapshot().Components, States,
            Display.ItemPresentations, "screen", grouping: Settings.GetSnapshot().Preferences.IslandGrouping);
        public void Dispose() { Settings.Dispose(); Display.Dispose(); Directory.Delete(directory, true); }
    }
}

using Mtp.Contracts;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class HostSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-settings-" + Guid.NewGuid().ToString("N"));
    public HostSettingsTests() => Directory.CreateDirectory(directory);
    private string SettingsPath => Path.Combine(directory, "host-settings.json");

    [Fact]
    public void AppearanceCommitPreservesStableOrderAndSurvivesStoreRecreation()
    {
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.True(store.Load().IsSuccess); Assert.False(File.Exists(SettingsPath));
        Assert.True(store.CommitOrder(["[\"app\",\"group\",\"first\"]", "[\"app\",\"group\",\"second\"]"]).IsSuccess);
        var appearance = new HostAppearancePreferences(HostTheme.Dark, MaterialKind.Acrylic, 0.7);
        Assert.True(store.CommitAppearance(appearance).IsSuccess);
        var restored = new LocalHostSettingsPreferenceStore(SettingsPath).Load().Value!;
        Assert.Equal(appearance, restored.Appearance);
        Assert.Equal(new[] { "[\"app\",\"group\",\"first\"]", "[\"app\",\"group\",\"second\"]" }, restored.ComponentOrder);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void SettingsNavigateAndReturnWithoutWritingDefaults()
    {
        using var display = CreateDisplay();
        var applied = new List<HostAppearancePreferences>();
        using var controller = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(SettingsPath), () => [],
            (_, _) => Task.FromResult(ProtocolResult.Success()), () => "none", applied.Add, () => { });
        Assert.Equal(SettingsPage.Applications, controller.GetSnapshot().Navigation.Current);
        Assert.Equal(SettingsPage.Layout, controller.Navigate(SettingsPage.Layout).Navigation.Current);
        Assert.Equal(SettingsPage.Global, controller.Navigate(SettingsPage.Global).Navigation.Current);
        Assert.Equal(SettingsPage.Layout, controller.Back().Navigation.Current);
        Assert.Equal(SettingsPage.Applications, controller.Back().Navigation.Current);
        Assert.False(controller.Back().Navigation.CanGoBack);
        Assert.Single(applied); Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void MovingVisibleRowsPreservesMissingIdentitiesAndDoesNotChangeVisibility()
    {
        var states = Ready("first", "second");
        using var display = CreateDisplay(); display.ApplyBrokerSnapshots(states.Snapshots);
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        string first = "[\"app\",\"group\",\"first\"]", missing = "[\"app\",\"group\",\"missing\"]", second = "[\"app\",\"group\",\"second\"]";
        Assert.True(store.CommitOrder([first, missing, second]).IsSuccess);
        using var controller = new HostSettingsController(display, store, () => states.Snapshots,
            (_, _) => Task.FromResult(ProtocolResult.Success()), () => "none", _ => { }, () => { });
        var identity = display.CurrentComponents.Single(value => value.Identity.LocalId.Value == "second").Identity;
        var moved = controller.MoveComponent(identity, -1);
        Assert.True(moved.IsSuccess);
        Assert.Equal(new[] { "second", "first" }, moved.Value!.Components.Select(value => value.Identity.LocalId.Value));
        Assert.Equal(new[] { second, missing, first }, store.Load().Value!.ComponentOrder);
        Assert.All(moved.Value.Components, value => Assert.False(value.IsVisible));
        Assert.True(controller.SetVisibility(identity, true).IsSuccess);
        Assert.True(controller.GetSnapshot().Components[0].IsVisible);
        Assert.Equal(new[] { second, missing, first }, store.Load().Value!.ComponentOrder);
    }

    private static BrokerStateStore Ready(params string[] components)
    {
        var states = new BrokerStateStore(["app"]);
        states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = "session" });
        Assert.True(states.Handle(new()
        {
            Kind = MessageKind.Declare,
            ApplicationId = "app",
            SessionId = "session",
            Declaration = new("app", [new("group", components.Select(id => new ComponentDeclaration(id, [new("activate")])).ToArray(), [new("panel", [new("activate")])])]),
            State = new(0, components.Select(id => new ComponentReading("group", id, id)).ToArray())
        }).Result!.Accepted);
        return states;
    }

    [Fact]
    public void FailedAppearanceSaveRetainsLastAppliedAndPersistedValues()
    {
        using var display = CreateDisplay(); var applied = new List<HostAppearancePreferences>();
        using var controller = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(SettingsPath), () => [],
            (_, _) => Task.FromResult(ProtocolResult.Success()), () => "Acrylic API configured", applied.Add, () => { });
        var desired = new HostAppearancePreferences(HostTheme.Dark, MaterialKind.Mica, 0.4);
        Assert.True(controller.SetAppearance(desired).IsSuccess);
        using (var blocked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(controller.SetAppearance(new(HostTheme.Light, MaterialKind.None, 0)).IsSuccess);
            Assert.Equal(desired, controller.GetSnapshot().Preferences.Appearance);
            Assert.NotNull(controller.GetSnapshot().Error);
            Assert.Equal(desired, applied[^1]); Assert.Equal(2, applied.Count);
        }
        Assert.Equal(desired, new LocalHostSettingsPreferenceStore(SettingsPath).Load().Value!.Appearance);
    }

    [Fact]
    public async Task ClosingSettingsCancelsRecoveryAndIgnoresItsLateCompletion()
    {
        using var display = CreateDisplay(); var states = Ready("first");
        int notifications = 0, attempts = 0;
        var ignoredCancellation = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(SettingsPath), () => states.Snapshots,
            (_, _) => { attempts++; return ignoredCancellation.Task; }, () => "none", _ => { }, () => notifications++);
        var recovery = controller.RetryAsync("app");
        Assert.Equal("RecoveryBusy", (await controller.RetryAsync("app")).Code);
        controller.Dispose(); controller.Dispose();
        int finalNotifications = notifications;
        Assert.Equal("SettingsClosed", (await recovery.WaitAsync(TimeSpan.FromSeconds(2))).Code);
        ignoredCancellation.SetResult(ProtocolResult.Reject("late", "late"));
        Assert.Equal("SettingsClosed", (await controller.RetryAsync("app")).Code);
        Assert.False(controller.SetAppearance(new()).IsSuccess);
        Assert.Equal(finalNotifications, notifications); Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[],\"Unknown\":true}")]
    [InlineData("{\"Appearance\":{\"Theme\":999},\"ComponentOrder\":[]}")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[\"[1,2,3]\"]}")]
    [InlineData("{\"Appearance\":{},\"ComponentOrder\":[\"[\\\"a\\\",\\\"b\\\",\\\"c\\\"]\",\"[ \\\"a\\\",\\\"b\\\",\\\"c\\\"]\"]}")]
    public void InvalidFilesAreNeverReplacedBySafeDefaults(string contents)
    {
        File.WriteAllText(SettingsPath, contents);
        using var display = CreateDisplay(); int applied = 0;
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        using var controller = new HostSettingsController(display, store, () => [], (_, _) => Task.FromResult(ProtocolResult.Success()),
            () => "none", _ => applied++, () => { });
        Assert.NotNull(controller.GetSnapshot().Error);
        Assert.False(controller.SetAppearance(new()).IsSuccess);
        Assert.False(store.CommitOrder([]).IsSuccess);
        Assert.Equal(contents, File.ReadAllText(SettingsPath)); Assert.Equal(0, applied);
    }

    [Fact]
    public void TemporarilyUnreadableFileIsNotOverwrittenAndCanBeRetriedAfterRecovery()
    {
        var original = new HostAppearancePreferences(HostTheme.Dark, MaterialKind.Solid, 0.9);
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.True(store.CommitAppearance(original).IsSuccess);
        using var display = CreateDisplay(); HostSettingsController controller;
        using (var blocked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            controller = new(display, store, () => [], (_, _) => Task.FromResult(ProtocolResult.Success()), () => "none", _ => { }, () => { });
            Assert.NotNull(controller.GetSnapshot().Error);
            Assert.False(controller.SetAppearance(new()).IsSuccess);
        }
        using (controller)
        {
            Assert.Equal(original, store.Load().Value!.Appearance);
            Assert.True(controller.SetAppearance(original with { Theme = HostTheme.Light }).IsSuccess);
            Assert.Null(controller.GetSnapshot().Error);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void InvalidAppearanceNeverCreatesASettingsFile(double opacity)
    {
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.False(store.CommitAppearance(new(Opacity: opacity)).IsSuccess);
        Assert.False(store.CommitAppearance(new((HostTheme)999)).IsSuccess);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void StableIdentityAndFileBudgetsRejectBeforeReplacingExistingPreferences()
    {
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        Assert.True(store.CommitAppearance(new()).IsSuccess);
        var original = File.ReadAllBytes(SettingsPath);
        Assert.False(store.CommitOrder(Enumerable.Range(0, 4097).Select(i => $"[\"app\",\"group\",\"{i}\"]").ToArray()).IsSuccess);
        Assert.False(store.CommitOrder(["[\"app\",\"group\"]"]).IsSuccess);
        Assert.False(store.CommitOrder([System.Text.Json.JsonSerializer.Serialize(new[] { "app", "group", new string('x', 257) })]).IsSuccess);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        File.WriteAllBytes(SettingsPath, new byte[LocalHostSettingsPreferenceStore.MaximumBytes + 1]);
        Assert.False(store.Load().IsSuccess); Assert.False(store.CommitAppearance(new()).IsSuccess);
        Assert.Equal(LocalHostSettingsPreferenceStore.MaximumBytes + 1, new FileInfo(SettingsPath).Length);
    }

    [Fact]
    public void NavigationHistoryIsBoundedSkipsUnavailablePagesAndIgnoresDuplicateSelection()
    {
        var unavailable = new HashSet<SettingsPage>();
        var navigation = new SettingsNavigation(page => !unavailable.Contains(page));
        navigation.Navigate(SettingsPage.Layout); navigation.Navigate(SettingsPage.Global);
        unavailable.Add(SettingsPage.Layout);
        Assert.Equal(SettingsPage.Applications, navigation.Back().Current);
        Assert.False(navigation.Back().CanGoBack);
        unavailable.Clear();
        for (int i = 0; i < 100; i++) navigation.Navigate(i % 2 == 0 ? SettingsPage.Layout : SettingsPage.Global);
        var before = navigation.Snapshot;
        Assert.Equal(before, navigation.Navigate(before.Current));
        Assert.Equal(before, navigation.Navigate((SettingsPage)999));
        int steps = 0;
        while (navigation.Snapshot.CanGoBack) { navigation.Back(); steps++; }
        Assert.Equal(64, steps);
    }

    [Fact]
    public async Task RecoveryCancellationAndUnknownIdentityAreBoundedAndDoNotChangePreferences()
    {
        using var display = CreateDisplay(); var states = Ready("first");
        using var cancellation = new CancellationTokenSource();
        using var controller = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(SettingsPath), () => states.Snapshots,
            (_, _) => new TaskCompletionSource<ProtocolResult>().Task, () => "none", _ => { }, () => { });
        Assert.Equal("UnknownApplication", (await controller.RetryAsync("unknown")).Code);
        var pending = controller.RetryAsync(null, cancellation.Token);
        cancellation.Cancel();
        Assert.Equal("RecoveryCancelled", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Code);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void WindowCallbackFailureIsIsolatedAndClosedSnapshotsDoNotTouchReleasedOwners()
    {
        using var display = CreateDisplay(); bool released = false;
        using var controller = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(SettingsPath),
            () => released ? throw new ObjectDisposedException("host") : [],
            (_, _) => Task.FromResult(ProtocolResult.Success()), () => released ? throw new ObjectDisposedException("material") : "none",
            _ => throw new InvalidOperationException("window unavailable"), () => throw new InvalidOperationException("view unavailable"));
        Assert.Equal("settings_apply_failed", controller.GetSnapshot().Error!.Code);
        Assert.Equal(SettingsPage.Layout, controller.Navigate(SettingsPage.Layout).Navigation.Current);
        Assert.True(controller.SetAppearance(new(HostTheme.Dark)).IsSuccess);
        Assert.Equal(HostTheme.Dark, new LocalHostSettingsPreferenceStore(SettingsPath).Load().Value!.Appearance.Theme);
        controller.Dispose(); released = true;
        Assert.Equal("settings_closed", controller.GetSnapshot().Error!.Code);
        Assert.Equal(SettingsPage.Layout, controller.Navigate(SettingsPage.Global).Navigation.Current);
    }

    [Fact]
    public void LiveActivitiesNeverAddSettingsRowsAndVisibilityChangesExistingAdmission()
    {
        var states = new BrokerStateStore(["app"]);
        states.Handle(new() { Kind = MessageKind.Welcome, ApplicationId = "app", SessionId = "session" });
        Assert.True(states.Handle(new()
        {
            Kind = MessageKind.Declare,
            ApplicationId = "app",
            SessionId = "session",
            Declaration = new("app", [new("group", [new("island", [new("activate")],
                new(DynamicContentKind.LiveIsland, [new("item", true, new(PresetTemplate.Status, ContentFields.Status, new(WidthTier.Small)))]))],
                [new("panel", [new("activate")])])]),
            State = new(0, [new("group", "island", "ready")])
        }).Result!.Accepted);
        using var display = CreateDisplay(); display.BindActivityPermissions(states); display.ApplyBrokerSnapshots(states.Snapshots);
        using var controller = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(SettingsPath), () => states.Snapshots,
            (_, _) => Task.FromResult(ProtocolResult.Success()), () => "none", _ => { }, () => { });
        var row = Assert.Single(controller.GetSnapshot().Components);
        Assert.False(row.IsVisible); Assert.False(states.GetDisplayPermissions("app")!.Entries[0].Allowed);
        Assert.True(controller.SetVisibility(row.Identity, true).IsSuccess);
        Assert.True(states.Handle(new()
        {
            Kind = MessageKind.State,
            ApplicationId = "app",
            SessionId = "session",
            State = new(1, [new("group", "island", "active")], DynamicEntries:
                [new("group", "island", new([new("one", DateTimeOffset.UtcNow.AddMinutes(1)), new("two", DateTimeOffset.UtcNow.AddMinutes(1))],
                    [new("one", "item", ["one"], new(Status: new("active"))), new("two", "item", ["two"], new(Status: new("active")))]))])
        }).Result!.Accepted);
        display.ApplyBrokerSnapshots(states.Snapshots);
        Assert.Equal(row.Identity, Assert.Single(controller.GetSnapshot().Components).Identity);
        Assert.True(controller.SetVisibility(row.Identity, false).IsSuccess);
        Assert.False(states.GetDisplayPermissions("app")!.Entries[0].Allowed);
        Assert.Equal(2, states.GetSnapshot("app")!.State!.DynamicEntries![0].Content.Activities.Count);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void FailedOrderCommitKeepsCurrentOrderAndNewComponentsAppendWithoutDiscardingMissingOnes()
    {
        var states = Ready("first", "second", "third");
        using var display = CreateDisplay(); display.ApplyBrokerSnapshots(states.Snapshots);
        var store = new LocalHostSettingsPreferenceStore(SettingsPath);
        string second = "[\"app\",\"group\",\"second\"]", absent = "[\"app\",\"group\",\"absent\"]";
        Assert.True(store.CommitOrder([second, absent]).IsSuccess);
        using var controller = new HostSettingsController(display, store, () => states.Snapshots,
            (_, _) => Task.FromResult(ProtocolResult.Success()), () => "none", _ => { }, () => { });
        Assert.Equal(new[] { "second", "first", "third" }, controller.GetSnapshot().Components.Select(value => value.Identity.LocalId.Value));
        var third = display.CurrentComponents.Single(value => value.Identity.LocalId.Value == "third").Identity;
        using (var blocked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(controller.MoveComponent(third, -1).IsSuccess);
            Assert.Equal(new[] { "second", "first", "third" }, controller.GetSnapshot().Components.Select(value => value.Identity.LocalId.Value));
        }
        Assert.True(controller.MoveComponent(third, -1).IsSuccess);
        Assert.Equal(new[] { second, absent, "[\"app\",\"group\",\"third\"]", "[\"app\",\"group\",\"first\"]" }, store.Load().Value!.ComponentOrder);
        using var recreated = new HostSettingsController(display, new LocalHostSettingsPreferenceStore(SettingsPath), () => states.Snapshots,
            (_, _) => Task.FromResult(ProtocolResult.Success()), () => "none", _ => { }, () => { });
        Assert.Equal(new[] { "second", "third", "first" }, recreated.GetSnapshot().Components.Select(value => value.Identity.LocalId.Value));
    }

    [Fact]
    public async Task IndependentAppearanceAndOrderWritersPreserveBothFields()
    {
        var first = new LocalHostSettingsPreferenceStore(SettingsPath); var second = new LocalHostSettingsPreferenceStore(SettingsPath);
        var appearance = new HostAppearancePreferences(HostTheme.Dark, MaterialKind.Solid, 0.25);
        var results = await Task.WhenAll(Task.Run(() => first.CommitAppearance(appearance)),
            Task.Run(() => second.CommitOrder(["[\"app\",\"group\",\"first\"]"])));
        Assert.All(results, result => Assert.True(result.IsSuccess));
        var restored = first.Load().Value!;
        Assert.Equal(appearance, restored.Appearance); Assert.Single(restored.ComponentOrder);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)restored.ComponentOrder)[0] = "changed");
    }

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

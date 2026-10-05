using System.Diagnostics;
using System.Text.Json;
using Mtp.Contracts;
using Mtp.Host;
using Xunit.Abstractions;

namespace Mtp.Communication.Tests;

public sealed class ItemExpansionProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_dynamic_updates_preserve_per_screen_expansion_and_new_items_do_not_inherit_batch_state()
    {
        await using var fixture = await ExpansionFixture.StartAsync(output, activity: false);
        var items = fixture.Items;
        Assert.True(items.UpdateScreens(["screen-a", "screen-b"]).Accepted);
        Assert.Empty(items.GetPresentation("screen-a"));
        await fixture.AdvanceAsync("ready", "added");
        Assert.Equal(4, items.GetPresentation("screen-a").Count);
        Assert.Equal(4, items.GetPresentation("screen-b").Count);
        var ringA = fixture.Item("screen-a", "ordinary", "ring-a");
        var ringB = fixture.Item("screen-a", "ordinary", "ring-b");
        var islandA = fixture.Item("screen-a", "island", "visible-a");
        string session = ringA.SessionId;
        Assert.Equal(DynamicContentKind.OrdinaryItems, ringA.Kind);
        Assert.Equal(DynamicContentKind.LiveIsland, islandA.Kind);
        Assert.True(items.Toggle(ringA.Handle, declaredTargets: true).Result.Accepted);
        Assert.True(items.Toggle(islandA.Handle).Result.Accepted);
        Assert.True(fixture.Item("screen-a", "ordinary", "ring-a").Expanded);
        Assert.True(fixture.Item("screen-a", "ordinary", "ring-b").Expanded);
        Assert.Equal(3, fixture.Item("screen-a", "ordinary", "ring-a").Presentation.Width.Slots);
        Assert.Equal(WidthTier.Large, fixture.Item("screen-a", "island", "visible-a").Presentation.Width.Tier);
        Assert.All(items.GetPresentation("screen-b"), item => Assert.False(item.Expanded));
        Assert.Equal(1, fixture.Host.States.GetSnapshot("dynamic")!.State!.Revision);
        Assert.Equal(0, fixture.Host.Actions.OutstandingCount);

        await fixture.AdvanceAsync("added", "updated");
        Assert.Equal(6, items.GetPresentation("screen-a").Count);
        Assert.True(fixture.Item("screen-a", "ordinary", "ring-a").Expanded);
        Assert.True(fixture.Item("screen-a", "ordinary", "ring-b").Expanded);
        Assert.False(fixture.Item("screen-a", "ordinary", "ring-c").Expanded);
        Assert.True(fixture.Item("screen-a", "island", "visible-a").Expanded);
        Assert.False(fixture.Item("screen-a", "island", "visible-c").Expanded);
        Assert.Equal(ringA.Handle, fixture.Item("screen-a", "ordinary", "ring-a").Handle);
        Assert.Equal(45, fixture.Item("screen-a", "ordinary", "ring-a").Item.Fields.Progress!.Value);
        Assert.Equal(session, fixture.Item("screen-a", "island", "visible-a").SessionId);
        Assert.All(items.GetPresentation("screen-b"), item => Assert.False(item.Expanded));
        Assert.True(items.Toggle(ringA.Handle, declaredTargets: true).Result.Accepted);
        Assert.All(items.GetPresentation("screen-a").Where(item => item.Kind == DynamicContentKind.OrdinaryItems), item => Assert.True(item.Expanded));
        Assert.True(items.Toggle(ringA.Handle, declaredTargets: true).Result.Accepted);
        Assert.All(items.GetPresentation("screen-a").Where(item => item.Kind == DynamicContentKind.OrdinaryItems), item => Assert.False(item.Expanded));
        Assert.True(fixture.Item("screen-a", "island", "visible-a").Expanded);

        var rebuilt = new ItemPresentationController(fixture.Host.States);
        Assert.True(rebuilt.UpdateScreens(["screen-a"]).Accepted);
        Assert.All(rebuilt.GetPresentation("screen-a"), item => Assert.False(item.Expanded));
        Assert.False(rebuilt.Toggle(islandA.Handle).Result.Accepted);
        rebuilt.Close();
        Assert.Equal(0, fixture.Host.Actions.OutstandingCount);
        Assert.Equal(2, fixture.Host.States.GetSnapshot("dynamic")!.State!.Revision);

        await fixture.AdvanceAsync("updated", "rejected");
        Assert.True(fixture.Item("screen-a", "island", "visible-a").Expanded);
        await fixture.AdvanceAsync("rejected", "removed");
        Assert.Equal(2, items.GetPresentation("screen-a").Count);
        Assert.Equal(2, items.GetPresentation("screen-b").Count);
        Assert.True(fixture.Item("screen-a", "island", "visible-a").Expanded);
        Assert.False(items.Toggle(ringB.Handle).Result.Accepted);
        await fixture.AdvanceAsync("removed", "complete");
        Assert.Empty(items.GetPresentation("screen-a"));
        Assert.Empty(items.GetPresentation("screen-b"));
        Assert.False(items.Toggle(islandA.Handle).Result.Accepted);
        Assert.Equal(4, fixture.Host.States.GetSnapshot("dynamic")!.State!.Revision);
        Assert.Equal(0, fixture.Host.Actions.OutstandingCount);
        output.WriteLine("realStateRevisions=0,1,2,3,4; screens=2; ordinaryAndIslandShareController=true; refreshRetained=true; newItemsStartCollapsed=true; mixedBatchExpandsAll=true; fullBatchCollapsesAll=true; removedHandlesRejected=true; businessActionsDispatched=0");
    }

    [Fact]
    public async Task A_new_Host_rebuilds_item_presentation_from_template_defaults_and_rejects_previous_Host_handles()
    {
        ItemInteractionHandle oldHandle;
        await using (var first = await ExpansionFixture.StartAsync(output, activity: false))
        {
            Assert.True(first.Items.UpdateScreens(["screen-a"]).Accepted);
            await first.AdvanceAsync("ready", "added");
            oldHandle = first.Item("screen-a", "ordinary", "ring-a").Handle;
            Assert.True(first.Items.Toggle(oldHandle).Result.Accepted);
            Assert.True(first.Item("screen-a", "ordinary", "ring-a").Expanded);
        }
        await using var second = await ExpansionFixture.StartAsync(output, activity: false);
        Assert.True(second.Items.UpdateScreens(["screen-a"]).Accepted);
        Assert.Empty(second.Items.GetPresentation("screen-a"));
        await second.AdvanceAsync("ready", "added");
        var current = second.Item("screen-a", "ordinary", "ring-a");
        Assert.Equal(oldHandle.Item, current.Handle.Item);
        Assert.NotEqual(oldHandle.Owner, current.Handle.Owner);
        Assert.All(second.Items.GetPresentation("screen-a"), item => Assert.False(item.Expanded));
        Assert.False(second.Items.Toggle(oldHandle).Result.Accepted);
        Assert.Equal(1, second.Host.States.GetSnapshot("dynamic")!.State!.Revision);
        Assert.Equal(0, second.Host.Actions.OutstandingCount);
        output.WriteLine("newHostAndBroker=true; stableItemIdentityPreserved=true; expansionPersisted=false; previousHostHandleRejected=true");
    }

    [Fact]
    public async Task Real_activity_expiry_removes_item_handles_from_both_screens_without_changing_business_revision()
    {
        await using var fixture = await ExpansionFixture.StartAsync(output, activity: true);
        Assert.True(fixture.Items.UpdateScreens(["screen-a", "screen-b"]).Accepted);
        Assert.Empty(fixture.Items.GetPresentation("screen-a"));
        Assert.True(fixture.Host.States.SetEntryDisplayAllowed("dynamic", "main", "island", true).Accepted);
        using var published = await fixture.MarkerAsync("publication-1");
        var first = Assert.Single(fixture.Items.GetPresentation("screen-a"));
        var second = Assert.Single(fixture.Items.GetPresentation("screen-b"));
        Assert.Equal(first.Handle.Item, second.Handle.Item);
        Assert.Equal(DynamicContentKind.LiveIsland, first.Kind);
        await fixture.CommandAsync(1, "expires-short");
        long revision = fixture.Host.States.GetSnapshot("dynamic")!.State!.Revision;
        await fixture.UntilAsync(() => fixture.Items.GetPresentation("screen-a").Count == 0);
        Assert.Empty(fixture.Items.GetPresentation("screen-b"));
        Assert.False(fixture.Items.Toggle(first.Handle).Result.Accepted);
        Assert.False(fixture.Items.Toggle(second.Handle).Result.Accepted);
        Assert.Equal(revision, fixture.Host.States.GetSnapshot("dynamic")!.State!.Revision);
        Assert.True(fixture.Host.States.GetSnapshot("dynamic")!.IsInteractive);
        Assert.Equal(0, fixture.Host.Actions.OutstandingCount);
        output.WriteLine($"expiredItemRemovedFromScreens=2; oldHandlesRejected=true; confirmedRevision={revision}; serviceInteractive=true; businessActionsDispatched=0");
    }

    private sealed class ExpansionFixture : IAsyncDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mtp-item-expansion-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        private readonly List<Process> processes = [];
        private ExpansionFixture(ITestOutputHelper output) { this.output = output; Directory.CreateDirectory(directory); }
        public HostBrokerSession Host { get; private set; } = null!;
        public ItemPresentationController Items { get; private set; } = null!;
        public CancellationToken Token => deadline.Token;

        public static async Task<ExpansionFixture> StartAsync(ITestOutputHelper output, bool activity)
        {
            var fixture = new ExpansionFixture(output);
            try
            {
                fixture.Host = await HostBrokerSession.StartAsync(Path.Combine(AppContext.BaseDirectory, "Broker/Mtp.Broker.dll"), ["dynamic"], fixture.Token);
                fixture.Track(fixture.Host.BrokerProcessId);
                fixture.Items = new ItemPresentationController(fixture.Host.States);
                string relative = activity ? "ActivityProcessProbe/Mtp.Activity.ProcessProbe.dll" : "DynamicContentProcessProbe/Mtp.DynamicContent.ProcessProbe.dll";
                string[] arguments = activity ? [fixture.directory] : ["phases", fixture.directory];
                int pid = await fixture.Host.StartServiceAsync("dynamic", Path.Combine(AppContext.BaseDirectory, relative), fixture.Token, arguments);
                fixture.Track(pid);
                using var ready = await fixture.MarkerAsync("ready");
                Assert.Equal(pid, ready.RootElement.GetProperty("pid").GetInt32());
                Assert.True(fixture.Host.States.GetSnapshot("dynamic")!.IsInteractive);
                if (!activity) Assert.True(fixture.Host.States.SetEntryDisplayAllowed("dynamic", "main", "island", true).Accepted);
                output.WriteLine($"probe={(activity ? "activity" : "dynamic")}; servicePid={pid}; brokerPid={fixture.Host.BrokerProcessId}; fixturePid={Environment.ProcessId}; session={fixture.Host.States.GetSnapshot("dynamic")!.SessionId}");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public HostItemPresentation Item(string screen, string component, string item) =>
            Assert.Single(Items.GetPresentation(screen), value => value.Handle.Item.ComponentId == component && value.Handle.Item.ItemId == item);

        private void Track(int pid)
        {
            var process = Process.GetProcessById(pid);
            processes.Add(process);
            _ = process.Handle;
        }

        public async Task AdvanceAsync(string previous, string next)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "after-" + previous), "continue", Token);
            using var marker = await MarkerAsync(next);
        }

        public async Task CommandAsync(int command, string scenario)
        {
            string path = Path.Combine(directory, "command-" + command);
            await File.WriteAllTextAsync(path + ".tmp", scenario, Token);
            File.Move(path + ".tmp", path);
            using var result = await MarkerAsync("command-" + command + "-result");
            Assert.True(result.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
        }

        public async Task UntilAsync(Func<bool> predicate)
        {
            while (!predicate()) await Task.Delay(10, Token);
        }

        public async Task<JsonDocument> MarkerAsync(string name)
        {
            string path = Path.Combine(directory, name + ".json");
            while (!File.Exists(path))
            {
                string failed = Path.Combine(directory, "failed.json");
                if (File.Exists(failed)) Assert.Fail("Expansion source probe failed: " + await File.ReadAllTextAsync(failed, Token));
                await Task.Delay(10, Token);
            }
            string json = await File.ReadAllTextAsync(path, Token);
            output.WriteLine("itemSourceMarker=" + json);
            return JsonDocument.Parse(json);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Items?.Close();
                if (Host is not null)
                {
                    await Host.DisposeAsync();
                    await Host.DisposeAsync();
                    Assert.Empty(Host.ServiceProcessIds);
                    Assert.Equal(0, Host.Actions.OutstandingCount);
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var process in processes)
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    Assert.True(process.HasExited);
                    output.WriteLine($"expansionPid={process.Id}; processExited=true; exitCode={process.ExitCode}");
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
                deadline.Dispose();
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

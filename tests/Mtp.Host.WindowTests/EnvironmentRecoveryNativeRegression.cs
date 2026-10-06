using System.Reflection;
using Mtp.Host.Islands;
using Mtp.Platform.Core;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Mtp.Host.WindowTests;

/// <summary>Production controller/adapter recovery using an owned native parent, without Explorer manipulation.</summary>
internal static class EnvironmentRecoveryNativeRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await HostConsoleRegression.RunLifetimeAsync(log);
        log("environment-recovery-scope: contentIslandDirect=true; initializationStages=host,source,attach; ownedParent=true; explorerCompatibility=false");

        var root = Path.Combine(AppContext.BaseDirectory, "environment-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var preferences = new LocalComponentDisplayPreferenceStore(Path.Combine(root, "display.json"));
        var display = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")), preferences);
        var loaded = display.Load();
        using var target = new OwnedIslandTarget();
        IslandTarget? frozenTarget = null;
        int captures = 0;
        CoreResult<IslandTarget> Capture(TaskbarDockPreferences value)
        {
            captures++;
            return frozenTarget is null ? target.Capture(value) : CoreResult<IslandTarget>.Success(frozenTarget);
        }
        var controller = new HostConsoleController(display, loaded,
            new LocalTaskbarDockPreferenceStore(Path.Combine(root, "dock.json")), Capture, Path.Combine(root, "evidence"));
        var adapter = Field<IslandDisplayAdapter>(controller, "adapter");
        int notifications = 0;
        int uiThread = Environment.CurrentManagedThreadId;
        adapter.Lost += () => { Check(Environment.CurrentManagedThreadId == uiThread, "Environment callback escaped the UI thread."); notifications++; };
        try
        {
            controller.SetVisibility(false);
            foreach (var stage in new[] { "host", "source", "attach" })
            {
                adapter.FailAfter = stage;
                controller.SetVisibility(true);
                Check(controller.Session.State == IslandDisplayState.Failed && controller.Session.Error?.Code == "island_embed_failed",
                    "Controller did not expose adapter initialization failure: " + stage);
                Check(adapter.Handle == 0 && Subscription(adapter) is null, "Failed initialization retained native host or environment subscription.");
                adapter.FailAfter = null;
                controller.Refresh(); controller.Refresh();
                Check(controller.Session.State == IslandDisplayState.Failed, "Same environment automatically reset its failed attempt budget.");
                controller.Retry();
                Check(controller.Session.State == IslandDisplayState.Embedded && adapter.IsAlive,
                    "Explicit controller Retry did not rebuild the production adapter: " + stage);
                controller.SetVisibility(false);
            }
            log("environment-recovery-controller: initializationStages=host,source,attach; failureOwnerCleanup=true; sameEpochRetryStopped=true; explicitRetry=true");

            controller.SetVisibility(true);
            var oldHandle = adapter.Handle;
            var oldSubscription = Subscription(adapter) ?? throw new InvalidOperationException("Production adapter did not subscribe to text-scale changes.");
            var beforeNotification = notifications;
            // Complete this bounded burst before returning the UI dispatcher to prove coalescing deterministically.
            Task.Run(() => { for (int i = 0; i < 32; i++) oldSubscription(null!, null!); }).GetAwaiter().GetResult();
            await Until(() => notifications > beforeNotification);
            Check(notifications == beforeNotification + 1 && adapter.Handle == oldHandle && controller.Session.State == IslandDisplayState.Embedded,
                "Text-scale notification was unbounded or recreated a healthy native host.");
            Check(ReferenceEquals(oldSubscription, Subscription(adapter)), "Refresh duplicated the environment subscription.");

            frozenTarget = target.Capture(controller.Preferences).Value!;
            beforeNotification = notifications;
            target.Destroy();
            // The native WM_NCDESTROY callback must reach the real controller without a manual Refresh.
            await Until(() => controller.Session.State == IslandDisplayState.Failed);
            Check(notifications > beforeNotification && controller.Session.Error?.Code == "island_lost" && adapter.Handle == 0 && Subscription(adapter) is null,
                "Parent loss did not release the production adapter and subscription.");
            controller.Refresh();
            Check(controller.Session.State == IslandDisplayState.Failed, "Parent loss caused an unbounded same-epoch retry.");
            frozenTarget = null;
            target.Recreate(); controller.Refresh();
            Check(controller.Session.State == IslandDisplayState.Embedded && adapter.IsAlive, "New native target epoch did not automatically recover.");
            var recreated = adapter.Handle;
            beforeNotification = notifications;
            await Task.Run(() => oldSubscription(null!, null!));
            await Task.Delay(80);
            Check(notifications == beforeNotification && adapter.Handle == recreated && adapter.IsAlive,
                "Late old-generation environment callback touched the recreated island.");

            var queuedSubscription = Subscription(adapter)!;
            Task.Run(() => queuedSubscription(null!, null!)).GetAwaiter().GetResult();
            controller.Retry(); // Rebuild while a real dispatcher callback from the old generation is still pending.
            recreated = adapter.Handle;
            beforeNotification = notifications;
            await Task.Delay(80);
            Check(notifications == beforeNotification && adapter.Handle == recreated && adapter.IsAlive,
                "Already queued old-generation environment callback touched the replacement island.");

            controller.SetVisibility(false);
            Check(controller.Session.State == IslandDisplayState.Hidden && Subscription(adapter) is null, "Hidden intent did not clean up resources.");
            target.Recreate(); controller.Refresh(); controller.Retry();
            Check(controller.Session.State == IslandDisplayState.Hidden && !controller.Component!.IsVisible && adapter.Handle == 0,
                "Environment recovery or Retry overwrote the user's hidden preference.");
            var reloaded = new HostDisplayController(new LocalJsonDeclarationSource(Path.Combine(AppContext.BaseDirectory, "declaration.json")), preferences);
            try { reloaded.Load(); Check(reloaded.CurrentComponents.All(value => !value.IsVisible), "Hidden preference was not retained in the isolated preference store."); }
            finally { reloaded.Dispose(); }
            Check(adapter.Close().IsSuccess && adapter.Close().IsSuccess, "Repeated adapter cleanup failed.");
            Check(controller.Shutdown() && controller.Shutdown(), "Repeated controller shutdown retained resources.");
            Check(!adapter.TimerDriverRunning && Subscription(adapter) is null && adapter.Handle == 0 && controller.Session.State == IslandDisplayState.Closed,
                "Controller shutdown retained host, timer or environment subscription.");
            oldSubscription(null!, null!);
            await Task.Delay(80);
            Check(controller.Session.State == IslandDisplayState.Closed && adapter.Handle == 0, "Late callback recovered a closed controller.");
            log($"environment-recovery-pass: elapsedMs={clock.ElapsedMilliseconds}; controllerAdapterContentIsland=true; ownedNativeParent=true; captures={captures}; parentLossCallback=true; delayedCallbackIsolation=true; repeatedCleanup=true; manualRetryBoundary=true; hiddenPreferenceReadback=true; textScaleNotificationsSynthesized=true; uiThreadDispatch=true; notificationCoalescing=true; subscriptionCleanup=true; explorerCompatibility=false; evidence={root}");
        }
        finally { controller.Shutdown(); }

        async Task Until(Func<bool> condition)
        {
            var deadline = clock.Elapsed + TimeSpan.FromSeconds(3);
            while (!condition())
            {
                if (clock.Elapsed > deadline) throw new InvalidOperationException("Production recovery condition did not complete within 3 seconds.");
                await Task.Delay(10);
            }
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static TypedEventHandler<UISettings, object>? Subscription(IslandDisplayAdapter adapter) => Field<TypedEventHandler<UISettings, object>?>(adapter, "textScaleChanged");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

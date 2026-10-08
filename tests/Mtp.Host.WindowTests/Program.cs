using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Environment.ExitCode = 1;
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length == 3 && arguments[1] == "--settings-readback")
        {
            var loaded = new LocalHostSettingsPreferenceStore(arguments[2]).Load();
            if (!loaded.IsSuccess) return;
            File.WriteAllText(arguments[2] + ".readback.json", System.Text.Json.JsonSerializer.Serialize(loaded.Value));
            Environment.ExitCode = 0;
            return;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            new WindowTestApplication();
        });
    }
}

public sealed partial class WindowTestApplication : Application
{
    private static readonly object LogGate = new();
    private static void AppendLog(string value) { lock (LogGate) File.AppendAllText(LogPath, value); }
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "window-tests.log");
    private readonly Type dockType = typeof(WinUiIndependentDockWindowAdapter).Assembly.GetType("Mtp.Host.WinUiTaskbarDockWindow", throwOnError: true)!;
    private readonly HostComponentDisplayModel component = HostComponentDisplayModel.From(
        new Component(new StableIdentity(new StableId("frame-test")), CapabilityState.Available));
    private object? dock;
    private Window? lifetimeWindow;
    private DispatcherTimer? timer;
    private int stage;
    private int samples;
    private readonly Type monitorType = typeof(MainWindow).Assembly.GetType("Mtp.Host.TaskbarEnvironmentMonitor", throwOnError: true)!;
    private object? monitor;
    private nint eventWindow;
    private int eventCount;
    private int stoppedEventCount;
    private readonly System.Diagnostics.Stopwatch eventClock = new();

    public WindowTestApplication()
    {
        InitializeComponent();
        if (!Environment.GetCommandLineArgs().Contains("--hint-input-target", StringComparer.Ordinal))
        {
            File.WriteAllText(LogPath, "Starting real WinUI dock frame regression.\n");
            UnhandledException += (_, e) => AppendLog(e.Exception + "\n");
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var launchArguments = Environment.GetCommandLineArgs();
            int hintTarget = Array.IndexOf(launchArguments, "--hint-input-target");
            if (hintTarget >= 0 && hintTarget + 1 < launchArguments.Length)
            {
                HintInputProcessFixture.RunTarget(launchArguments[hintTarget + 1]);
                return;
            }
            // Keep the application alive while the last dock is closed and recreated.
            lifetimeWindow = new Window();
            if (launchArguments.Contains("--event-production-only", StringComparer.Ordinal))
            {
                await EventProductionRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (launchArguments.Contains("--event-only", StringComparer.Ordinal))
            {
                await EventGroupNativeTests.RunAsync(message => AppendLog(message + "\n"));
                await EventHintNativeTests.RunAsync(message => AppendLog(message + "\n"));
                await HostOwnedFlyoutStackNativeTests.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (launchArguments.Contains("--event-settings-only", StringComparer.Ordinal))
            {
                await EventSettingsNativeTests.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (launchArguments.Contains("--interactive-hint-motion-only", StringComparer.Ordinal))
            {
                await InteractiveHintMotionNativeTests.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (launchArguments.Contains("--interactive-hint-production-only", StringComparer.Ordinal))
            {
                await InteractiveHintProductionRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (launchArguments.Contains("--interactive-hint-only", StringComparer.Ordinal))
            {
                await InteractiveHintWindowNativeTests.RunAsync(message => AppendLog(message + "\n"));
                await InteractiveHintManagerNativeTests.RunAsync(message => AppendLog(message + "\n"));
                await InteractiveHintMotionNativeTests.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (launchArguments.Contains("--hint-window-only", StringComparer.Ordinal))
            {
                await ShortHintNativeTests.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--hint-only", StringComparer.Ordinal))
            {
                await ShortHintNativeTests.RunAsync(message => AppendLog(message + "\n"));
                await ShortHintManagerNativeTests.RunAsync(message => AppendLog(message + "\n"));
                await ShortHintMotionNativeTests.RunAsync(message => AppendLog(message + "\n"));
                await HintInputProcessFixture.RunAsync(message => AppendLog(message + "\n"));
                await ShortHintSettingsNativeTests.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--flyout-scheduling-only", StringComparer.Ordinal))
            {
                await TaskbarFlyoutNativeRegression.RunAnimationSchedulingRegressionAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--flyout-create-only", StringComparer.Ordinal))
            {
                await TaskbarFlyoutNativeRegression.RunCreateDiagnosisAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--organization-only", StringComparer.Ordinal))
            {
                await IslandOrganizationNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--flyout-production-only", StringComparer.Ordinal))
            {
                await TaskbarFlyoutProductionRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--preset-only", StringComparer.Ordinal))
            {
                await PresetNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--flyout-only", StringComparer.Ordinal))
            {
                await TaskbarFlyoutNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timer-only", StringComparer.Ordinal))
            {
                await TimerNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--dynamic-only", StringComparer.Ordinal))
            {
                await DynamicWidthNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--settings-only", StringComparer.Ordinal))
            {
                await SettingsNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--visual-environment-only", StringComparer.Ordinal))
            {
                await VisualEnvironmentNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--environment-recovery-only", StringComparer.Ordinal))
            {
                await EnvironmentRecoveryNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--combination-stability-only", StringComparer.Ordinal))
            {
                // Tiered entry: `--combination-profile smoke|soak` (or MTP_COMBINATION_PROFILE) selects
                // how many rounds run. Both tiers share this fixture, its assertions and its error
                // classes; see CombinationProfile for what each tier covers and cannot cover.
                int profileArg = Array.IndexOf(launchArguments, "--combination-profile");
                if (profileArg >= 0 && profileArg + 1 < launchArguments.Length)
                    Environment.SetEnvironmentVariable("MTP_COMBINATION_PROFILE", launchArguments[profileArg + 1]);
                await CombinationStabilityNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--template-only", StringComparer.Ordinal))
            {
                await TemplateNativeRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--broker-only", StringComparer.Ordinal))
            {
                await BrokerCounterRegression.RunAsync(message => AppendLog(message + "\n"));
                Finish(null);
                return;
            }
            dock = Activator.CreateInstance(dockType, nonPublic: true)!;
            dockType.GetMethod("Hide")!.Invoke(dock, null);
            var initiallyHidden = (nint)(long)dockType.GetProperty("Identity")!.GetValue(dock)!;
            if (Native.IsWindowVisible(initiallyHidden)) throw new InvalidOperationException("New suspended dock flashed before its first show.");
            ShowAndAssert(new PixelRect(-30000, -30000, 360, 48), "initial show");
            RecordEnvironment();
            StartEventTest();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => Advance();
            timer.Start();
        }
        catch (Exception error) { Finish(error); }
    }

    private async void Advance()
    {
        try
        {
            // Cross dispatcher turns to catch styles restored by the presenter, then move/resize/recreate.
            AssertFrame(stage < 2 ? 360 : 400, 48, $"before refresh {stage}");
            switch (stage++)
            {
                case 0:
                    if (eventCount == 0) throw new InvalidOperationException("Native window event did not reach the monitor.");
                    if (!(bool)monitorType.GetMethod("TryStop")!.Invoke(monitor, null)!)
                        throw new InvalidOperationException("Native hooks were not released.");
                    stoppedEventCount = eventCount;
                    Native.NotifyWinEvent(0x800B, eventWindow, 0, 0);
                    AssertRefreshDoesNotRaise();
                    ShowAndAssert(new PixelRect(-30000, -30000, 360, 48), "same bounds");
                    break;
                case 1: ShowAndAssert(new PixelRect(-29900, -30000, 400, 48), "move and resize"); break;
                case 2:
                    AssertHideAndRestore();
                    dockType.GetMethod("Close")!.Invoke(dock, null);
                    dock = null;
                    dock = Activator.CreateInstance(dockType, nonPublic: true)!;
                    ShowAndAssert(new PixelRect(-30000, -30000, 400, 48), "recreated");
                    break;
                default:
                    if (eventCount != stoppedEventCount) throw new InvalidOperationException("Stopped monitor delivered late refresh.");
                    timer!.Stop();
                    await MonitorDragRegression.RunAsync(message => AppendLog(message + "\n"));
                    await PreviewStartupRegression.RunAsync(message => AppendLog(message + "\n"));
                    await ExplorerProbeCleanupRegression.RunAsync(message => AppendLog(message + "\n"));
                    await MainWindowDisplayRegression.RunAsync(message => AppendLog(message + "\n"));
                    await HostConsoleRegression.RunLifetimeAsync(message => AppendLog(message + "\n"));
                    await HostConsoleRegression.RunAsync(message => AppendLog(message + "\n"));
                    await BrokerCounterRegression.RunAsync(message => AppendLog(message + "\n"));
                    Finish(null);
                    break;
            }
        }
        catch (Exception error) { Finish(error); }
    }

    private void ShowAndAssert(PixelRect bounds, string label)
    {
        dockType.GetMethod("Show")!.Invoke(dock, [component, bounds]);
        AssertFrame(bounds.Width, bounds.Height, label);
    }

    private void AssertRefreshDoesNotRaise()
    {
        var sentinel = Native.CreateWindowExW(0x08000080, "STATIC", "MTP z-order test", 0x80000000,
            -29000, -29000, 100, 100, 0, 0, 0, 0);
        if (sentinel == 0) throw new InvalidOperationException("Could not create native sentinel.");
        try
        {
            if (!Native.SetWindowPos(sentinel, -1, -29000, -29000, 100, 100, 0x10 | 0x40))
                throw new InvalidOperationException("Could not position native sentinel.");
            var handle = (nint)(long)dockType.GetProperty("Identity")!.GetValue(dock)!;
            var previous = Native.GetWindow(handle, 3);
            AppendLog($"sentinel={sentinel:X}, dock={handle:X}, previous={previous:X}\n");
            if (!IsAbove(sentinel, handle)) throw new InvalidOperationException("Sentinel setup did not place it above the dock.");
            dockType.GetMethod("Show")!.Invoke(dock, [component, new PixelRect(-30000, -30000, 360, 48)]);
            AppendLog($"refresh z-order: previous={previous:X}, after={Native.GetWindow(handle, 3):X}\n");
            if (!IsAbove(sentinel, handle))
                throw new InvalidOperationException("Repeated layout raised the dock above another topmost window.");
        }
        finally { Native.DestroyWindow(sentinel); }
    }

    private static bool IsAbove(nint above, nint below)
    {
        var current = below;
        for (var i = 0; i < 4096 && current != 0; i++)
        {
            current = Native.GetWindow(current, 3);
            if (current == above) return true;
        }
        return false;
    }

    private void AssertHideAndRestore()
    {
        var handle = (nint)(long)dockType.GetProperty("Identity")!.GetValue(dock)!;
        var foreground = Native.GetForegroundWindow();
        dockType.GetMethod("Hide")!.Invoke(dock, null);
        if (Native.IsWindowVisible(handle)) throw new InvalidOperationException("Hidden dock retains a visible input surface.");
        if ((nint)(long)dockType.GetProperty("Identity")!.GetValue(dock)! != handle)
            throw new InvalidOperationException("Hiding recreated the window.");
        ShowAndAssert(new PixelRect(-30000, -30000, 400, 48), "restored after hide");
        if (!Native.IsWindowVisible(handle)) throw new InvalidOperationException("Dock did not reappear.");
        if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("Visibility transition stole foreground focus.");
        AppendLog("PASS: hide/restore preserves identity, foreground, and borderless client area.\n");
    }

    private void StartEventTest()
    {
        eventWindow = Native.CreateWindowExW(0, "STATIC", "MTP event test", 0x80000000, -28000, -28000, 100, 100, 0, 0, 0, 0);
        if (eventWindow == 0) throw new InvalidOperationException("Could not create event test window.");
        monitor = Activator.CreateInstance(monitorType,
            new Func<Action, bool>(action => lifetimeWindow!.DispatcherQueue.TryEnqueue(() => action())),
            new Action(() =>
            {
                eventCount++;
                AppendLog($"native event delivered: {eventClock.Elapsed.TotalMilliseconds:F1} ms\n");
            }),
            new Func<nint>(() => eventWindow));
        if (monitorType.GetProperty("Error")!.GetValue(monitor) is not null)
            throw new InvalidOperationException("Native event subscription failed.");
        eventClock.Start();
        Native.NotifyWinEvent(0x800B, eventWindow, 0, 0);
    }

    private void RecordEnvironment()
    {
        var environmentType = typeof(MainWindow).Assembly.GetType("Mtp.Host.Win32TaskbarDockEnvironment", throwOnError: true)!;
        var environment = Activator.CreateInstance(environmentType, new object?[] { null });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = environmentType.GetMethod("Capture")!.Invoke(environment, new object?[] { null })!;
        var snapshot = result.GetType().GetProperty("Value")!.GetValue(result);
        AppendLog($"environment read: {clock.Elapsed.TotalMilliseconds:F1} ms, " + System.Text.Json.JsonSerializer.Serialize(snapshot) + "\n");
    }

    private void AssertFrame(int width, int height, string label)
    {
        var handle = (nint)(long)dockType.GetProperty("Identity")!.GetValue(dock)!;
        if (!Native.GetClientRect(handle, out var client))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        var style = (long)Native.GetWindowLongPtrW(handle, -16);
        var extendedStyle = (long)Native.GetWindowLongPtrW(handle, -20);
        AppendLog($"{label}: style=0x{style:X8} ex=0x{extendedStyle:X8} client={client.Right}x{client.Bottom}\n");
        if ((style & 0x00CF0000) != 0 || (extendedStyle & 0x00020301) != 0 || client.Right != width || client.Bottom != height)
            throw new InvalidOperationException($"Native frame returned at {label}; expected full client area {width}x{height}.");
        if ((extendedStyle & 0x08000080) != 0x08000080)
            throw new InvalidOperationException("Dock lost NOACTIVATE or TOOLWINDOW style.");
        samples++;
    }

    private void Finish(Exception? error)
    {
        timer?.Stop();
        try { if (dock is not null) dockType.GetMethod("Close")!.Invoke(dock, null); }
        catch (Exception cleanupError) { error ??= cleanupError; }
        try { (monitor as IDisposable)?.Dispose(); }
        catch (Exception cleanupError) { error ??= cleanupError; }
        if (eventWindow != 0) Native.DestroyWindow(eventWindow);
        // The combination fixture owns an exit-code contract (see FixtureOutcome): its failures carry a
        // class, and collapsing them all to 1 here would throw away the classification the ticket asks
        // for. Every other scenario keeps the historical 0/1.
        if (error is FixtureFailureException classified)
        {
            AppendLog($"FAIL[{FixtureOutcome.Describe(classified.FailureClass)}]: code={classified.Code}; {error}\n");
            Environment.ExitCode = FixtureOutcome.ExitCode(classified.FailureClass);
        }
        else
        {
            AppendLog(error is null ? $"PASS: {samples} native frame samples.\n" : $"FAIL: {error}\n");
            Environment.ExitCode = error is null ? 0 : 1;
        }
        lifetimeWindow?.Close();
        Exit();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint window, int index);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern void NotifyWinEvent(uint kind, nint window, int objectId, int childId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint window, out Rect rect);
    }
}

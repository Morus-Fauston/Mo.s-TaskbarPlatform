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
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ => new WindowTestApplication());
    }
}

public sealed partial class WindowTestApplication : Application
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "window-tests.log");
    private readonly Type dockType = typeof(HostComponentDisplayModel).Assembly.GetType("Mtp.Host.WinUiTaskbarDockWindow", throwOnError: true)!;
    private readonly HostComponentDisplayModel component = HostComponentDisplayModel.From(
        new Component(new StableIdentity(new StableId("frame-test")), CapabilityState.Available));
    private object? dock;
    private Window? lifetimeWindow;
    private DispatcherTimer? timer;
    private int stage;
    private int samples;
    private readonly Type monitorType = typeof(HostComponentDisplayModel).Assembly.GetType("Mtp.Host.TaskbarEnvironmentMonitor", throwOnError: true)!;
    private object? monitor;
    private nint eventWindow;
    private int eventCount;
    private int stoppedEventCount;
    private readonly System.Diagnostics.Stopwatch eventClock = new();

    public WindowTestApplication()
    {
        InitializeComponent();
        File.WriteAllText(LogPath, "Starting real WinUI dock frame regression.\n");
        UnhandledException += (_, e) => File.AppendAllText(LogPath, e.Exception + "\n");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Contains("--child-material"))
        {
            new ChildMaterialRegression(this).Run();
            return;
        }
        try
        {
            // Keep the application alive while the last dock is closed and recreated.
            lifetimeWindow = new Window();
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

    private void Advance()
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
            File.AppendAllText(LogPath, $"sentinel={sentinel:X}, dock={handle:X}, previous={previous:X}\n");
            if (!IsAbove(sentinel, handle)) throw new InvalidOperationException("Sentinel setup did not place it above the dock.");
            dockType.GetMethod("Show")!.Invoke(dock, [component, new PixelRect(-30000, -30000, 360, 48)]);
            File.AppendAllText(LogPath, $"refresh z-order: previous={previous:X}, after={Native.GetWindow(handle, 3):X}\n");
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
        File.AppendAllText(LogPath, "PASS: hide/restore preserves identity, foreground, and borderless client area.\n");
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
                File.AppendAllText(LogPath, $"native event delivered: {eventClock.Elapsed.TotalMilliseconds:F1} ms\n");
            }),
            new Func<nint>(() => eventWindow));
        if (monitorType.GetProperty("Error")!.GetValue(monitor) is not null)
            throw new InvalidOperationException("Native event subscription failed.");
        eventClock.Start();
        Native.NotifyWinEvent(0x800B, eventWindow, 0, 0);
    }

    private void RecordEnvironment()
    {
        var environmentType = dockType.Assembly.GetType("Mtp.Host.Win32TaskbarDockEnvironment", throwOnError: true)!;
        var environment = Activator.CreateInstance(environmentType, new object?[] { null });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = environmentType.GetMethod("Capture")!.Invoke(environment, new object?[] { null })!;
        var snapshot = result.GetType().GetProperty("Value")!.GetValue(result);
        File.AppendAllText(LogPath, $"environment read: {clock.Elapsed.TotalMilliseconds:F1} ms, " + System.Text.Json.JsonSerializer.Serialize(snapshot) + "\n");
    }

    private void AssertFrame(int width, int height, string label)
    {
        var handle = (nint)(long)dockType.GetProperty("Identity")!.GetValue(dock)!;
        if (!Native.GetClientRect(handle, out var client))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        var style = (long)Native.GetWindowLongPtrW(handle, -16);
        var extendedStyle = (long)Native.GetWindowLongPtrW(handle, -20);
        File.AppendAllText(LogPath, $"{label}: style=0x{style:X8} ex=0x{extendedStyle:X8} client={client.Right}x{client.Bottom}\n");
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
        File.AppendAllText(LogPath, error is null ? $"PASS: {samples} native frame samples.\n" : $"FAIL: {error}\n");
        Environment.ExitCode = error is null ? 0 : 1;
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

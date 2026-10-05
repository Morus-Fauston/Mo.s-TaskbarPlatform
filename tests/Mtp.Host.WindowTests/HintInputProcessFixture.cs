using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mtp.Host.Flyouts;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>A separately owned real Button target. Only the root runner may execute this desktop fixture.</summary>
internal static class HintInputProcessFixture
{
    private static Target? target;

    // Called by the child application's OnLaunched branch, before any ordinary regression starts.
    internal static void RunTarget(string directory) => target = new Target(Path.GetFullPath(directory));

    internal static async Task RunAsync(Action<string> log)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "hint-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        nint savedForeground = FlyoutNative.GetForegroundWindow();
        Check(FlyoutNative.GetCursorPos(out var savedCursor), "Could not preserve cursor position.");
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Current test executable is unavailable."))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--hint-input-target");
        start.ArgumentList.Add(directory);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned hint input target.");
        TargetReady? ready = null;
        bool forcedExit = false;
        try
        {
            await Until(() =>
            {
                CheckTargetRunning();
                ready = Read<TargetReady>(Path.Combine(directory, "target.json"));
                return ready is not null;
            }, TimeSpan.FromSeconds(8), "Owned cross-process Button did not become ready.");
            var info = ready!;
            var hwnd = new nint(info.Handle);
            Check(info.ProcessId == child.Id && FlyoutNative.IsWindow(hwnd), "Ready file does not identify the owned live process/window.");
            FlyoutNative.GetWindowThreadProcessId(hwnd, out uint actualProcess);
            Check(actualProcess == child.Id && FlyoutNative.Root(hwnd) == hwnd && info.Bounds.IsValid,
                "Cross-process target HWND ownership or Button bounds are invalid.");
            var point = new FlyoutNative.Point { X = info.Bounds.X + info.Bounds.Width / 2, Y = info.Bounds.Y + info.Bounds.Height / 2 };
            var before = Read<Counts>(Path.Combine(directory, "counts.json")) ?? throw new InvalidOperationException("Target input counters are unavailable.");
            Check(before == new Counts(0, 0, 0), "The target received input before this fixture's single click.");
            Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(point)) == hwnd,
                "Owned Button is occluded; refusing to send input to another window.");
            await ShortHintNativeTests.VerifyCrossProcessPassThroughAsync(hwnd, info.Bounds, async () =>
            {
                CheckTargetRunning();
                FlyoutNative.GetWindowThreadProcessId(hwnd, out uint currentProcess);
                Check(currentProcess == child.Id && FlyoutNative.Root(FlyoutNative.WindowFromPoint(point)) == hwnd,
                    "Target identity or hit-test root changed; refusing to send input.");
                Check(SetCursorPos(point.X, point.Y), "Could not position cursor over the owned Button.");
                Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(point)) == hwnd,
                    "Target became occluded after cursor movement; refusing to click.");
                var input = new[] { new Input { Type = 0, Flags = 0x0002 }, new Input { Type = 0, Flags = 0x0004 } };
                uint sent = SendInput(2, input, Marshal.SizeOf<Input>());
                if (sent == 1) SendInput(1, [new Input { Type = 0, Flags = 0x0004 }], Marshal.SizeOf<Input>());
                Check(sent == 2, "SendInput did not submit the complete press/release pair.");
                await Until(() =>
                {
                    CheckTargetRunning();
                    var counts = Read<Counts>(Path.Combine(directory, "counts.json"));
                    return counts is not null && counts.Presses >= 1 && counts.Releases >= 1 && counts.Clicks >= 1;
                }, TimeSpan.FromSeconds(3), "Actual pointer press/release/Click did not reach the separate Button process.");
                // One short observation window catches duplicate delivery after the initial Click.
                await Task.Delay(250);
                var after = Read<Counts>(Path.Combine(directory, "counts.json"));
                Check(after == new Counts(1, 1, 1), "Cross-process pointer delivery was not exactly one press, release and Click.");
                Check(ReadText(Path.Combine(directory, "clicks.txt")) == "1", "Independent Click readback differs from the routed-event counts.");
                log("hint-cross-process-input: " + JsonSerializer.Serialize(new
                { parentPid = Environment.ProcessId, targetPid = child.Id, targetHwnd = info.Handle, info.Bounds, before, after, sent }));
            }, log);
            AtomicWrite(Path.Combine(directory, "exit.request"), "stop");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(child.ExitCode == 0, "Owned input target exited with an error.");
            var ended = Read<TargetExit>(Path.Combine(directory, "exit.json"));
            Check(ended is { WindowClosed: true, Counts: { Presses: 1, Releases: 1, Clicks: 1 } } && ended.ProcessId == child.Id,
                "Target did not confirm HWND release and final input counts.");
            Check(!FlyoutNative.IsWindow(hwnd), "Owned target HWND survived child exit.");
            log("hint-cross-process-pass: " + JsonSerializer.Serialize(new
            { targetPid = child.Id, child.ExitCode, presses = 1, releases = 1, clicks = 1, windowClosed = true, evidence = directory }));

            void CheckTargetRunning()
            {
                string errorPath = Path.Combine(directory, "target-error.txt");
                if (File.Exists(errorPath)) throw new InvalidOperationException("Owned target failure: " + File.ReadAllText(errorPath));
                Check(!child.HasExited, "Owned input target exited before verification.");
            }
        }
        finally
        {
            try
            {
                if (!child.HasExited)
                {
                    AtomicWrite(Path.Combine(directory, "exit.request"), "stop");
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException)
                    {
                        forcedExit = true;
                        child.Kill();
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                }
                log("hint-cross-process-cleanup: " + JsonSerializer.Serialize(new { targetPid = child.Id, exited = child.HasExited, child.ExitCode, forcedExit }));
            }
            finally
            {
                SetCursorPos(savedCursor.X, savedCursor.Y);
                if (savedForeground != 0 && FlyoutNative.IsWindow(savedForeground)) SetForegroundWindow(savedForeground);
            }
        }
    }

    private sealed class Target
    {
        private readonly string directory;
        private readonly Window window;
        private readonly Button button;
        private readonly nint hwnd;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly Stopwatch deadline = Stopwatch.StartNew();
        private readonly PointerEventHandler pressed, released;
        private readonly RoutedEventHandler clicked;
        private Counts counts = new(0, 0, 0);
        private bool published, stopping;

        internal Target(string directory)
        {
            this.directory = directory;
            Directory.CreateDirectory(directory);
            button = new Button
            {
                Content = "MTP 跨进程穿透目标", HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(24)
            };
            // A root container preserves the Button's margin in TransformToVisual(root).
            // When the Button was itself Content, transforming it to itself omitted that
            // margin and reported a rectangle extending into non-interactive client space.
            var root = new Grid();
            root.Children.Add(button);
            window = new Window { Title = "MTP owned hint input target", Content = root };
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            pressed = (_, _) => Update(counts with { Presses = counts.Presses + 1 });
            released = (_, _) => Update(counts with { Releases = counts.Releases + 1 });
            clicked = (_, _) => Update(counts with { Clicks = counts.Clicks + 1 });
            button.AddHandler(UIElement.PointerPressedEvent, pressed, true);
            button.AddHandler(UIElement.PointerReleasedEvent, released, true);
            button.Click += clicked;
            window.Closed += Closed;
            var work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            window.AppWindow.MoveAndResize(new(work.X + 64, work.Y + 64, 480, 240));
            window.AppWindow.Show(false);
            // The input target is a normal application, not a competing topmost overlay.
            // Activating a topmost target would intentionally raise it over the hints.
            if (!SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0013)) throw FlyoutNative.Error("PositionInputTarget");
            Update(counts);
            timer.Tick += Tick;
            timer.Start();
        }

        private void Update(Counts value)
        {
            if (stopping) return;
            counts = value;
            AtomicWrite(Path.Combine(directory, "clicks.txt"), counts.Clicks.ToString(CultureInfo.InvariantCulture));
            AtomicWrite(Path.Combine(directory, "counts.json"), JsonSerializer.Serialize(counts));
        }

        private void Tick(object? sender, object args)
        {
            try
            {
                if (deadline.Elapsed >= TimeSpan.FromSeconds(25)) throw new TimeoutException("Input target exceeded its 25 second lifetime.");
                if (File.Exists(Path.Combine(directory, "exit.request"))) { Stop(0); return; }
                if (!published && button.IsLoaded && button.ActualWidth > 0 && button.ActualHeight > 0)
                {
                    var bounds = TaskbarFlyoutNativeRegression.ElementScreenBounds(button, window);
                    AtomicWrite(Path.Combine(directory, "target.json"), JsonSerializer.Serialize(new TargetReady(Environment.ProcessId, hwnd.ToInt64(), bounds)));
                    published = true;
                }
            }
            catch (Exception error)
            {
                AtomicWrite(Path.Combine(directory, "target-error.txt"), error.ToString());
                Stop(2);
            }
        }

        private void Stop(int exitCode)
        {
            if (stopping) return;
            stopping = true;
            Environment.ExitCode = exitCode;
            timer.Stop();
            timer.Tick -= Tick;
            button.RemoveHandler(UIElement.PointerPressedEvent, pressed);
            button.RemoveHandler(UIElement.PointerReleasedEvent, released);
            button.Click -= clicked;
            window.Closed -= Closed;
            window.Close();
            AtomicWrite(Path.Combine(directory, "exit.json"), JsonSerializer.Serialize(new TargetExit(Environment.ProcessId, !FlyoutNative.IsWindow(hwnd), counts)));
            target = null;
            Application.Current.Exit();
        }

        private void Closed(object sender, WindowEventArgs args)
        {
            AtomicWrite(Path.Combine(directory, "target-error.txt"), "Owned target was closed before the exit signal.");
            Stop(2);
        }
    }

    private sealed record TargetReady(int ProcessId, long Handle, PixelRect Bounds);
    private sealed record Counts(int Presses, int Releases, int Clicks);
    private sealed record TargetExit(int ProcessId, bool WindowClosed, Counts Counts);
    private static T? Read<T>(string path) where T : class => File.Exists(path) ? JsonSerializer.Deserialize<T>(ReadText(path)) : null;
    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static void AtomicWrite(string path, string value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, value);
        File.Move(temporary, path, overwrite: true);
    }
    private static async Task Until(Func<bool> condition, TimeSpan limit, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= limit) throw new TimeoutException(failure);
            await Task.Delay(25);
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int X;
        [FieldOffset(12)] public int Y;
        [FieldOffset(16)] public uint MouseData;
        [FieldOffset(20)] public uint Flags;
        [FieldOffset(24)] public uint Time;
        [FieldOffset(32)] public nuint Extra;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

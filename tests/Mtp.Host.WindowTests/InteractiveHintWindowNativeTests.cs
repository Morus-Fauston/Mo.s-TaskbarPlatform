using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;
using Windows.Graphics.Imaging;

namespace Mtp.Host.WindowTests;

/// <summary>Serialized real input against an owned second process; never substitutes for human acceptance.</summary>
internal static class InteractiveHintWindowNativeTests
{
    internal static async Task RunAsync(Action<string> log)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "interactive-hint-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        nint savedForeground = FlyoutNative.GetForegroundWindow();
        Check(FlyoutNative.GetCursorPos(out var savedCursor), "Cannot preserve cursor.");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--hint-input-target"); start.ArgumentList.Add(directory);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Owned input target failed to start.");
        InteractiveHintWindow? window = null;
        var driver = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        EventHandler<object> drive = (_, _) => window?.RefreshInteraction();
        driver.Tick += drive;
        bool pointerDown = false;
        try
        {
            TargetReady? ready = null;
            await Until(() => (ready = Read<TargetReady>(Path.Combine(directory, "target.json"))) is not null,
                "Owned cross-process target was not ready.", 8);
            nint target = new(ready!.Handle);
            FlyoutNative.GetWindowThreadProcessId(target, out uint targetPid);
            Check(ready.ProcessId == child.Id && targetPid == child.Id && FlyoutNative.IsWindow(target), "Invalid target identity.");
            var states = new BrokerStateStore(["interactive-test"]);
            InteractiveHintManagerNativeTests.Connect(states, "native-window");
            var entry = new TemplateEntryReference("group", TemplateEntryKind.Hint, "adjust");
            int actions = 0, expansions = 0, backgroundActions = 0;
            using var images = new RegisteredImageCache();
            using var foreground = new TemplateRenderer(new TemplateInteractionController(states, "interactive-test", entry,
                (_, _, _, _) => { actions++; return Task.FromResult(ProtocolResult.Reject("FixtureDenied", "夹具拒绝以验证回退")); },
                "compact", _ => { expansions++; return ProtocolResult.Success(); }), images, TemplateRenderSurface.HintForeground);
            using var background = new TemplateRenderer(new TemplateInteractionController(states, "interactive-test", entry,
                (_, _, _, _) => { backgroundActions++; return Task.FromResult(ProtocolResult.Reject("UnexpectedBackgroundAction", "background")); }),
                images, TemplateRenderSurface.HintBackground);
            var clock = new ManualClock();
            var lifetime = new FlyoutLifetime(1, FlyoutLifetimeKind.InteractiveHint, clock: clock);
            var active = new HashSet<string>(StringComparer.Ordinal);
            window = new(foreground, background, () => { },
                (kind, value) => log(kind + ": " + JsonSerializer.Serialize(value)), (source, value) =>
                {
                    if (value) active.Add(source); else active.Remove(source);
                    lifetime.SetInteraction(1, source, value);
                    log("interactive-native-source: " + JsonSerializer.Serialize(new { source, active = value }));
                });
            var bounds = ready.Bounds;
            nint before = FlyoutNative.GetForegroundWindow();
            window.Apply(bounds, 1); window.Show(); lifetime.MarkVisible(1);
            driver.Start();
            await Until(() => foreground.GetInteractiveRegions().Count == 2, "Native control regions did not load.");
            Check(FlyoutNative.GetForegroundWindow() == before, "Automatic interactive hint stole activation.");
            Check(FlyoutNative.Bounds(window.Handle) == FlyoutNative.Bounds(window.BackgroundHandle), "Hint native pair is not aligned.");
            var slider = (Slider)foreground.GetNodeElement("value")!;
            var expand = (Button)foreground.GetNodeElement("expand")!;
            var passiveSlider = Descendants(background).OfType<Slider>().Single();
            Check(passiveSlider.Opacity == 0 && FrameworkElementAutomationPeer.CreatePeerForElement(passiveSlider) is null,
                "Background produced a duplicate visible or accessible slider.");
            Check(background.GetInteractiveRegions().Count == 0, "Background produced input regions.");
            Check(ReferenceEquals(foreground.CapturedSnapshot, background.CapturedSnapshot),
                "Foreground and background did not render the exact same confirmed snapshot.");
            var blank = new FlyoutNative.Point { X = (int)bounds.Right - 8, Y = bounds.Y + 8 };
            Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(blank)) == target,
                "Empty hint area did not pass native hit testing through to the other process.");
            await Click(blank, target);
            await Until(() => CountsNow()?.Clicks == 1, "Empty-area click did not reach the separate process.");
            Check(CountsNow() == new Counts(1, 1, 1), "Empty-area input was not exactly once.");
            await SaveNativeAsync(bounds, Path.Combine(directory, "interactive-pair-native.png"));
            log("interactive-native-pass: no activation; one foreground control tree; blank-area cross-process input=1/1/1; screenshot=" + directory);

            var thumb = Descendants(slider).OfType<Thumb>().FirstOrDefault(x => x.ActualWidth > 0 && x.ActualHeight > 0)
                ?? throw new InvalidOperationException("Slider thumb did not load.");
            var thumbPoint = Center(thumb, window);
            Check(SetCursorPos(thumbPoint.X, thumbPoint.Y), "Cannot position pointer over owned slider.");
            Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(thumbPoint)) == window.Handle, "Slider region did not hit foreground.");
            SendMouse(0x0002); pointerDown = true;
            await Until(() => active.Contains("capture") && active.Contains("pressed"), "Real slider press did not retain pointer capture.");
            var outside = new FlyoutNative.Point { X = (int)bounds.Right - 8, Y = bounds.Y + bounds.Height / 2 };
            Check(SetCursorPos(outside.X, outside.Y), "Cannot drag captured pointer outside control region.");
            await Task.Delay(80);
            Check(active.Contains("capture"), "Leaving the control region lost real drag capture.");
            clock.Advance(TimeSpan.FromSeconds(20));
            Check(!lifetime.IsExpired(1), "Captured drag did not pause timeout.");
            SendMouse(0x0004); pointerDown = false;
            await Until(() => actions > 0 && !active.Contains("capture") && !active.Contains("pressed") && slider.Value == 40,
                "Slider release did not submit and roll rejected preview back to confirmed 40.");
            Check(CountsNow() == new Counts(1, 1, 1), "Captured drag leaked input to the lower process.");
            Check(backgroundActions == 0, "Background renderer duplicated a business action.");
            log("interactive-native-pass: real slider capture outside clipped region; 20-second pause; final release submits; failure rolls back to confirmed 40; background actions=0");

            var expandPoint = Center(expand, window);
            await Click(expandPoint, window.Handle);
            await Until(() => expansions == 1, "Real expand-button click did not reach the controlled navigation callback.");
            Check(CountsNow() == new Counts(1, 1, 1), "Foreground button click leaked to the other process.");
            Check(SetCursorPos(blank.X, blank.Y), "Cannot clear pointer hover before keyboard test.");
            FlyoutNative.GetCursorPos(out var hoverPoint);
            log("interactive-native-hover-exit-probe: " + JsonSerializer.Serialize(new
            {
                point = new { hoverPoint.X, hoverPoint.Y },
                hitRoot = FlyoutNative.Root(FlyoutNative.WindowFromPoint(hoverPoint)).ToInt64(),
                foreground = window.Handle.ToInt64(), target = target.ToInt64(),
                activeSources = active.OrderBy(x => x).ToArray()
            }));
            await Until(() => !active.Contains("hover"), "Pointer hover source did not clear outside controls.");
            Check(window.FocusFirst(), "Explicit keyboard entry failed to obtain foreground and control focus.");
            await Until(() => active.Contains("keyboard"), "Keyboard focus did not pause lifetime.");
            clock.Advance(TimeSpan.FromSeconds(20));
            Check(!lifetime.IsExpired(1), "Keyboard focus did not pause timeout.");
            await SaveNativeAsync(bounds, Path.Combine(directory, "interactive-keyboard-native.png"));
            await Click(blank, target);
            await Until(() => CountsNow()?.Clicks == 2 && active.Count == 0, "Leaving keyboard focus did not release the final interaction source.");
            clock.Advance(TimeSpan.FromMilliseconds(4999));
            foreground.Refresh(); background.Refresh(); window.RefreshAppearance();
            Check(!lifetime.IsExpired(1), "Ordinary refresh or focus exit shortened the five-second idle period.");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Check(lifetime.IsExpired(1), "Last interaction exit did not restart exactly five seconds.");
            log("interactive-native-pass: button captured input once; explicit keyboard focus pauses; outside click releases; 4999ms alive and 5000ms expires after last interaction");

            slider.IsEnabled = false; window.RefreshAppearance();
            var sliderPoint = Center(slider, window);
            Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(sliderPoint)) == target, "Disabled control retained a native input region.");
            await Click(sliderPoint, target);
            await Until(() => CountsNow()?.Clicks == 3, "Disabled region did not pass through to the other process.");
            expand.Visibility = Visibility.Collapsed; window.RefreshAppearance();
            Check(foreground.GetInteractiveRegions().Count == 0 && active.Count == 0, "Hidden/disabled controls retained regions or interaction sources.");
            nint front = window.Handle, back = window.BackgroundHandle;
            int closeAttempts = 0;
            window.CloseForegroundForTesting = value =>
            {
                if (++closeAttempts == 1) throw new COMException("Injected interactive foreground Close failure.");
                value.Close();
            };
            Check(!window.TryClose().IsSuccess && window.IsAlive && FlyoutNative.IsWindow(front) && !FlyoutNative.IsWindow(back),
                "Failed foreground close did not retain its owner while cleaning the other HWND.");
            Check(window.TryClose().IsSuccess && !window.IsAlive && closeAttempts == 2 && !FlyoutNative.IsWindow(front),
                "Explicit native pair cleanup retry failed.");
            Check(window.TryClose().IsSuccess && backgroundActions == 0, "Repeated pair cleanup was not idempotent.");
            File.WriteAllText(Path.Combine(directory, "exit.request"), "stop");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(child.ExitCode == 0 && !FlyoutNative.IsWindow(target) && CountsNow() == new Counts(3, 3, 3),
                "Cross-process target final delivery/cleanup did not match exactly three clicks.");
            log("interactive-native-pass: disabled/hidden regions removed; native pair Close failure retained owner then retry released; target input=3/3/3; all owned HWNDs/process exited");

            Counts? CountsNow() => Read<Counts>(Path.Combine(directory, "counts.json"));
        }
        finally
        {
            driver.Stop(); driver.Tick -= drive;
            if (pointerDown) SendMouse(0x0004);
            if (window is not null) { window.CloseForegroundForTesting = null; Check(window.TryClose().IsSuccess, "Interactive native fixture window cleanup failed."); }
            if (!child.HasExited)
            {
                File.WriteAllText(Path.Combine(directory, "exit.request"), "stop");
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            }
            SetCursorPos(savedCursor.X, savedCursor.Y);
            if (savedForeground != 0 && FlyoutNative.IsWindow(savedForeground)) SetForegroundWindow(savedForeground);
        }
    }
    private static FlyoutNative.Point Center(FrameworkElement element, InteractiveHintWindow window)
    {
        var bounds = element.TransformToVisual(window.RootForTesting).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        double scale = GetDpiForWindow(window.Handle) / 96d;
        return new() { X = window.LastBounds.X + (int)Math.Round((bounds.X + bounds.Width / 2) * scale),
            Y = window.LastBounds.Y + (int)Math.Round((bounds.Y + bounds.Height / 2) * scale) };
    }
    private static async Task Click(FlyoutNative.Point point, nint expected)
    {
        Check(SetCursorPos(point.X, point.Y), "SetCursorPos failed.");
        Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(point)) == expected, "Click target is not the expected owned HWND.");
        SendMouse(0x0002); SendMouse(0x0004); await Task.Delay(80);
    }
    private static void SendMouse(uint flags)
    { Check(SendInput(1, [new Input { Type = 0, Flags = flags }], Marshal.SizeOf<Input>()) == 1, "Real mouse input was not delivered."); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(value, i))) yield return child;
    }
    private static async Task Until(Func<bool> condition, string message, int seconds = 4)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new InvalidOperationException(message); await Task.Delay(20); }
    }
    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(file);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record TargetReady(int ProcessId, long Handle, PixelRect Bounds);
    private sealed record Counts(int Presses, int Releases, int Clicks);
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan value) => ticks += value.Ticks;
    }
    private static async Task SaveNativeAsync(PixelRect bounds, string path)
    {
        Check((long)bounds.Width * bounds.Height <= 4 * 1024 * 1024, "Native screenshot budget exceeded.");
        nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
        byte[] pixels;
        try
        {
            Check(screen != 0, "Screen DC unavailable.");
            memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            Check(memory != 0 && bitmap != 0, "Native screenshot allocation failed.");
            previous = SelectObject(memory, bitmap);
            Check(BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40CC0020), "Native pair screenshot failed.");
            SelectObject(memory, previous); previous = 0;
            var info = new BitmapInfo { Size = 40, Width = bounds.Width, Height = -bounds.Height, Planes = 1, Bits = 32 };
            pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
            Check(GetDIBits(screen, bitmap, 0, (uint)bounds.Height, pixels, ref info, 0) == bounds.Height, "Native pair screenshot readback failed.");
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
        using var stream = new FileStream(path, FileMode.Create);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bounds.Width, (uint)bounds.Height, 96, 96, pixels);
        await encoder.FlushAsync();
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(20)] public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize;
        public int XPixels, YPixels; public uint ColorsUsed, ColorsImportant, Color;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
}

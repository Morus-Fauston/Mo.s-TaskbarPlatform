using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Measures what the layered-window route costs per update. This is the number that decides whether
/// an embedded widget can animate, and it is a different question from whether the route works at
/// all.
///
/// A native WinUI window composites on the GPU: the CPU does not touch a frame, so motion, hover and
/// transitions cost the UI thread nothing. The layered route replaces the window's content with an
/// application-supplied bitmap, so every single update pays render &#8594; read the pixels back out of
/// video memory &#8594; copy &#8594; submit, on the UI thread. The cost is certainly higher; the question is
/// whether it lands inside a frame budget.
///
/// The run reports three things so the total can be read honestly: an idle dispatcher round trip to
/// subtract, a render-only pass that both routes pay for, and the full chain. Everything is measured
/// on the UI thread against a real taskbar child window that is actually on screen, and the run
/// refuses to print a number when the work did not really happen.
///
/// Run with <c>--render-latency</c>.
/// </summary>
internal sealed class RenderLatencyProbe(Application app)
{
    private const int WarmupRuns = 15;

    /// <summary>
    /// Enough samples that the scheduler-clock quantisation stops dominating: the thread-time counter
    /// advances in 15.625 ms steps, so the batch average is only meaningful to about 15.625/N ms.
    /// </summary>
    private const int MeasuredRuns = 600;
    private const uint UlwAlpha = 0x00000002;
    private const uint WsChild = 0x40000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExLayered = 0x00080000;
    private const int GwlStyle = -16;

    /// <summary>Realistic shapes: a component strip, the taskbar strip itself, and a flyout panel.</summary>
    private static readonly (string Label, int Width, int Height)[] Sizes =
    [
        ("component strip", 260, 56),
        ("taskbar strip", 360, 48),
        ("flyout panel", 360, 240),
    ];

    private readonly string log = Path.Combine(AppContext.BaseDirectory, "render-latency.log");
    private readonly List<string> report = [];

    private Window? lifetime;
    private DispatcherQueue? queue;
    private nint taskbar;
    private nint classAtom;
    private nint memoryDc;
    private nint bitmap;
    private nint bitmapBits;
    private nint layerWindow;
    private Window? renderer;
    private int idleSamples;
    private double idleCpu;

    private readonly Native.WndProc windowProc = static (window, message, wParam, lParam) =>
        Native.DefWindowProcW(window, message, wParam, lParam);

    public async Task Run()
    {
        File.WriteAllText(log, "Layered route update cost probe (CPU time first, wall clock second)\n");
        try
        {
            lifetime = new Window();
            queue = lifetime.DispatcherQueue;
            taskbar = Native.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == 0)
            {
                throw new InvalidOperationException("Shell_TrayWnd was not found; this probe needs a visible taskbar.");
            }

            RegisterWindowClass();

            await MeasureIdleRoundTrip();

            foreach (var (label, width, height) in Sizes)
            {
                await MeasureSize(label, width, height);
            }

            WriteReport();
            Finish(null);
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    /// <summary>
    /// What one empty dispatcher round trip costs. Every sample below is taken across a round trip,
    /// so this is the floor that has to be subtracted before any of the numbers mean anything.
    /// </summary>
    private async Task MeasureIdleRoundTrip()
    {
        var samples = new List<double>();
        var cpuStart = 0.0;
        for (var index = 0; index < WarmupRuns + MeasuredRuns; index++)
        {
            if (index == WarmupRuns)
            {
                cpuStart = CpuNow();
            }

            var start = Stopwatch.GetTimestamp();
            await NextTick();
            var elapsed = Stopwatch.GetTimestamp();
            if (index >= WarmupRuns)
            {
                samples.Add(Milliseconds(start, elapsed));
            }
        }

        idleSamples = samples.Count;
        idleCpu = (CpuNow() - cpuStart) / MeasuredRuns;
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"idle round trip: {Summarize(samples)}; CPU {idleCpu:0.000} ms per trip (batch average)\n"));
    }

    private async Task MeasureSize(string label, int width, int height)
    {
        var panel = BuildPanel(width, height);
        renderer = new Window { Content = panel };
        renderer.AppWindow.Resize(new SizeInt32(width, height));
        panel.UpdateLayout();

        var sizeLine = await DescribeLayout(panel, width, height);

        // One render up front to learn the physical size, which is what the bitmap has to match.
        var probe = new RenderTargetBitmap();
        await probe.RenderAsync(panel);
        var pixelWidth = probe.PixelWidth;
        var pixelHeight = probe.PixelHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new InvalidOperationException("RenderTargetBitmap reported a zero-sized render for " + label + ".");
        }

        // The layer window is attached to the taskbar before its first submit. Submitting while the
        // window is still top level makes the layer permanently stop showing, which would turn this
        // measurement into a measurement of nothing.
        CreateLayerWindow(width, height, pixelWidth, pixelHeight);

        var renderOnly = await MeasureRenderOnly(panel);
        var chain = await MeasureFullChain(panel, pixelWidth, pixelHeight);

        report.Add(string.Empty);
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"=== {label}: {width}x{height} dip -> {pixelWidth}x{pixelHeight} px ({sizeLine}) ==="));
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  {pixelWidth * pixelHeight * 4:N0} bytes per frame"));

        // CPU time is the figure that matters for a background program. Wall clock cannot tell
        // "worked for 12 ms" apart from "slept for 12 ms waiting for the compositor", and only the
        // first one costs the machine anything.
        var totalCpu = chain.Cpu;
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  CPU per update : {totalCpu:0.000} ms  (render {chain.RenderCpu:0.000} + readback {chain.ReadbackCpu:0.000} "
            + $"+ copy {chain.CopyCpu:0.000} + submit {chain.SubmitCpu:0.000})"));
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  wall clock     : {Summarize(chain.Total)}"));
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  render only    : {renderOnly.Cpu:0.000} ms CPU, wall {Summarize(renderOnly.Wall)}"));
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  readback+copy+submit together: {chain.ReadbackCpu + chain.CopyCpu + chain.SubmitCpu:0.000} ms CPU"));
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  share of wall clock that is CPU: {Share(totalCpu, Median(chain.Total)):P0}"));
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  duty cycle: {totalCpu / 10:0.000}% of one core at 1 update/s, {totalCpu * 3:0.00}% at 30 updates/s"));

        DestroyLayerWindow();
        renderer.Close();
        renderer = null;
    }

    private static double Share(double part, double whole) => whole <= 0 ? 0 : part / whole;

    private async Task<(List<double> Wall, double Cpu)> MeasureRenderOnly(FrameworkElement panel)
    {
        var target = new RenderTargetBitmap();
        var samples = new List<double>();
        var cpuStart = 0.0;
        for (var index = 0; index < WarmupRuns + MeasuredRuns; index++)
        {
            await NextTick();
            if (index == WarmupRuns)
            {
                cpuStart = CpuNow();
            }

            var start = Stopwatch.GetTimestamp();
            await target.RenderAsync(panel);
            var elapsed = Stopwatch.GetTimestamp();
            if (index >= WarmupRuns)
            {
                samples.Add(Milliseconds(start, elapsed));
            }
        }

        return (samples, (CpuNow() - cpuStart) / MeasuredRuns);
    }

    /// <summary>
    /// CPU time consumed on this thread, in milliseconds, taken from the Win32 thread times rather
    /// than a stopwatch. Wall clock cannot distinguish "worked for 17 ms" from "slept for 17 ms
    /// waiting for the compositor", and for a background program only the first one costs anything.
    /// </summary>
    private static double CpuNow()
    {
        if (!Native.GetThreadTimes(Native.GetCurrentThread(), out var creation, out var exit, out var kernel, out var user))
        {
            throw new InvalidOperationException("GetThreadTimes failed: " + Native.LastError());
        }

        return ToMilliseconds(kernel) + ToMilliseconds(user);
    }

    private static double ToMilliseconds(long fileTime) =>
        fileTime / 10000.0;

    private async Task<ChainSamples> MeasureFullChain(FrameworkElement panel, int pixelWidth, int pixelHeight)
    {
        var expected = pixelWidth * pixelHeight * 4;
        var target = new RenderTargetBitmap();
        var total = new List<double>();
        var size = new Native.Size { Width = pixelWidth, Height = pixelHeight };
        var source = new Native.Point { X = 0, Y = 0 };
        var blend = new Native.BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };

        // Batch CPU accumulators. GetThreadTimes is far coarser than one update, so only the totals
        // over a whole batch are meaningful.
        var totalCpu = 0.0;
        var renderCpu = 0.0;
        var readbackCpu = 0.0;
        var copyCpu = 0.0;
        var submitCpu = 0.0;

        for (var index = 0; index < WarmupRuns + MeasuredRuns; index++)
        {
            await NextTick();

            // The markers are per iteration. Setting them once before the batch would fold the whole
            // warmup period into the first measured sample.
            var measured = index >= WarmupRuns;
            var cpuStart = measured ? CpuNow() : 0;

            var start = Stopwatch.GetTimestamp();
            await target.RenderAsync(panel);
            var afterRender = Stopwatch.GetTimestamp();
            var afterRenderCpu = measured ? CpuNow() : 0;

            var buffer = await target.GetPixelsAsync();
            var afterReadback = Stopwatch.GetTimestamp();
            var afterReadbackCpu = measured ? CpuNow() : 0;

            if (buffer is null || buffer.Length == 0)
            {
                throw new InvalidOperationException("GetPixelsAsync handed back an empty buffer; the timing would be meaningless.");
            }

            if (buffer.Length != expected)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The buffer is {buffer.Length} bytes, not {expected} for {pixelWidth}x{pixelHeight}; the timing would be meaningless."));
            }

            var pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(pixels);
            }

            var afterCopy = Stopwatch.GetTimestamp();
            var afterCopyCpu = measured ? CpuNow() : 0;

            Marshal.Copy(pixels, 0, bitmapBits, pixels.Length);
            var afterDib = Stopwatch.GetTimestamp();

            // A null destination point keeps the current position. Passing a point silently moves the
            // window, which is how an earlier probe ended up measuring a window that was off screen.
            var ok = Native.UpdateLayeredWindow(layerWindow, 0, 0, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
            var afterSubmit = Stopwatch.GetTimestamp();

            if (!ok)
            {
                throw new InvalidOperationException("UpdateLayeredWindow failed: " + Native.LastError());
            }

            if (measured)
            {
                var afterSubmitCpu = CpuNow();
                total.Add(Milliseconds(start, afterSubmit));
                totalCpu += afterSubmitCpu - cpuStart;
                renderCpu += afterRenderCpu - cpuStart;
                readbackCpu += afterReadbackCpu - afterRenderCpu;
                copyCpu += afterCopyCpu - afterReadbackCpu;
                submitCpu += afterSubmitCpu - afterCopyCpu;
            }
        }

        return new ChainSamples(
            total,
            totalCpu / MeasuredRuns,
            renderCpu / MeasuredRuns,
            readbackCpu / MeasuredRuns,
            copyCpu / MeasuredRuns,
            submitCpu / MeasuredRuns);
    }

    /// <summary>
    /// Wall clock and CPU time for one update.
    ///
    /// CPU is a batch average, not a per-sample figure: <c>GetThreadTimes</c> only advances on the
    /// scheduler clock, about every 15.6 ms, while one update costs far less than that. Timing a
    /// single update therefore reads 0.00 ms almost always and 15.63 ms occasionally, which is the
    /// clock granularity rather than the work. Summing a whole batch and dividing by its size is the
    /// only way to get a number out of a counter this coarse.
    /// </summary>
    private sealed record ChainSamples(
        List<double> Total,
        double Cpu,
        double RenderCpu,
        double ReadbackCpu,
        double CopyCpu,
        double SubmitCpu);

    /// <summary>
    /// Proves the layer is really on screen. Without this the whole run could be timing a window that
    /// was never visible, and the numbers would look exactly the same.
    /// </summary>
    private void VerifyOnScreen()
    {
        if (!Native.GetWindowRect(layerWindow, out var rect))
        {
            throw new InvalidOperationException("The layer window rectangle could not be read.");
        }

        var x = (rect.Left + rect.Right) / 2;
        var y = (rect.Top + rect.Bottom) / 2;
        var onScreen = SampleScreen(x, y);
        var frame = Native.DwmGetWindowAttribute(layerWindow, 9, out Native.Rect bounds, Marshal.SizeOf<Native.Rect>());
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"  check: layer rect ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}), centre ({x},{y}) reads 0x{onScreen:X6}, "
            + $"siblings above {SiblingsAbove(layerWindow)}, visible {Native.IsWindowVisible(layerWindow)}, "
            + $"dwm frame hresult {frame} ({bounds.Left},{bounds.Top})-({bounds.Right},{bounds.Bottom})\n"));

        if (onScreen != 0x0000FF)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The layer should be opaque red at its centre but the screen reads 0x{onScreen:X6}; a hidden or buried window would produce meaningless timings."));
        }
    }

    /// <summary>
    /// How many siblings sit above this window; zero means nothing can cover it.
    /// </summary>
    private static int SiblingsAbove(nint window)
    {
        var count = 0;
        var current = Native.GetWindow(window, 3);
        while (current != 0 && count < 64)
        {
            count++;
            current = Native.GetWindow(current, 3);
        }

        return count;
    }

    private static uint SampleScreen(int x, int y)
    {
        var screen = Native.GetDC(0);
        try
        {
            return Native.GetPixel(screen, x, y);
        }
        finally
        {
            Native.ReleaseDC(0, screen);
        }
    }

    private async Task<string> DescribeLayout(FrameworkElement panel, int width, int height)
    {
        await NextTick();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(renderer!);
        var dpi = Native.GetDpiForWindow(handle);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{width}x{height} dip, window dpi {dpi}, {dpi / 96.0:0.00}x, panel {panel.ActualWidth:0.#}x{panel.ActualHeight:0.#} dip");
        File.AppendAllText(log, "layout: " + line + "\n");
        return line;
    }

    private static Grid BuildPanel(int width, int height)
    {
        // A Grid, not a StackPanel: a StackPanel sizes itself to its children in the stacking
        // direction and ignores Height, so the render comes out at the text's natural height instead
        // of the requested one. That silently changes the size being measured.
        var panel = new Grid { Width = width, Height = height, Background = null };
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Child = new TextBlock
            {
                Text = "MTP 组件",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        return panel;
    }

    private void RegisterWindowClass()
    {
        var description = new Native.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<Native.WndClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(windowProc),
            Instance = Native.GetModuleHandleW(null),
            BackgroundBrush = 0,
            ClassName = "MtpRenderLatencyProbe",
        };
        classAtom = Native.RegisterClassExW(ref description);
        if (classAtom == 0)
        {
            throw new InvalidOperationException("RegisterClassExW failed: " + Native.LastError());
        }
    }

    /// <summary>
    /// Creates the layered child window and attaches it to the taskbar before the first submit.
    /// </summary>
    private void CreateLayerWindow(int width, int height, int pixelWidth, int pixelHeight)
    {
        Native.GetWindowRect(taskbar, out var rect);

        // Child coordinates are relative to the parent's client area, not the screen.
        var x = (rect.Right - rect.Left) * 3 / 4 - (pixelWidth / 2);
        var y = ((rect.Bottom - rect.Top) - pixelHeight) / 2;

        memoryDc = Native.CreateCompatibleDC(0);
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
            Width = pixelWidth,
            Height = -pixelHeight,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };
        bitmap = Native.CreateDIBSection(memoryDc, ref header, 0, out bitmapBits, 0, 0);
        if (bitmap == 0 || bitmapBits == 0)
        {
            throw new InvalidOperationException("The layered bitmap could not be created.");
        }

        _ = Native.SelectObject(memoryDc, bitmap);

        layerWindow = Native.CreateWindowExW(
            WsExLayered, "MtpRenderLatencyProbe", string.Empty, WsChild | WsVisible,
            x, y, pixelWidth, pixelHeight, taskbar, 0, Native.GetModuleHandleW(null), 0);
        if (layerWindow == 0)
        {
            throw new InvalidOperationException("CreateWindowExW failed: " + Native.LastError());
        }

        Native.GetWindowRect(layerWindow, out var afterCreate);
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"  create: asked {width}x{height} dip -> {pixelWidth}x{pixelHeight} px at parent({x},{y}); "
            + $"taskbar client {TaskbarClientWidth()}x{TaskbarClientHeight()}; after create ({afterCreate.Left},{afterCreate.Top})-({afterCreate.Right},{afterCreate.Bottom}) "
            + $"= {afterCreate.Right - afterCreate.Left}x{afterCreate.Bottom - afterCreate.Top}\n"));

        // HWND_TOP, and deliberately no SWP_NOZORDER: the two cancel each other out, and a reparented
        // child then sits behind the taskbar's own content window while looking perfectly healthy.
        var moved = Native.SetWindowPos(layerWindow, 0, x, y, pixelWidth, pixelHeight, 0x0040);
        Native.GetWindowRect(layerWindow, out var afterMove);
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"  place: SetWindowPos={moved} err={Native.LastError()}; "
            + $"after place ({afterMove.Left},{afterMove.Top})-({afterMove.Right},{afterMove.Bottom}) "
            + $"= {afterMove.Right - afterMove.Left}x{afterMove.Bottom - afterMove.Top}\n"));

        // Fill the layer with opaque red once, so the first submit paints something checkable. This is
        // also the first submit, and it happens after SetParent.
        var count = pixelWidth * pixelHeight;
        var pixels = new byte[count * 4];
        for (var index = 0; index < count; index++)
        {
            pixels[(index * 4) + 2] = 255;
            pixels[(index * 4) + 3] = 255;
        }

        Marshal.Copy(pixels, 0, bitmapBits, pixels.Length);
        var size = new Native.Size { Width = pixelWidth, Height = pixelHeight };
        var source = new Native.Point { X = 0, Y = 0 };
        var blend = new Native.BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (!Native.UpdateLayeredWindow(layerWindow, 0, 0, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha))
        {
            throw new InvalidOperationException("The first UpdateLayeredWindow failed: " + Native.LastError());
        }

        Native.DwmFlush();

        // Verify here, while the layer is still the opaque red that was just submitted. Checking later
        // would sample whatever the panel bitmap put on screen, and a correct transparent panel is
        // invisible on purpose -- so the check would fail even though everything worked.
        VerifyLayerCoversTaskbar();
    }

    /// <summary>
    /// Proves the layer really is on screen, with opaque red covering the taskbar.
    ///
    /// Without this the whole run could be timing a window that was never visible, and the numbers
    /// would look exactly the same as a real one. Case B of the layered-child probe is the control
    /// this copies: opaque red must cover the taskbar, otherwise everything downstream is meaningless.
    /// </summary>
    private void VerifyLayerCoversTaskbar()
    {
        if (!Native.GetWindowRect(layerWindow, out var rect))
        {
            throw new InvalidOperationException("The layer window rectangle could not be read.");
        }

        var x = (rect.Left + rect.Right) / 2;
        var y = (rect.Top + rect.Bottom) / 2;
        var onScreen = SampleScreen(x, y);
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"  control: layer rect ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}), centre ({x},{y}) reads 0x{onScreen:X6}, "
            + $"siblings above {SiblingsAbove(layerWindow)}, visible {Native.IsWindowVisible(layerWindow)}\n"));

        if (onScreen != 0x0000FF)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The layer should be opaque red at its centre but the screen reads 0x{onScreen:X6}; a hidden or buried window would produce meaningless timings."));
        }
    }

    private int TaskbarClientWidth()
    {
        _ = Native.GetClientRect(taskbar, out var rect);
        return rect.Right - rect.Left;
    }

    private int TaskbarClientHeight()
    {
        _ = Native.GetClientRect(taskbar, out var rect);
        return rect.Bottom - rect.Top;
    }

    private void DestroyLayerWindow()
    {
        if (layerWindow != 0)
        {
            _ = Native.SetParent(layerWindow, 0);
            _ = Native.SetWindowLongPtrW(layerWindow, GwlStyle, unchecked((nint)(int)(WsPopup | WsVisible)));
            _ = Native.DestroyWindow(layerWindow);
            layerWindow = 0;
        }

        if (bitmap != 0)
        {
            _ = Native.DeleteObject(bitmap);
            bitmap = 0;
        }

        if (memoryDc != 0)
        {
            _ = Native.DeleteDC(memoryDc);
            memoryDc = 0;
        }

        Native.DwmFlush();
    }

    private void WriteReport()
    {
        report.Add(string.Empty);
        report.Add(string.Empty);
        report.Add("=== how to read this (CPU time, screen pixels; not human acceptance) ===");
        report.Add("This is a background program, so the figure that matters is how much it occupies, not");
        report.Add("how long it takes. CPU time says what it occupies; wall clock says how long the user");
        report.Add("waited, and a thread that is asleep waiting for the compositor costs nothing.");
        report.Add("A native WinUI window never calls RenderAsync at all: it composites on the GPU, so an");
        report.Add("update costs the UI thread near zero CPU.");
        report.Add($"Samples per line: {MeasuredRuns} measured after {WarmupRuns} warmup, {idleSamples} idle samples.");
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"CPU method: GetThreadTimes summed over the whole batch and divided by {MeasuredRuns}; the counter"));
        report.Add("only advances on the scheduler clock (~15.6 ms), so a single update reads as 0 and a");
        report.Add("per-sample average would be meaningless.");
        report.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"Empty dispatcher round trip for reference: {idleCpu:0.000} ms CPU."));

        foreach (var line in report)
        {
            File.AppendAllText(log, line + "\n");
        }
    }

    /// <summary>
    /// One empty dispatcher round trip. The await is the delivered-notification hop, which is the
    /// same hop a production update takes, so subtracting this leaves the work itself.
    /// </summary>
    private Task NextTick()
    {
        var completion = new TaskCompletionSource();
        if (!queue!.TryEnqueue(() => completion.SetResult()))
        {
            completion.SetException(new InvalidOperationException("The dispatcher queue rejected the round trip."));
        }

        return completion.Task;
    }

    private static double Milliseconds(long from, long to) =>
        (to - from) * 1000.0 / Stopwatch.Frequency;

    private static List<double> Sorted(List<double> samples) => [.. samples.OrderBy(value => value)];

    private static double Median(List<double> samples) => Sorted(samples)[samples.Count / 2];

    private static double Percentile(List<double> samples, double fraction)
    {
        var sorted = Sorted(samples);
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * fraction))];
    }

    private static string Summarize(List<double> samples) => string.Create(
        CultureInfo.InvariantCulture,
        $"median {Median(samples):0.00} ms, p95 {Percentile(samples, 0.95):0.00} ms, "
        + $"max {samples.Max():0.00} ms, min {samples.Min():0.00} ms");

    private void Finish(Exception? error)
    {
        if (error is not null)
        {
            File.AppendAllText(log, "FAILED: " + error + "\n");
            report.Add("FAILED: " + error.Message);
        }

        try
        {
            DestroyLayerWindow();
            if (classAtom != 0)
            {
                _ = Native.UnregisterClassW("MtpRenderLatencyProbe", Native.GetModuleHandleW(null));
            }

            renderer?.Close();
            lifetime?.Close();
        }
        finally
        {
            Environment.ExitCode = error is null ? 0 : 1;
            app.Exit();
        }
    }

    private static class Native
    {
        internal delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Size
        {
            internal int Width;
            internal int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BlendFunction
        {
            internal byte BlendOp;
            internal byte BlendFlags;
            internal byte SourceConstantAlpha;
            internal byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width;
            internal int Height;
            internal ushort Planes;
            internal ushort BitCount;
            internal uint Compression;
            internal uint SizeImage;
            internal int XPelsPerMeter;
            internal int YPelsPerMeter;
            internal uint ColorsUsed;
            internal uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WndClassEx
        {
            internal uint Size;
            internal uint Style;
            internal nint WindowProc;
            internal int ClassExtra;
            internal int WindowExtra;
            internal nint Instance;
            internal nint Icon;
            internal nint Cursor;
            internal nint BackgroundBrush;
            internal string? MenuName;
            internal string ClassName;
            internal nint SmallIcon;
        }

        internal static string LastError() => Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowW(string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WndClassEx description);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool UnregisterClassW(string className, nint instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UpdateLayeredWindow(
            nint window, nint destinationDc, nint destination, ref Size size,
            nint sourceDc, ref Point source, uint colorKey, ref BlendFunction blend, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetParent(nint window, nint parent);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetWindowRect(nint window, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetClientRect(nint window, out Rect rect);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint window);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint window, uint command);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint CreateCompatibleDC(nint dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint SelectObject(nint dc, nint value);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteObject(nint value);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteDC(nint dc);

        [DllImport("user32.dll")]
        internal static extern nint GetDC(nint window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(nint window, nint dc);

        [DllImport("gdi32.dll")]
        internal static extern uint GetPixel(nint dc, int x, int y);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? name);

        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetThreadTimes(nint thread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
    }
}

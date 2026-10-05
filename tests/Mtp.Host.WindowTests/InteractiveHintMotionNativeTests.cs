using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mtp.Contracts;
using Mtp.Host.Flyouts;
using Mtp.Host.Templates;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class InteractiveHintMotionNativeTests
{
    internal static async Task RunAsync(Action<string> log)
    {
        var budget = Stopwatch.StartNew();
        var states = new BrokerStateStore(["interactive-test"]);
        InteractiveHintManagerNativeTests.Connect(states, "motion");
        using var images = new RegisteredImageCache();
        using var groups = new TaskbarFlyoutManager(states, (key, id, navigate) => new TemplateRenderer(
            new TemplateInteractionController(states, key.ApplicationId, key.Entry,
                (_, _, _, _) => Task.FromResult(ProtocolResult.Success()), id, navigate), images),
            (kind, value) => { if (kind.Contains("failed")) log(kind + ": " + JsonSerializer.Serialize(value)); });
        var clock = new ManualClock();
        using var independent = CreateManager();
        using var associated = CreateManager();
        var frames = new List<Frame>();
        var primary = DisplayArea.Primary.WorkArea;
        Check(primary.Width >= 650 && primary.Height >= 500, "Motion fixture requires a 650x500 work area.");
        var work = new PixelRect(primary.X + 32, primary.Y + 32, 580, 420);
        var target = new Window { Title = "MTP owned interactive motion background", Content = new Button { Content = "Owned motion target" } };
        nint targetHandle = WinRT.Interop.WindowNative.GetWindowHandle(target);
        if (target.AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(false, false);
        FlyoutNative.NoActivate(targetHandle, true); FlyoutNative.Position(targetHandle, work); target.AppWindow.Show(false);
        uint dpi = GetDpiForWindow(targetHandle);
        Check(dpi is >= 48 and <= 960, "Owned motion target DPI is unavailable.");
        int margin = (int)Math.Round(16 * dpi / 96d), gap = (int)Math.Round(8 * dpi / 96d);
        log("interactive-motion-environment: " + JsonSerializer.Serialize(new { dpi, work, margin, gap }));
        var entry = new TemplateEntryReference("group", TemplateEntryKind.Hint, "adjust");
        var request = new ShortHintRequest(new("interactive-test", entry), "motion", "motion-screen", work, dpi);
        var ownedHandles = new List<nint> { targetHandle };
        try
        {
            independent.ReducedMotionOverride = true;
            independent.ApplySettings(new(new(), [], Hints: new(FlyoutPosition.BottomLeft)));
            Check(independent.Show(request).Accepted, "Independent motion hint failed to open.");
            var observed = independent.Inspect().Single();
            var window = independent.WindowForTesting(observed.Generation)!;
            ownedHandles.Add(window.Handle); ownedHandles.Add(window.BackgroundHandle);
            await Until(() => Renderer(window).GetInteractiveRegions().Count == 2, budget, "Initial interactive regions did not load.");
            Capture("bottom-left", window);
            var start = window.LastBounds;
            independent.ReducedMotionOverride = false;
            independent.ApplySettings(new(new(), [], Hints: new(FlyoutPosition.TopRight)));
            for (int i = 0; i < 2; i++)
            { clock.Advance(TimeSpan.FromMilliseconds(55)); await Task.Delay(35); Capture("toward-top-right", window); }
            var intermediate = window.LastBounds;
            Check(intermediate.X > start.X && intermediate.Y < start.Y && intermediate.Y > work.Y + margin,
                "Position change did not present a real intermediate native frame.");
            independent.ApplySettings(new(new(), [], Hints: new(FlyoutPosition.BottomRight)));
            Check(window.LastBounds == intermediate, "Retarget at a fixed clock jumped from its presented native frame.");
            for (int i = 0; i < 4; i++)
            { clock.Advance(TimeSpan.FromMilliseconds(55)); await Task.Delay(35); Capture("retarget-bottom-right", window); }
            Check(window.LastBounds.Right == work.Right - margin && window.LastBounds.Bottom == work.Bottom - margin,
                "Retarget did not reach the requested bottom-right terminal rectangle.");
            independent.ReducedMotionOverride = true;
            independent.ApplySettings(new(new(), [], Hints: new(FlyoutPosition.TopLeft)));
            Capture("reduced-top-left", window);
            Check(window.LastBounds.X == work.X + margin && window.LastBounds.Y == work.Y + margin,
                "Reduced motion did not immediately apply terminal position.");
            Check(independent.TryClose().IsSuccess && !independent.HasResources, "Independent native pair cleanup failed.");

            groups.ReducedMotionOverride = true;
            var panel = new TemplateEntryReference("group", TemplateEntryKind.TaskbarFlyout, "details");
            var anchor = new PixelRect((int)work.Right - 64, (int)work.Bottom - 8, 48, 8);
            var groupRequest = new FlyoutOpenRequest(new("interactive-test", panel), "motion", "motion-screen", anchor, work, dpi);
            Check(groups.Toggle(groupRequest).IsSuccess, "Associated motion owner failed to open.");
            var group = groups.Inspect().Single();
            ownedHandles.AddRange(group.Windows.Select(x => x.Handle));
            associated.ReducedMotionOverride = true;
            Check(associated.Show(request with { Owner = new(group.ScreenId, group.Generation, group.Entry, group.SessionId) }).Accepted,
                "Associated motion hint failed to open.");
            var linked = associated.Inspect().Single();
            var linkedWindow = associated.WindowForTesting(linked.Generation)!;
            ownedHandles.Add(linkedWindow.Handle); ownedHandles.Add(linkedWindow.BackgroundHandle);
            await Until(() => Renderer(linkedWindow).GetInteractiveRegions().Count == 2, budget, "Associated regions did not load.");
            var ownerStart = FlyoutNative.Bounds(group.Windows.Single().Handle);
            groups.ReducedMotionOverride = false;
            Check(groups.UpdateEnvironment(group.ScreenId, anchor with { X = anchor.X - 100 }, work, dpi).IsSuccess,
                "Associated owner movement was rejected.");
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(35);
                var owner = FlyoutNative.Bounds(group.Windows.Single().Handle);
                Capture("associated-owner-move", linkedWindow, owner);
                Check(linkedWindow.LastBounds.X == owner.X && linkedWindow.LastBounds.Width == owner.Width &&
                    owner.Y - linkedWindow.LastBounds.Bottom == gap, "Interactive hint detached from the actual animated owner frame.");
            }
            Check(frames.Any(x => x.Phase == "associated-owner-move" && x.Owner is { } owner &&
                owner.X != ownerStart.X && owner.X != ownerStart.X - 100), "Associated movement has no intermediate owner frame.");
            Check(associated.TryClose().IsSuccess && groups.TryClose().IsSuccess, "Associated motion cleanup failed.");
            target.Close();
            Check(ownedHandles.All(x => !FlyoutNative.IsWindow(x)), "Motion fixture retained an owned HWND.");
            Check(budget.Elapsed <= TimeSpan.FromSeconds(8), "Interactive motion exceeded eight seconds.");
            foreach (var frame in frames) log("interactive-motion-frame: " + JsonSerializer.Serialize(frame));
            log($"PASS: {frames.Count} interactive native pair frames; independent retarget, control and blank hit regions, owner-following motion, reduced motion; resources=0; elapsed={budget.ElapsedMilliseconds}ms.");
        }
        catch
        {
            foreach (var frame in frames) log("interactive-motion-frame-before-failure: " + JsonSerializer.Serialize(frame));
            throw;
        }
        finally
        {
            Check(independent.TryClose().IsSuccess && associated.TryClose().IsSuccess && groups.TryClose().IsSuccess,
                "Interactive motion fixture cleanup failed.");
            if (FlyoutNative.IsWindow(targetHandle)) target.Close();
        }

        InteractiveHintManager CreateManager() => new(states, groups, images,
            (_, _, _, _, _) => Task.FromResult(ProtocolResult.Success()), (_, _) => ProtocolResult.Success(),
            (kind, value) => { if (kind.Contains("failed") || kind.Contains("cleanup-pending")) log(kind + ": " + JsonSerializer.Serialize(value)); }, clock);

        void Capture(string phase, InteractiveHintWindow window, PixelRect? owner = null)
        {
            Check(frames.Count < 32 && budget.Elapsed < TimeSpan.FromSeconds(8), "Interactive motion evidence budget exceeded.");
            var front = FlyoutNative.Bounds(window.Handle);
            var back = FlyoutNative.Bounds(window.BackgroundHandle);
            Check(front == back && front == window.LastBounds, "Animated foreground and background native rectangles diverged.");
            Check(FlyoutNative.ReadOpacity(window.Handle) == FlyoutNative.ReadOpacity(window.BackgroundHandle), "Animated pair alpha diverged.");
            double scale = GetDpiForWindow(window.Handle) / 96d;
            var renderer = Renderer(window);
            var regions = renderer.GetInteractiveRegions();
            Check(regions.Count == 2, "Animated controls lost their hit regions.");
            foreach (var region in regions)
            {
                var rect = renderer.TransformToVisual(window.RootForTesting).TransformBounds(region.Bounds);
                var point = new FlyoutNative.Point { X = front.X + (int)Math.Round((rect.X + rect.Width / 2) * scale),
                    Y = front.Y + (int)Math.Round((rect.Y + rect.Height / 2) * scale) };
                nint hit = FlyoutNative.Root(FlyoutNative.WindowFromPoint(point));
                bool regionContains = InNativeRegion(window.Handle, point.X - front.X, point.Y - front.Y);
                log("interactive-motion-control-hit: " + JsonSerializer.Serialize(new
                {
                    phase, region.NodeId, requestedDpi = dpi, nativeDpi = GetDpiForWindow(window.Handle),
                    localRect = region.Bounds, rootRect = rect, point = new { point.X, point.Y }, front, back,
                    hitRoot = hit.ToInt64(), ownerHwnd = window.Handle.ToInt64(), targetHwnd = targetHandle.ToInt64(),
                    inWindow = point.X >= front.X && point.Y >= front.Y && point.X < front.Right && point.Y < front.Bottom,
                    regionContains, committedRegions = window.RegionBoundsForTesting
                }));
                Check(regionContains && hit == window.Handle,
                    $"Animated control {region.NodeId} did not hit its actual foreground HWND.");
            }
            var blank = new FlyoutNative.Point { X = (int)front.Right - 2, Y = front.Y + 2 };
            Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(blank)) == targetHandle,
                "Animated blank area intercepted the owned background target.");
            frames.Add(new(phase, clock.GetTimestamp(), front, back, owner, regions.Count));
        }
    }
    private static TemplateRenderer Renderer(InteractiveHintWindow window) => ((Grid)window.RootForTesting).Children.OfType<TemplateRenderer>().Single();
    private static async Task Until(Func<bool> test, Stopwatch budget, string error)
    {
        while (!test()) { if (budget.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException(error); await Task.Delay(16); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan amount) => ticks += amount.Ticks;
    }
    private sealed record Frame(string Phase, long ClockTicks, PixelRect Foreground, PixelRect Background, PixelRect? Owner, int ControlRegions);
    private static bool InNativeRegion(nint window, int x, int y)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        Check(region != 0, "Cannot allocate bounded native region readback.");
        try
        {
            Check(GetWindowRgn(window, region) != 0, "Cannot read actual interactive foreground region.");
            return PtInRegion(region, x, y);
        }
        finally { DeleteObject(region); }
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
}

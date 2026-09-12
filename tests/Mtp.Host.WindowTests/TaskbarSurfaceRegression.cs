using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Native regression for the transparency contract.
///
/// Asserts: a TOP-LEVEL MTP window keeps its surface transparent once the contract recipe is applied
/// in order (frame/DWM surface preparation, an alpha color brush, wait for the brush to connect, then
/// one GDI surface paint). Removing the surface preparation turns the assertion red.
///
/// Measures but does NOT assert: an embedded WS_CHILD window inside a taskbar. It cannot host the DWM
/// frame extension, so its surface stays opaque on every display; asserting "opaque" would make a
/// future fix fail this suite. Evidence: Docs/Research/SYS-003.
///
/// Two false-positive traps this harness refuses to fall into, both of which produced wrong
/// conclusions during the investigation:
///   1. A window hidden behind another window samples exactly like a transparent one. Each case
///      therefore first proves the window renders, by requiring its no-material state to differ from
///      the bare background. Cases that fail that proof are reported inconclusive and not judged.
///   2. A near-white background is indistinguishable from the white opaque surface. That same render
///      proof covers it, because a white window over a white background cannot differ.
///
/// Run with <c>--taskbar-surface</c>. Needs an interactive desktop with a visible taskbar, so it lives
/// here rather than in <c>dotnet test Mtp.sln</c>.
/// </summary>
internal sealed class TaskbarSurfaceRegression(Application app)
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const uint WsChildStyle = 0x40000000;
    private const uint WsPopupStyle = 0x80000000;
    private const int GwlStyle = -16;
    private const uint SwHide = 0;
    private const uint SwShowNoActivate = 4;
    private const int Width = 400;
    private const int Height = 64;
    private const int ChannelTolerance = 6;
    private const int RenderAttempts = 6;

    private readonly string log = Path.Combine(AppContext.BaseDirectory, "taskbar-surface.log");
    private readonly List<Case> cases = [];
    private readonly List<string> failures = [];
    private Window? lifetime;
    private DispatcherTimer? timer;
    private ExplorerTaskbarProbeWindow? window;
    private nint handle;
    private int caseIndex;
    private int stage;
    private uint[] baseline = [];
    private uint[] background = [];
    private uint[] noMaterial = [];
    private uint[] adoptBaseline = [];
    private bool renders;
    private int renderAttempts;
    private bool adoptTransparent;

    /// <summary>
    /// <paramref name="AdoptLate"/> asks a different question from an embedded child: does a window that
    /// is ALREADY transparent as a top-level window stay transparent when it is reparented into the
    /// taskbar, keeping its popup style? That is the order the community taskbar hacks use, and it is not
    /// covered by the WS_CHILD case.
    /// </summary>
    private sealed record Case(string Name, nint Parent, int X, int Y, bool TopLevel, bool AdoptLate = false, int AdoptX = 0, int AdoptY = 0);

    public void Run()
    {
        File.WriteAllText(log, "Taskbar surface regression\n");
        try
        {
            lifetime = new Window();
            _ = typeof(Win32ExplorerTaskbarEmbedAdapter)
                .GetMethod("EnsureSystemDispatcherQueue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [null]);

            var main = Native.FindWindowW("Shell_TrayWnd", null);
            var secondary = Native.FindWindowW("Shell_SecondaryTrayWnd", null);
            AddTopLevel("top-level over primary desktop", main);
            AddTopLevel("top-level over secondary desktop", secondary);
            AddEmbedded("embedded child in primary taskbar", main);
            AddEmbedded("embedded child in secondary taskbar", secondary);
            AddAdoptedLate("top-level recipe, then reparented into the primary taskbar", main);
            if (cases.Count == 0)
            {
                throw new InvalidOperationException("No taskbar was found; this regression needs a visible taskbar.");
            }

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) => Advance();
            timer.Start();
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    private void AddTopLevel(string name, nint taskbar)
    {
        if (taskbar == 0)
        {
            File.AppendAllText(log, $"{name}: skipped, that taskbar was not found\n");
            return;
        }

        var monitor = Native.MonitorFromWindow(taskbar, Native.MonitorDefaultToNearest);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == 0 || !Native.GetMonitorInfoW(monitor, ref info))
        {
            File.AppendAllText(log, $"{name}: skipped, the work area could not be read\n");
            return;
        }

        // Work-area centre first, then a spread of other spots. A spot whose desktop is near-white is
        // useless: the white opaque surface cannot be told from the background there, so the case would
        // be unjudgeable. Picking a spot with colour keeps the case judgeable.
        var chosen = ChooseDesktopSpot(info.Work, name);
        cases.Add(new Case(name, 0, chosen.X, chosen.Y, TopLevel: true));
    }

    private void AddEmbedded(string name, nint taskbar)
    {
        if (taskbar == 0 || !Native.GetWindowRect(taskbar, out var rect))
        {
            File.AppendAllText(log, $"{name}: skipped, that taskbar was not found\n");
            return;
        }

        cases.Add(new Case(name, taskbar, (rect.Right - rect.Left - Width) / 2, 4, TopLevel: false));
    }

    /// <summary>
    /// Reparents a window that is already transparent, keeping its popup style. This asks whether the
    /// transparency survives adoption, which is a different question from whether a WS_CHILD window can
    /// build transparency from scratch.
    /// </summary>
    private void AddAdoptedLate(string name, nint taskbar)
    {
        if (taskbar == 0 || !Native.GetWindowRect(taskbar, out var rect))
        {
            File.AppendAllText(log, $"{name}: skipped, that taskbar was not found\n");
            return;
        }

        var monitor = Native.MonitorFromWindow(taskbar, Native.MonitorDefaultToNearest);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == 0 || !Native.GetMonitorInfoW(monitor, ref info))
        {
            File.AppendAllText(log, $"{name}: skipped, the work area could not be read\n");
            return;
        }

        var spot = ChooseDesktopSpot(info.Work, name);
        cases.Add(new Case(
            name, taskbar, spot.X, spot.Y, TopLevel: true, AdoptLate: true,
            // Screen coordinates on the taskbar, offset away from the embedded cases.
            AdoptX: rect.Left + (rect.Right - rect.Left) / 2 + 500,
            AdoptY: rect.Top + 6));
    }

    /// <summary>
    /// Work-area centre first, then a spread of other spots. A spot whose desktop is near-white is
    /// useless: the white opaque surface cannot be told from the background there, so the case would be
    /// unjudgeable. Picking a spot with colour keeps the case judgeable.
    /// </summary>
    private (int X, int Y) ChooseDesktopSpot(Native.Rect work, string name)
    {
        var steps = new[] { 0.5, 0.3, 0.7, 0.15, 0.85 };
        var chosen = (X: work.Left + (work.Right - work.Left - Width) / 2, Y: work.Top + (work.Bottom - work.Top - Height) / 2);
        var picked = false;
        foreach (var fx in steps)
        {
            foreach (var fy in steps)
            {
                var candidate = (
                    work.Left + (int)((work.Right - work.Left - Width) * fx),
                    work.Top + (int)((work.Bottom - work.Top - Height) * fy));
                if (HasColour(SampleRect(candidate.Item1, candidate.Item2)))
                {
                    chosen = candidate;
                    picked = true;
                    break;
                }
            }

            if (picked)
            {
                break;
            }
        }

        File.AppendAllText(log, picked
            ? $"{name}: using a spot with colour on the desktop\n"
            : $"{name}: every spot probed looked flat white, so the verdict may be inconclusive\n");
        return chosen;
    }

    private void Advance()
    {
        timer!.Stop();
        try
        {
            var entry = cases[caseIndex];
            switch (stage)
            {
                case 0:
                    Open(entry);
                    Native.ShowWindow(handle, SwHide);
                    baseline = Sample();
                    File.AppendAllText(log, $"  background: {Format(baseline)}\n");
                    break;

                case 1:
                    Native.ShowWindow(handle, SwShowNoActivate);
                    Native.DwmFlush();
                    noMaterial = Sample();
                    renders = CountMatches(noMaterial, baseline) < noMaterial.Length;
                    if (!renders && ++renderAttempts < RenderAttempts)
                    {
                        // Showing a window is asynchronous: an immediate read can still see the bare
                        // background. Retry before concluding it does not render.
                        stage--;
                        break;
                    }

                    _ = Native.GetWindowRect(handle, out var live);
                    File.AppendAllText(log, string.Create(
                        CultureInfo.InvariantCulture,
                        $"  live rect: ({live.Left},{live.Top}) {live.Right - live.Left}x{live.Bottom - live.Top}; {Native.CloakState(handle)}; render attempts={renderAttempts + 1}\n"));
                    renderAttempts = 0;
                    File.AppendAllText(log, $"  window shown, no material: {Format(noMaterial)}");
                    File.AppendAllText(log, renders
                        ? "  (differs from the background, so the window renders)\n"
                        : "  (identical to the background: the window does not render here)\n");
                    if (!renders)
                    {
                        File.AppendAllText(log, "  INCONCLUSIVE: not judged, because cover and transparency look the same here\n");
                        NextCase();
                        return;
                    }

                    break;

                case 2:
                    Apply(entry);
                    break;

                case 3:
                    // Contract 5.4: the surface paint must wait for the brush to connect.
                    var state = window!.DescribeBackdropState();
                    if (!state.Contains("connected", StringComparison.OrdinalIgnoreCase)
                        || state.Contains("pending", StringComparison.OrdinalIgnoreCase))
                    {
                        File.AppendAllText(log, $"  waiting for the brush: {state}\n");
                        stage--;
                        break;
                    }

                    File.AppendAllText(log, $"  brush state: {state}\n");
                    if (entry.TopLevel)
                    {
                        _ = Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce(handle, out var paintDetail);
                        File.AppendAllText(log, $"  contract step 2, GDI surface paint: {paintDetail}\n");
                    }

                    break;

                case 4:
                    // Hiding and re-showing the window to re-read the background destroys the very state
                    // being measured: showing re-applies the frame styles and drops the DWM glass. So the
                    // background is not re-read; instead the reading is repeated on the next tick and must
                    // be identical. A background that moved (video, animation) shows up as instability and
                    // is reported as inconclusive instead of as a false failure.
                    Native.DwmFlush();
                    background = Sample();
                    break;

                case 5:
                    Native.DwmFlush();
                    var samples = Sample();
                    if (!Stable(samples, background))
                    {
                        File.AppendAllText(log, $"  first reading:  {Format(background)}\n");
                        File.AppendAllText(log, $"  second reading: {Format(samples)}\n");
                        File.AppendAllText(log, "  INCONCLUSIVE: the reading is not stable, so the background behind is moving\n");
                        NextCase();
                        return;
                    }

                    var matches = CountMatches(samples, baseline);
                    File.AppendAllText(log, $"  after the recipe: {Format(samples)}  ({matches}/{samples.Length} match the background)\n");
                    if (entry.TopLevel)
                    {
                        File.AppendAllText(log, matches == samples.Length
                            ? "  PASS: the top-level surface stayed transparent\n"
                            : "  FAIL: the top-level surface stopped being transparent\n");
                        if (matches != samples.Length)
                        {
                            failures.Add($"{entry.Name}: {Format(samples)} over {Format(baseline)}");
                        }
                    }
                    else
                    {
                        // Deliberately not asserted: see the type comment.
                        File.AppendAllText(log, matches == samples.Length
                            ? "  the embedded child surface is transparent: the SYS-003 limitation no longer applies, so update that document and assert this\n"
                            : "  the embedded child surface is opaque (known limitation, SYS-003; not asserted)\n");
                    }

                    if (!entry.AdoptLate)
                    {
                        NextCase();
                        return;
                    }

                    break;

                case 6:
                    // Read the taskbar where the window is about to land, while the window is still
                    // elsewhere, then adopt it WITHOUT adding WS_CHILD so only reparenting changes.
                    adoptBaseline = SampleRect(entry.AdoptX + 24, entry.AdoptY + 4);
                    _ = Native.SetParent(handle, entry.Parent);
                    Native.SetStyle(handle, WsPopupStyle);
                    _ = Native.SetWindowPos(handle, 0, entry.AdoptX, entry.AdoptY, Width, Height,
                        SwpNoZOrder | SwpNoActivate | SwpShowWindow | SwpFrameChanged);
                    _ = Native.GetWindowRect(handle, out var landed);
                    File.AppendAllText(log, string.Create(
                        CultureInfo.InvariantCulture,
                        $"  adopted into the taskbar (no WS_CHILD); asked for ({entry.AdoptX},{entry.AdoptY}), landed at ({landed.Left},{landed.Top})\n"));

                    // A reparented popup does not necessarily interpret its position as parent-relative,
                    // so correct for whatever it actually did rather than assuming.
                    var dx = entry.AdoptX - landed.Left;
                    var dy = entry.AdoptY - landed.Top;
                    if (dx != 0 || dy != 0)
                    {
                        _ = Native.SetWindowPos(handle, 0, entry.AdoptX + dx, entry.AdoptY + dy, Width, Height,
                            SwpNoZOrder | SwpNoActivate | SwpShowWindow | SwpFrameChanged);
                        _ = Native.GetWindowRect(handle, out landed);
                        File.AppendAllText(log, $"  corrected the position, now at ({landed.Left},{landed.Top})\n");
                    }

                    break;

                case 7:
                    Native.DwmFlush();
                    _ = Native.GetWindowRect(handle, out var adoptedRect);
                    // adoptBaseline was read before the window arrived; re-reading it now would sample the
                    // window itself and make the comparison meaningless.
                    var adopted = Sample();
                    var adoptedMatches = CountMatches(adopted, adoptBaseline);
                    File.AppendAllText(log, $"  window at           ({adoptedRect.Left},{adoptedRect.Top}) {adoptedRect.Right - adoptedRect.Left}x{adoptedRect.Bottom - adoptedRect.Top}\n");
                    File.AppendAllText(log, $"  taskbar around it:  {Format(adoptBaseline)}\n");
                    File.AppendAllText(log, $"  adopted window:     {Format(adopted)}  ({adoptedMatches}/{adopted.Length} match the taskbar)\n");
                    adoptTransparent = adoptedMatches == adopted.Length;
                    File.AppendAllText(log, adoptTransparent
                        ? "  so far it still looks transparent, but that could also mean it stopped rendering\n"
                        : "  it turned opaque after being adopted\n");
                    break;

                case 8:
                    // Occupancy proof: with no material it must paint its opaque surface. If it does, the
                    // window really occupies this rectangle and the reading above is trustworthy.
                    window!.ClearMaterial();
                    Native.DwmFlush();
                    var bare = Sample();
                    var bareMatches = CountMatches(bare, adoptBaseline);
                    File.AppendAllText(log, $"  no material after adopting: {Format(bare)}  ({bareMatches}/{bare.Length} match the taskbar)\n");
                    var occupied = bareMatches < bare.Length;
                    File.AppendAllText(log, occupied
                        ? "  the window does occupy that rectangle, so the transparent reading above is real\n"
                        : "  INCONCLUSIVE: the window is not drawing there, so its transparency cannot be judged\n");
                    File.AppendAllText(log, occupied && adoptTransparent
                        ? "  FINDING: a reparented window KEEPS its transparency. WS_CHILD is the blocker, not reparenting.\n"
                        : occupied
                            ? "  FINDING: reparenting itself breaks the transparency, even without WS_CHILD.\n"
                            : "  FINDING: undetermined.\n");
                    NextCase();
                    return;

                default:
                    NextCase();
                    return;
            }

            stage++;
            timer.Start();
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    private void NextCase()
    {
        Close();
        caseIndex++;
        stage = -1;
        renderAttempts = 0;
        if (caseIndex >= cases.Count)
        {
            File.AppendAllText(log, failures.Count == 0
                ? "\nPASS: taskbar surface regression (not human acceptance).\n"
                : $"\nFAIL: taskbar surface regression, {failures.Count} case(s):\n  " + string.Join("\n  ", failures) + "\n");
            Finish(null);
            return;
        }

        stage = 0;
        timer!.Start();
    }

    private void Apply(Case entry)
    {
        // Keyed off TopLevel rather than Parent: an adopted-late window still has a taskbar parent by
        // the time it runs, yet it must be built with the top-level recipe.
        if (!entry.TopLevel)
        {
            var applied = typeof(ExplorerTaskbarProbeWindow)
                .GetMethod("TryApplyChildMaterial", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [(double)0, 0xFFFFFFu, null]);
            File.AppendAllText(log, $"  child composition color brush (alpha 0)={applied}\n");
            return;
        }

        // Contract 5.2 step 1, in the production order: frame/DWM preparation, then the alpha brush.
        // Removing this call is what the red run of this guard exercises.
        var prepareArgs = new object?[] { handle, null };
        if (!Environment.GetCommandLineArgs().Contains("--red-skip-prepare"))
        {
            _ = typeof(Win32ExplorerTaskbarEmbedAdapter)
                .GetMethod("TryPrepareControlWindowSurface", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, prepareArgs);
        }
        File.AppendAllText(log, $"  control surface={prepareArgs[0]}\n");
        var material = window!.TryApplyMaterial(new MaterialSpec(MaterialKind.Solid, 0), out var detail);
        File.AppendAllText(log, $"  contract step 1, alpha brush (Solid, opacity 0)={material} {detail}\n");
        if (!material)
        {
            throw new InvalidOperationException("The solid backdrop could not be applied: " + detail);
        }
    }

    private void Open(Case entry)
    {
        Close();
        window = new ExplorerTaskbarProbeWindow(HostComponentDisplayModel.From(
            new Component(new StableIdentity(new StableId("taskbar-surface")), CapabilityState.Available)));
        window.PrepareHidden(new SizeInt32(Width, Height), useToolWindowPresenter: false);
        handle = window.WindowHandle;

        if (entry.TopLevel)
        {
            // AppWindow.Show is what makes the XAML present; without it the raw window surface shows.
            window.AppWindow.Show(false);
        }
        else
        {
            Native.SetStyle(handle, WsChildStyle);
            _ = Native.SetParent(handle, entry.Parent);
            if (Native.GetAncestor(handle, 1) != entry.Parent)
            {
                throw new InvalidOperationException("The child window did not bind to the taskbar.");
            }
        }

        _ = Native.SetWindowPos(handle, 0, entry.X, entry.Y, Width, Height,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow | SwpFrameChanged);
        _ = Native.GetWindowRect(handle, out var placed);
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"\n--- {entry.Name} at ({placed.Left},{placed.Top}) {placed.Right - placed.Left}x{placed.Bottom - placed.Top} ---\n"));
    }

    private void Close()
    {
        if (window is null)
        {
            return;
        }

        window.ClearMaterial();
        _ = Native.SetParent(window.WindowHandle, 0);
        Native.SetStyle(window.WindowHandle, WsPopupStyle);
        window.Close();
        window = null;
        handle = 0;
    }

    /// <summary>
    /// Samples the window's top and bottom edges, which are clear of the vertically centred component
    /// text, so a non-match means the surface is hiding the background rather than showing text.
    /// </summary>
    private uint[] Sample()
    {
        _ = Native.GetWindowRect(handle, out var rect);
        return SampleRect(rect.Left, rect.Top);
    }

    /// <summary>Samples a 400x64 area at a screen position, independent of any window being open.</summary>
    private uint[] SampleRect(int left, int top)
    {
        var screen = Native.GetDC(0);
        try
        {
            var values = new List<uint>();
            foreach (var y in new[] { top + 5, top + Height - 5 })
            {
                foreach (var x in new[] { left + 40, left + Width / 2, left + Width - 40 })
                {
                    values.Add(Native.GetPixel(screen, x, y));
                }
            }

            return [.. values];
        }
        finally
        {
            Native.ReleaseDC(0, screen);
        }
    }

    /// <summary>True when at least one sample is clearly not near-white, so a white surface would show.</summary>
    private static bool HasColour(uint[] samples) => samples.Any(value =>
        value != 0xFFFFFFFF
        && ((value & 255) < 240 || (value >> 8 & 255) < 240 || (value >> 16 & 255) < 240));

    /// <summary>Element-wise equality within tolerance: two readings of one unchanged surface.</summary>
    private static bool Stable(uint[] left, uint[] right) => CountMatches(left, right) == left.Length;

    private static int CountMatches(uint[] samples, uint[] reference)
    {
        var matches = 0;
        for (var index = 0; index < samples.Length && index < reference.Length; index++)
        {
            if (Within(samples[index], reference[index]))
            {
                matches++;
            }
        }

        return matches;
    }

    private static bool Within(uint left, uint right) =>
        left != 0xFFFFFFFF && right != 0xFFFFFFFF
        && Math.Abs((int)(left & 255) - (int)(right & 255)) <= ChannelTolerance
        && Math.Abs((int)(left >> 8 & 255) - (int)(right >> 8 & 255)) <= ChannelTolerance
        && Math.Abs((int)(left >> 16 & 255) - (int)(right >> 16 & 255)) <= ChannelTolerance;

    private static string Format(uint[] values) => string.Join(", ", values.Select(value => value == 0xFFFFFFFF
        ? "CLR_INVALID"
        : string.Create(CultureInfo.InvariantCulture, $"0x{value & 0xFFFFFF:X6}")));

    private void Finish(Exception? error)
    {
        timer?.Stop();
        if (error is not null)
        {
            File.AppendAllText(log, error + "\n");
        }

        try
        {
            Close();
            lifetime?.Close();
        }
        finally
        {
            Environment.ExitCode = error is null && failures.Count == 0 ? 0 : 1;
            app.Exit();
        }
    }

    private static class Native
    {
        internal static void SetStyle(nint window, uint style) =>
            _ = SetWindowLongPtrW(window, GwlStyle, unchecked((nint)(int)style));

        internal const uint MonitorDefaultToNearest = 2;

        internal const uint DwmwaCloaked = 14;

        internal static string CloakState(nint window)
        {
            const int Ok = 0;
            var state = DwmGetWindowAttribute(window, DwmwaCloaked, out var value, sizeof(int)) == Ok
                ? string.Create(CultureInfo.InvariantCulture, $"cloaked={value}")
                : "cloaked=unknown";
            return $"{state} visible={IsWindowVisible(window)}";
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowW(string className, string? windowName);

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        internal static extern nint SetParent(nint window, nint parent);

        [DllImport("user32.dll")]
        internal static extern nint GetAncestor(nint window, uint flags);

        [DllImport("user32.dll")]
        internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(nint window, uint command);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(nint window, out Rect rect);

        [DllImport("user32.dll")]
        internal static extern nint GetDC(nint window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(nint window, nint dc);

        [DllImport("gdi32.dll")]
        internal static extern uint GetPixel(nint dc, int x, int y);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            internal int Size;
            internal Rect Monitor;
            internal Rect Work;
            internal uint Flags;
        }
    }
}

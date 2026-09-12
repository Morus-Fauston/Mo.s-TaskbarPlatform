using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Tests whether the opacity of an embedded taskbar child window is specific to the solid colour brush,
/// or whether the brush materials are blocked too.
///
/// The solid brush route works by making the window's own surface transparent and letting the taskbar
/// show through. Blur materials are a different route: they ask the system to composite what is BEHIND
/// the window. If that still works for a child window, the taskbar could be blurred instead of covered,
/// which would be a very different trade-off.
///
/// A top-level window with the same material is measured first, as the reference for what the material
/// looks like when it works. Each case also has a no-material reading, which proves the window is
/// really drawing there; without it a "nothing changed" result is indistinguishable from a window that
/// never appeared.
///
/// Run with <c>--acrylic-child</c>. Needs an interactive desktop with a visible taskbar.
/// </summary>
internal sealed class AcrylicChildProbe(Application app)
{
    private const int Width = 320;
    private const int Height = 64;
    private const int ChannelTolerance = 6;
    private const int RenderAttempts = 6;
    private const int SettleTicks = 4;
    private const uint SwHide = 0;
    private const uint SwShowNoActivate = 4;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const uint WsChildStyle = 0x40000000;
    private const uint WsPopupStyle = 0x80000000;
    private const int GwlStyle = -16;

    private readonly string log = Path.Combine(AppContext.BaseDirectory, "acrylic-child.log");
    private readonly List<Case> cases = [];
    private readonly List<string> notes = [];

    private Window? lifetime;
    private DispatcherTimer? timer;
    private ExplorerTaskbarProbeWindow? window;
    private nint handle;
    private int caseIndex;
    private int stage;
    private int renderAttempts;
    private int settleTicks;
    private List<(uint[] Samples, int Matches, int VsNoMaterial)> sampled = [];
    private uint[] background = [];
    private uint[] noMaterial = [];
    private bool renders;

    /// <summary><paramref name="Embedded"/> false means a top-level window used as the reference case.</summary>
    private sealed record Case(string Name, MaterialSpec Spec, bool Embedded, nint Parent, int X, int Y);

    public void Run()
    {
        File.WriteAllText(log, "Acrylic / Mica on an embedded taskbar child window\n");
        try
        {
            lifetime = new Window();
            app.UnhandledException += (_, e) =>
            {
                File.AppendAllText(log, "UNHANDLED: " + e.Exception.GetType().Name + " HRESULT=0x" + e.Exception.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + e.Exception.Message.Split('\n')[0] + "\n");
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception exception)
                {
                    File.AppendAllText(log, "FATAL: " + exception.GetType().Name + " HRESULT=0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + exception.Message.Split('\n')[0] + "\n");
                }
            };
            _ = typeof(Win32ExplorerTaskbarEmbedAdapter)
                .GetMethod("EnsureSystemDispatcherQueue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [null]);

            var capabilities = ExplorerTaskbarProbeWindow.ProbeCapabilities();
            File.AppendAllText(log, $"capabilities: acrylic={capabilities.SupportsAcrylic} mica={capabilities.SupportsMica}\n");

            var taskbar = Native.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == 0 || !Native.GetWindowRect(taskbar, out var rect))
            {
                throw new InvalidOperationException("Shell_TrayWnd was not found; this probe needs a visible taskbar.");
            }

            var embeddedX = ((rect.Right - rect.Left) - Width) / 2;
            var embeddedY = ((rect.Bottom - rect.Top) - Height) / 2;
            var referenceX = rect.Left + 40;
            var referenceY = rect.Top - (Height / 2); // Straddles the taskbar edge.

            cases.Add(new Case("reference: TOP-LEVEL window + acrylic", new MaterialSpec(MaterialKind.Acrylic, 0.8), false, taskbar, referenceX, referenceY));
            cases.Add(new Case("embedded child + acrylic", new MaterialSpec(MaterialKind.Acrylic, 0.8), true, taskbar, embeddedX, embeddedY));
            cases.Add(new Case("embedded child + acrylic 0.3", new MaterialSpec(MaterialKind.Acrylic, 0.3), true, taskbar, embeddedX, embeddedY));
            cases.Add(new Case("embedded child + mica", new MaterialSpec(MaterialKind.Mica, 0.8), true, taskbar, embeddedX, embeddedY));

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            timer.Tick += (_, _) => Advance();
            timer.Start();
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    private void Open(Case entry)
    {
        Close();
        window = new ExplorerTaskbarProbeWindow(HostComponentDisplayModel.From(
            new Component(new StableIdentity(new StableId("acrylic-child")), CapabilityState.Available)));
        window.PrepareHidden(new Windows.Graphics.SizeInt32(Width, Height), useToolWindowPresenter: false);
        handle = window.WindowHandle;

        if (entry.Embedded)
        {
            Native.SetStyle(handle, WsChildStyle);
            _ = Native.SetParent(handle, entry.Parent);
        }
        else
        {
            window.AppWindow.Show(false);
        }

        _ = Native.SetWindowPos(handle, 0, entry.X, entry.Y, Width, Height,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow | SwpFrameChanged);
        _ = Native.GetWindowRect(handle, out var placed);
        File.AppendAllText(log, string.Create(
            CultureInfo.InvariantCulture,
            $"\n--- {entry.Name} at ({placed.Left},{placed.Top}) {placed.Right - placed.Left}x{placed.Bottom - placed.Top} ---\n"));
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
                    _ = Native.ShowWindow(handle, SwHide);
                    background = Sample();
                    File.AppendAllText(log, $"  background behind it: {Format(background)}\n");
                    break;

                case 1:
                    _ = Native.ShowWindow(handle, SwShowNoActivate);
                    Native.DwmFlush();
                    noMaterial = Sample();
                    renders = CountMatches(noMaterial, background) < noMaterial.Length;
                    if (!renders && ++renderAttempts < RenderAttempts)
                    {
                        stage--;
                        break;
                    }

                    renderAttempts = 0;
                    File.AppendAllText(log, $"  no material:          {Format(noMaterial)}");
                    File.AppendAllText(log, renders
                        ? "  (differs from the background, so the window draws here)\n"
                        : "  (identical to the background: the window is not drawing here)\n");
                    if (!renders)
                    {
                        File.AppendAllText(log, "  INCONCLUSIVE: not judged, because a covered and a transparent window look the same\n");
                        NextCase();
                        return;
                    }

                    break;

                case 2:
                    if (!entry.Embedded)
                    {
                        var prepareArgs = new object?[] { handle, null };
                        _ = typeof(Win32ExplorerTaskbarEmbedAdapter)
                            .GetMethod("TryPrepareControlWindowSurface", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(null, prepareArgs);
                        File.AppendAllText(log, $"  contract step 1, control surface: {prepareArgs[1]}\n");
                    }

                    var applied = window!.TryApplyMaterial(entry.Spec, out var detail);
                    File.AppendAllText(log, $"  material {entry.Spec.Kind} {entry.Spec.Opacity}: applied={applied} {detail}\n");

                    // First reading in this same tick, so there is always evidence even if the process dies
                    // before the next one. A blur controller on a child window has been observed to kill the
                    // process, which is why nothing may be deferred to a later tick alone.
                    Native.DwmFlush();
                    var immediate = Sample();
                    File.AppendAllText(log, $"  immediately after:    {Format(immediate)}  (background {CountMatches(immediate, background)}/{immediate.Length}, no-material {CountMatches(immediate, noMaterial)}/{immediate.Length})\n");

                    // The material also needs a moment to composite, so keep sampling once per tick.
                    // Whatever ticks do run end up in the log, and the last line before a crash is evidence.
                    settleTicks = 0;
                    sampled = [];
                    break;

                case 3:
                    Native.DwmFlush();
                    var reading = Sample();
                    var readingMatches = CountMatches(reading, background);
                    var readingVsNoMaterial = CountMatches(reading, noMaterial);
                    settleTicks++;
                    File.AppendAllText(log, $"  tick {settleTicks}: {Format(reading)}  (background {readingMatches}/{reading.Length}, no-material {readingVsNoMaterial}/{reading.Length})\n");
                    sampled.Add((reading, readingMatches, readingVsNoMaterial));

                    if (settleTicks < SettleTicks)
                    {
                        stage--;
                        break;
                    }

                    // Judge the settled reading, taken from the last tick that actually ran.
                    var (final, finalMatches, finalVsNoMaterial) = sampled[^1];
                    File.AppendAllText(log, $"  settled:              {Format(final)}\n");
                    string verdict = finalMatches == final.Length
                        ? "the background is untouched: the material contributes nothing here"
                        : finalVsNoMaterial == final.Length
                            ? "identical to the no-material state: the material changed nothing"
                            : "the material changed the pixels, so the material is doing something on a child window";
                    File.AppendAllText(log, $"  => {verdict}\n");
                    notes.Add($"{entry.Name}: {verdict}");

                    try
                    {
                        File.AppendAllText(log, $"  backdrop state: {window!.DescribeBackdropState()}\n");
                    }
                    catch (Exception exception)
                    {
                        File.AppendAllText(log, "  backdrop state read failed: " + exception.GetType().Name + " HRESULT=0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture) + "\n");
                    }

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
        settleTicks = 0;
        sampled = [];
        if (caseIndex >= cases.Count)
        {
            File.AppendAllText(log, "\n=== summary (screen pixels only; not human acceptance) ===\n");
            foreach (var note in notes)
            {
                File.AppendAllText(log, "  " + note + "\n");
            }

            Finish(null);
            return;
        }

        stage = 0;
        timer!.Start();
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

    private uint[] Sample()
    {
        _ = Native.GetWindowRect(handle, out var rect);
        var screen = Native.GetDC(0);
        try
        {
            var values = new List<uint>();
            foreach (var y in new[] { rect.Top + 5, rect.Bottom - 5 })
            {
                foreach (var x in new[] { rect.Left + 40, rect.Left + Width / 2, rect.Right - 40 })
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
            Environment.ExitCode = error is null ? 0 : 1;
            app.Exit();
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        internal static void SetStyle(nint window, uint style) =>
            _ = SetWindowLongPtrW(window, GwlStyle, unchecked((nint)(int)style));

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowW(string className, string? windowName);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetParent(nint window, nint parent);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(nint window, uint command);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetWindowRect(nint window, out Rect rect);

        [DllImport("user32.dll")]
        internal static extern nint GetDC(nint window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(nint window, nint dc);

        [DllImport("gdi32.dll")]
        internal static extern uint GetPixel(nint dc, int x, int y);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();
    }
}

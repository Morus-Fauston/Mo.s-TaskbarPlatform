using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host.WindowTests;

/// <summary>
/// Puts all four surface combinations on screen AT THE SAME TIME and holds them there, so a human can
/// look at the result directly instead of reading pixel numbers.
///
/// Expected result:
///   embedded child in the primary taskbar    -> a solid white block
///   embedded child in the secondary taskbar  -> a solid white block
///   top-level window over the primary screen -> the desktop shows through
///   top-level window over the secondary screen -> the desktop shows through
///
/// If a block that is expected white actually shows the desktop through, the limitation in
/// Docs/Research/SYS-003 no longer applies and that document plus the regression must be updated.
///
/// Run with <c>--show-surface</c>. Hold time defaults to 45 seconds; override with
/// <c>--hold=30</c>. This is a viewing aid, not an automated check: it always exits 0.
/// </summary>
internal sealed class TaskbarSurfaceShowcase(Application app)
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const uint WsChildStyle = 0x40000000;
    private const uint WsPopupStyle = 0x80000000;
    private const int GwlStyle = -16;
    private const int ItemWidth = 420;
    private const int ItemHeight = 64;
    private const int DefaultHoldSeconds = 45;

    private readonly string log = Path.Combine(AppContext.BaseDirectory, "surface-showcase.log");
    private readonly List<Shown> shown = [];
    private readonly int holdSeconds = ReadHoldSeconds();

    /// <summary>HWND_TOPMOST is -1; GWL-style constants are unsigned but the API takes a pointer-sized value.</summary>
    private static readonly nint HwndTopmost = unchecked((nint)(-1));
    private Window? lifetime;
    private DispatcherTimer? timer;
    private DispatcherTimer? paintTimer;

    private sealed record Shown(
        string Label,
        string Expectation,
        ExplorerTaskbarProbeWindow Window,
        nint Handle,
        Native.Rect Rect,
        bool Adopted);

    public void Run()
    {
        File.WriteAllText(log, "Surface showcase\n");
        try
        {
            lifetime = new Window();
            var main = Native.FindWindowW("Shell_TrayWnd", null);
            var secondary = Native.FindWindowW("Shell_SecondaryTrayWnd", null);
            _ = typeof(Win32ExplorerTaskbarEmbedAdapter)
                .GetMethod("EnsureSystemDispatcherQueue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [null]);

            Add("主屏任务栏里的嵌入子窗口", "应当是一块不透明白色方块", main, embedded: true);
            Add("副屏任务栏里的嵌入子窗口", "应当是一块不透明白色方块", secondary, embedded: true);
            Add("主屏上骑在任务栏边缘的顶层窗口", "下半截应当能看见深色任务栏，而不是被白块盖住", main, embedded: false);
            Add("副屏上骑在任务栏边缘的顶层窗口", "下半截应当能看见深色任务栏，而不是被白块盖住", secondary, embedded: false);

            if (shown.Count == 0)
            {
                throw new InvalidOperationException("No taskbar was found; the showcase needs a visible taskbar.");
            }

            // Contract 5.4: the surface paint must follow brush connection, so it is deferred a moment
            // rather than run immediately after the material is attached.
            paintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            paintTimer.Tick += (_, _) =>
            {
                paintTimer.Stop();
                PaintTopLevelWindows();
                BeginHold();
            };
            paintTimer.Start();
        }
        catch (Exception error)
        {
            Finish(error);
        }
    }

    /// <summary>Contract 5.2 step 2, without which a top-level solid surface renders fully opaque.</summary>
    private void PaintTopLevelWindows()
    {
        foreach (var item in shown.Where(entry => !entry.Adopted))
        {
            _ = Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce(item.Handle, out var paintDetail);
            File.AppendAllText(log, $"  contract step 2, GDI surface paint: {paintDetail}\n");
        }
    }

    private void BeginHold()
    {
        File.AppendAllText(log, $"\n===== 现在请看屏幕，保持 {holdSeconds} 秒 =====\n");
        foreach (var item in shown)
        {
            File.AppendAllText(log, string.Create(
                CultureInfo.InvariantCulture,
                $"  {item.Rect.Left},{item.Rect.Top} {item.Rect.Right - item.Rect.Left}x{item.Rect.Bottom - item.Rect.Top}  {item.Label} -> {item.Expectation}\n"));
        }

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(holdSeconds) };
        timer.Tick += (_, _) => Finish(null);
        timer.Start();
    }

    private static int ReadHoldSeconds()
    {
        var argument = Environment.GetCommandLineArgs()
            .FirstOrDefault(value => value.StartsWith("--hold=", StringComparison.OrdinalIgnoreCase));
        return argument is not null && int.TryParse(argument.AsSpan("--hold=".Length), out var seconds) && seconds > 0
            ? seconds
            : DefaultHoldSeconds;
    }

    private void Add(string label, string expectation, nint taskbar, bool embedded)
    {
        if (taskbar == 0 || !Native.GetWindowRect(taskbar, out var rect))
        {
            File.AppendAllText(log, $"{label}: skipped, taskbar not found\n");
            return;
        }

        var window = new ExplorerTaskbarProbeWindow(HostComponentDisplayModel.From(
            new Component(new StableIdentity(new StableId("showcase")), CapabilityState.Available)));
        nint handle = 0;
        var adopted = false;
        try
        {
            window.PrepareHidden(new SizeInt32(ItemWidth, ItemHeight), useToolWindowPresenter: false);
            handle = window.WindowHandle;

            // Two side-by-side slots per taskbar so the two items on one screen do not overlap.
            var slotX = rect.Left + (rect.Right - rect.Left) / 2 - ItemWidth - 12;
            if (embedded)
            {
                Native.SetStyle(handle, WsChildStyle);
                _ = Native.SetParent(handle, taskbar);
                adopted = true;
                var x = (rect.Right - rect.Left) / 2 - ItemWidth - 12;
                _ = Native.SetWindowPos(handle, 0, x, 4, ItemWidth, ItemHeight,
                    SwpNoZOrder | SwpNoActivate | SwpShowWindow | SwpFrameChanged);
                ApplyChildMaterial(window);
            }
            else
            {
                // Straddle the taskbar's top edge. A window floating over the desktop proves nothing,
                // because the desktop behind it is often white too and then looks the same either way.
                // The taskbar is dark and has icons, so an opaque white block and a see-through window
                // cannot be confused here.
                window.AppWindow.Show(false);
                var x = rect.Left + 24;
                var y = rect.Top - 24;
                _ = Native.SetWindowPos(handle, HwndTopmost, x, y, ItemWidth, ItemHeight,
                    SwpNoActivate | SwpShowWindow | SwpFrameChanged);
                ApplyTopLevelMaterial(window, handle);
            }

            _ = Native.GetWindowRect(handle, out var placed);
            shown.Add(new Shown(label, expectation, window, handle, placed, adopted));
        }
        catch (Exception exception)
        {
            File.AppendAllText(log, $"{label}: failed: {exception.GetType().Name}: {exception.Message.Split('\n')[0]}\n");
            window.Close();
        }
    }

    private void ApplyChildMaterial(ExplorerTaskbarProbeWindow window)
    {
        var applied = typeof(ExplorerTaskbarProbeWindow)
            .GetMethod("TryApplyChildMaterial", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [(double)0, 0xFFFFFFu, null]);
        File.AppendAllText(log, $"  child color brush (alpha 0)={applied}\n");
        _ = Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce(window.WindowHandle, out var paintDetail);
        File.AppendAllText(log, $"  paint: {paintDetail}\n");
    }

    private static void ApplyTopLevelMaterial(ExplorerTaskbarProbeWindow window, nint handle)
    {
        var prepareArgs = new object?[] { handle, null };
        _ = typeof(Win32ExplorerTaskbarEmbedAdapter)
            .GetMethod("TryPrepareControlWindowSurface", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, prepareArgs);
        File.AppendAllText(FileLogPath(), $"  control surface={prepareArgs[0]}\n");
        var applied = window.TryApplyMaterial(new MaterialSpec(MaterialKind.Solid, 0), out var detail);
        File.AppendAllText(FileLogPath(), $"  top-level solid backdrop (alpha 0)={applied} {detail}\n");
    }

    private static string FileLogPath() => Path.Combine(AppContext.BaseDirectory, "surface-showcase.log");

    private void Finish(Exception? error)
    {
        timer?.Stop();
        paintTimer?.Stop();
        if (error is not null)
        {
            File.AppendAllText(log, error + "\n");
        }

        foreach (var item in shown)
        {
            try
            {
                // A WinUI window owns its HWND: destroying the handle directly crashes the process.
                // Reparent out of the taskbar first, then let the window close itself.
                if (item.Adopted)
                {
                    _ = Native.SetParent(item.Handle, 0);
                    Native.SetStyle(item.Handle, WsPopupStyle);
                }

                item.Window.ClearMaterial();
                item.Window.Close();
            }
            catch (Exception)
            {
                // The window may already be gone.
            }
        }

        lifetime?.Close();
        // Program.Main starts at 1, so a clean run has to clear it explicitly.
        Environment.ExitCode = error is null ? 0 : 1;
        app.Exit();
    }

    private static class Native
    {
        internal static void SetStyle(nint window, uint style) =>
            _ = SetWindowLongPtrW(window, GwlStyle, unchecked((nint)(int)style));

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowW(string className, string? windowName);

        [DllImport("user32.dll")]
        internal static extern nint SetParent(nint window, nint parent);

        [DllImport("user32.dll")]
        internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(nint window, out Rect rect);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }
    }
}

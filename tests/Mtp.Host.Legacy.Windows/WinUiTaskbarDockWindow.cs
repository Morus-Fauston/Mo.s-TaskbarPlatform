using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host;

/// <summary>05A uses the compact Host renderer as a top-level window with no message interception.</summary>
internal sealed class WinUiTaskbarDockWindow : ITaskbarDockWindow
{
    private readonly ExplorerTaskbarProbeWindow window = new();
    private bool initialized;
    private bool closed;
    private readonly DeferredSurfacePaint surfacePaint;
    private StructuredError? materialError;
    private bool surfaceReady;
    private PixelRect? lastBounds;
    private bool visible;

    public WinUiTaskbarDockWindow()
    {
        surfacePaint = new(
            action => window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => action()),
            () => IsAlive && window.IsBackdropConnected,
            () => Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce(window.WindowHandle, out var detail)
                ? CoreResult<bool>.Success(true)
                : CoreResult<bool>.Failure(new("dock_surface_paint_failed", detail)));
        surfacePaint.Changed += (_, _) =>
        {
            if (surfacePaint.IsPainted) window.MarkSurfacePainted();
            else if (surfacePaint.Error?.Code is not "dock_brush_not_connected") window.ClearMaterial();
            VisualStateChanged?.Invoke(this, EventArgs.Empty);
        };
        window.Closed += (_, _) =>
        {
            closed = true;
            surfacePaint.Cancel();
            window.BackdropConnectionChanged -= BackdropConnectionChanged;
            Closed?.Invoke(this, EventArgs.Empty);
        };
        window.BackdropConnectionChanged += BackdropConnectionChanged;
    }

    public event EventHandler? Closed;
    public event EventHandler? VisualStateChanged;
    public long Identity => window.WindowHandle;
    public bool IsAlive => !closed && Native.IsWindow(window.WindowHandle);
    public StructuredError? VisualError => materialError ?? surfacePaint.Error;

    public void Show(HostComponentDisplayModel component, PixelRect bounds)
    {
        window.InitializeView(component);
        if (!initialized)
        {
            window.Title = "MTP 任务栏组件";
            // The tool presenter restores a native dialog edge even after frame suppression.
            // Keep the default presenter; native positioning still applies TOOLWINDOW/NOACTIVATE.
            window.PrepareHidden(new SizeInt32(bounds.Width, bounds.Height), useToolWindowPresenter: false);
            window.AppWindow.IsShownInSwitchers = false;
            initialized = true;
        }
        var resized = lastBounds is null || lastBounds.Value.Width != bounds.Width || lastBounds.Value.Height != bounds.Height;
        if (lastBounds != bounds)
            window.AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        var showing = !visible;
        if (showing) window.AppWindow.Show(false);
        Native.PositionWithoutActivation(window.WindowHandle, bounds, showing);
        visible = true;
        if (!surfaceReady)
        {
            if (!Win32ExplorerTaskbarEmbedAdapter.TryPrepareControlWindowSurface(window.WindowHandle, out var frameDetail))
                ReportVisualFailure("dock_surface_prepare_failed", frameDetail);
            if (!window.TryApplyMaterial(new(MaterialKind.Solid, 0), out var detail))
                ReportVisualFailure("dock_brush_failed", detail ?? "透明色刷应用失败。");
            else
            {
                surfaceReady = true;
                surfacePaint.Schedule();
            }
        }
        else if (resized || showing)
        {
            if (showing && !Win32ExplorerTaskbarEmbedAdapter.TryPrepareControlWindowSurface(window.WindowHandle, out var detail))
                ReportVisualFailure("dock_surface_prepare_failed", detail);
            surfacePaint.Schedule();
        }
        lastBounds = bounds;
    }

    public void Close() => window.Close();

    public void Hide()
    {
        Native.ShowWindow(window.WindowHandle, 0);
        if (Native.IsWindowVisible(window.WindowHandle))
            throw new InvalidOperationException("The dock window remained visible after hiding.");
        visible = false;
    }

    private void BackdropConnectionChanged(object? sender, EventArgs args)
    {
        if (window.IsBackdropConnected) surfacePaint.Schedule();
        else ReportVisualFailure("dock_brush_connect_failed", window.DescribeBackdropState());
    }

    private void ReportVisualFailure(string code, string? detail)
    {
        materialError = new(code, $"透明呈现降级；组件状态和设置保留。{detail}");
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static class Native
    {
        internal static void PositionWithoutActivation(nint window, PixelRect bounds, bool show)
        {
            var style = GetWindowLongPtrW(window, -20);
            var next = style | 0x08000000 | 0x00000080; // NOACTIVATE and TOOLWINDOW; input messages still reach WinUI.
            Marshal.SetLastPInvokeError(0);
            if (next != style && SetWindowLongPtrW(window, -20, next) == 0 && Marshal.GetLastPInvokeError() != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            if (!SetWindowPos(window, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010u | (show ? 0x0040u : 0x0004u)))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint window, int index);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    }
}

using System.Runtime.InteropServices;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class WindowSurfacePaintIntegrationTests
{
    [Fact]
    public void OwnHiddenWindowCanBePaintedAndDestroyedHandleIsRejected()
    {
        var window = CreateWindowExW(0, "STATIC", "MTP automated surface test", 0x80000000,
            0, 0, 32, 16, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            Assert.True(Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce(window, out var detail), detail);
            Assert.Contains("32x16", detail);
        }
        finally { Assert.True(DestroyWindow(window)); }
        Assert.False(Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce(window, out _));
        Assert.False(Win32ExplorerTaskbarEmbedAdapter.PaintWindowSurfaceOnce(0, out _));
        Assert.False(Win32ExplorerTaskbarEmbedAdapter.TryPrepareControlWindowSurface(0, out _));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}

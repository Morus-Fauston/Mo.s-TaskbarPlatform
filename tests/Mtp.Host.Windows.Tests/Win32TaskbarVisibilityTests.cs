using System.Runtime.InteropServices;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class Win32TaskbarVisibilityTests
{
    [Theory]
    [InlineData(1032, 48, true, true)]
    [InlineData(1080, 48, true, false)]
    [InlineData(1078, 2, true, false)]
    [InlineData(1060, 48, true, false)]
    [InlineData(1032, 48, false, null)]
    public void AutoHideRequiresAFullyExposedBottomBar(int y, int height, bool autoHide, bool? expected)
        => Assert.Equal(expected, Win32TaskbarVisibility.EvaluateTaskbarExposure(
            new(0, 0, 1920, 1080), new(0, 0, 1920, 1080), new(0, y, 1920, height), autoHide));

    [Fact]
    public void RealClientBoundsDistinguishFullscreenFromFramedAndSmallerWindows()
    {
        var display = new PixelRect(-30000, -30000, 400, 300);
        foreach (var style in new uint[] { 0x80000000, 0x00CF0000 })
        {
            var handle = CreateWindowExW(0x08000080, "STATIC", "MTP visibility test", style,
                display.X, display.Y, display.Width, display.Height, 0, 0, 0, 0);
            Assert.NotEqual(0, handle);
            try
            {
                Assert.False(Win32TaskbarVisibility.IsFullScreenWindow(handle, display));
                ShowWindow(handle, 4);
                Assert.Equal(style == 0x80000000, Win32TaskbarVisibility.IsFullScreenWindow(handle, display));
                if (style == 0x80000000)
                {
                    // A custom title bar can leave a maximized window's client as large as the monitor.
                    var originalStyle = GetWindowLongPtrW(handle, -16);
                    SetWindowLongPtrW(handle, -16, originalStyle | 0x01C00000);
                    Assert.False(Win32TaskbarVisibility.IsFullScreenWindow(handle, display));
                    SetWindowLongPtrW(handle, -16, originalStyle | 0x01000000);
                    Assert.True(Win32TaskbarVisibility.IsFullScreenWindow(handle, display));
                    SetWindowLongPtrW(handle, -16, originalStyle);
                }
                Assert.True(SetWindowPos(handle, 0, -30000, -30000, 200, 200, 0x14));
                Assert.False(Win32TaskbarVisibility.IsFullScreenWindow(handle, display));
            }
            finally { Assert.True(DestroyWindow(handle)); }
            Assert.Null(Win32TaskbarVisibility.IsFullScreenWindow(handle, display));
        }
        Assert.Null(Win32TaskbarVisibility.IsFullScreenWindow(0, display));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
}

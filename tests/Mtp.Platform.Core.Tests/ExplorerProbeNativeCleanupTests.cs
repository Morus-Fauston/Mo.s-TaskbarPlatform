using System.ComponentModel;
using System.Runtime.InteropServices;
using Mtp.Host;

namespace Mtp.Platform.Core.Tests;

public sealed class ExplorerProbeNativeCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupRestoresARealHiddenChildToTheDesktopBeforeClosing(bool retryClose)
    {
        using var parent = new HiddenWindow();
        using var child = new HiddenWindow();
        var owner = new ExplorerProbeWindowOwner(new Win32ExplorerTaskbarEmbedAdapter.NativeExplorerWindowOperations());
        owner.TakeOwnership(child);
        var originalStyle = Native.GetWindowLongPtrW(child.Handle, -16);
        _ = Native.SetWindowLongPtrW(child.Handle, -16, (nint)(((long)originalStyle & ~0x80000000L) | 0x40000000L));
        owner.MarkStyleChanged(originalStyle);
        _ = Native.SetParent(child.Handle, parent.Handle);
        owner.MarkReparented();
        Assert.Equal(parent.Handle, Native.GetAncestor(child.Handle, 1));
        child.BeforeClose = () =>
        {
            Assert.Equal(Native.GetDesktopWindow(), Native.GetAncestor(child.Handle, 1));
            Assert.Equal(originalStyle, Native.GetWindowLongPtrW(child.Handle, -16));
        };

        if (retryClose)
        {
            child.CloseError = new InvalidOperationException("Injected close failure after native restoration.");
            var failed = owner.Cleanup();
            Assert.Equal("explorer_probe_close_failed", failed.Error?.Code);
            Assert.True(owner.HasResource);
            Assert.True(owner.IsCleanupPending);
            Assert.False(owner.IsReparented);
            Assert.False(owner.IsStyleChanged);
            Assert.True(Native.IsWindow(child.Handle));
            child.CloseError = null;
        }

        var result = owner.Cleanup();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(retryClose ? 2 : 1, child.CloseCalls);
        Assert.False(Native.IsWindow(child.Handle));
        Assert.True(Native.IsWindow(parent.Handle));
        Assert.False(owner.HasResource);
        Assert.True(owner.Cleanup().IsSuccess);
    }

    [Fact]
    public void NativeParentRestoreRejectsAnInvalidWindow()
    {
        var operations = new Win32ExplorerTaskbarEmbedAdapter.NativeExplorerWindowOperations();

        Assert.False(operations.RestoreParent(0));
    }

    private sealed class HiddenWindow : IExplorerProbeWindowResource, IDisposable
    {
        public HiddenWindow()
        {
            Handle = Native.CreateWindowExW(0, "STATIC", "MTP cleanup integration test", 0, 0, 0, 1, 1, 0, 0, 0, 0);
            if (Handle == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public event EventHandler? Closed;

        public nint Handle { get; }

        public Action? BeforeClose { get; set; }

        public Exception? CloseError { get; set; }

        public int CloseCalls { get; private set; }

        public void Close()
        {
            CloseCalls++;
            BeforeClose?.Invoke();
            if (CloseError is not null)
            {
                throw CloseError;
            }

            Dispose();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (Native.IsWindow(Handle) && !Native.DestroyWindow(Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    private static class Native
    {
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(nint handle);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(nint handle);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint GetDesktopWindow();

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint GetAncestor(nint handle, uint flags);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint SetParent(nint child, nint parent);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint GetWindowLongPtrW(nint handle, int index);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint SetWindowLongPtrW(nint handle, int index, nint value);
    }
}

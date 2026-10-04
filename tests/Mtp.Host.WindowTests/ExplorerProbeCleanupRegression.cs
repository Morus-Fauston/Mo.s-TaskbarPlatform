using System.Reflection;
using System.Runtime.InteropServices;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host.WindowTests;

internal static class ExplorerProbeCleanupRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        var adapter = new Win32ExplorerTaskbarEmbedAdapter();
        var adapterType = typeof(Win32ExplorerTaskbarEmbedAdapter);
        var owner = adapterType.GetField("owner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var resourceType = adapterType.GetNestedType("ExplorerProbeWindowResource", BindingFlags.NonPublic)!;
        var component = HostComponentDisplayModel.From(
            new Component(new StableIdentity(new StableId("destroyed-probe-test")), CapabilityState.Available));

        // Exercise the production resource and Detach using only our own parent and WinUI HWNDs.
        // Alternating forced parent destruction and normal detach also proves subsequent reuse.
        foreach (var destroyParent in new[] { true, false, true, false })
        {
            var parent = Native.CreateWindowExW(0, "STATIC", "MTP cleanup test parent", 0,
                -30000, -30000, 400, 80, 0, 0, 0, 0);
            if (parent == 0) throw new InvalidOperationException("Could not create test parent.");
            try
            {
                var window = new ExplorerTaskbarProbeWindow(component);
                window.PrepareHidden(new SizeInt32(360, 48));
                var handle = window.WindowHandle;
                var resource = Activator.CreateInstance(resourceType, window)!;
                owner.GetType().GetMethod("TakeOwnership")!.Invoke(owner, [resource]);
                adapterType.GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adapter, window);
                adapterType.GetField("windowHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adapter, handle);
                var style = Native.GetWindowLongPtrW(handle, -16);
                Native.SetWindowLongPtrW(handle, -16, (nint)(((long)style & ~0x80000000L) | 0x40000000L));
                owner.GetType().GetMethod("MarkStyleChanged")!.Invoke(owner, [style]);
                Native.SetParent(handle, parent);
                if (Native.GetAncestor(handle, 1) != parent) throw new InvalidOperationException("Test reparent failed.");
                owner.GetType().GetMethod("MarkReparented")!.Invoke(owner, null);
                await Task.Delay(100);
                if (adapter.Lifecycle != ExplorerTaskbarProbeLifecycle.Embedded)
                    throw new InvalidOperationException("Test did not own an embedded probe.");
                if (destroyParent)
                {
                    log("Destroying only the fixture parent and its real WinUI child.");
                    if (!Native.DestroyWindow(parent)) throw new InvalidOperationException("Could not destroy test parent.");
                    parent = 0;
                    if (Native.IsWindow(handle)) throw new InvalidOperationException("Test child survived parent destruction.");
                    await Task.Delay(100);
                }
                log($"Calling production Detach: parentDestroyed={destroyParent}, lifecycle={adapter.Lifecycle}.");
                var detached = adapter.Detach();
                if (!detached.IsSuccess || adapter.Lifecycle != ExplorerTaskbarProbeLifecycle.Detached || Native.IsWindow(handle))
                    throw new InvalidOperationException($"Probe cleanup failed: {detached.Error}");
                if (!adapter.Detach().IsSuccess) throw new InvalidOperationException("Repeated detach failed.");
                log($"PASS: production WinUI cleanup and reuse; parentDestroyed={destroyParent}.");
            }
            finally
            {
                if (parent != 0) Native.DestroyWindow(parent);
            }
        }
    }

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint CreateWindowExW(uint ex, string cls, string title, uint style,
            int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] internal static extern nint SetParent(nint child, nint parent);
        [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint window, int index);
        [DllImport("user32.dll")] internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    }
}

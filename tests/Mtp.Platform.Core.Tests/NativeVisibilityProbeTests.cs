using System.Runtime.InteropServices;
using Mtp.Host.Experiments;

namespace Mtp.Platform.Core.Tests;

public sealed class NativeVisibilityProbeTests
{
    [Fact]
    public void RealChildIsTransparentVisibleDisabledAndDestroyedOnDispose()
    {
        var parent = CreateWindowExW(0x08000080, "STATIC", "MTP test parent", 0x80000000,
            -30000, -30000, 400, 40, 0, 0, 0, 0);
        Assert.NotEqual(0, parent);
        long child = 0;
        try
        {
            using (var probe = new Win32VisibilityProbe(() => [new("screen-a", new(-30000, -30000, 400, 300), 96, (long)parent)]))
            {
                var sample = Assert.Single(probe.Capture());
                child = sample.Probe.Handle;
                Assert.NotEqual(0, child);
                Assert.Equal((long)parent, sample.ParentChain[0].Handle);
                Assert.True(sample.Signals.AttributesValid);
                Assert.True((sample.Probe.Style & 0x10000000) != 0); // WS_VISIBLE even while parent hidden.
                Assert.False(sample.Probe.LogicallyVisible);
                Assert.Equal((byte)0, sample.Alpha);
                Assert.Equal(2u, sample.LayeredFlags);
                Assert.Equal(-1, (long)SendMessageW((nint)child, 0x84, 0, 0)); // HTTRANSPARENT
                Assert.Equal(3, (long)SendMessageW((nint)child, 0x21, 0, 0)); // MA_NOACTIVATE
            }
            Assert.False(IsWindow((nint)child));
            Assert.True(IsWindow(parent));
        }
        finally { DestroyWindow(parent); }
    }

    [Fact]
    public void ParentDestructionReportsLossThenRecreatesOnlyThatScreensProbe()
    {
        var first = Parent();
        var second = Parent();
        try
        {
            using var probe = new Win32VisibilityProbe(() => [Target("a", first), Target("b", second)]);
            var initial = probe.Capture();
            var oldChild = initial[0].Probe.Handle;
            var unaffectedChild = initial[1].Probe.Handle;
            Assert.True(DestroyWindow(first));
            first = Parent();
            var lost = probe.Capture();
            Assert.Equal(ProbeExposure.Unknown, lost[0].Exposure);
            Assert.Equal(unaffectedChild, lost[1].Probe.Handle);
            Assert.False(IsWindow((nint)oldChild));
            var recovered = probe.Capture();
            Assert.True(recovered[0].Probe.Exists);
            Assert.True(recovered[0].Generation > initial[0].Generation);
            Assert.Equal((long)first, recovered[0].ParentChain[0].Handle);
            Assert.Equal(unaffectedChild, recovered[1].Probe.Handle);
            Assert.Contains(probe.DrainTrace(), e => e.Code == 0x82 && e.DisplayId == "a");
        }
        finally { DestroyWindow(first); DestroyWindow(second); }
    }

    [Fact]
    public void DisconnectAndReconnectPreserveOtherScreensAndRemoveOnlyOwnedChildren()
    {
        var first = Parent();
        var second = Parent();
        var connected = true;
        try
        {
            using var probe = new Win32VisibilityProbe(() => connected
                ? [Target("a", first), Target("b", second)] : [Target("b", second)]);
            var initial = probe.Capture();
            connected = false;
            Assert.Equal(initial[1].Probe.Handle, Assert.Single(probe.Capture()).Probe.Handle);
            Assert.False(IsWindow((nint)initial[0].Probe.Handle));
            connected = true;
            var reconnected = probe.Capture();
            Assert.Equal(2, reconnected.Count);
            Assert.True(reconnected[0].Generation > initial[0].Generation);
            Assert.Equal(initial[1].Probe.Handle, reconnected[1].Probe.Handle);
            Assert.True(IsWindow(first));
            Assert.True(IsWindow(second));
        }
        finally { DestroyWindow(first); DestroyWindow(second); }
    }

    [Fact]
    public void MissingTaskbarDoesNotCopyAnotherScreensResult()
    {
        var parent = Parent();
        try
        {
            using var probe = new Win32VisibilityProbe(() => [Target("missing", 0), Target("present", parent)]);
            var samples = probe.Capture();
            Assert.Equal(ProbeExposure.Unknown, samples[0].Exposure);
            Assert.Equal(0, samples[0].Probe.Handle);
            Assert.True(samples[1].Probe.Exists);
            var secondRead = probe.Capture();
            Assert.Equal(samples[1].Probe.Handle, secondRead[1].Probe.Handle);
        }
        finally { DestroyWindow(parent); }
    }

    private static nint Parent()
    {
        var hwnd = CreateWindowExW(0x08000080, "STATIC", "MTP test parent", 0x80000000,
            -30000, -30000, 400, 40, 0, 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        return hwnd;
    }

    [Fact]
    public void TransientDiscoveryFailureMarksKnownScreensUnknownAndThenRecovers()
    {
        var parent = Parent();
        var fail = false;
        try
        {
            using var probe = new Win32VisibilityProbe(() => fail
                ? throw new InvalidOperationException("display transition") : [Target("a", parent)]);
            var before = Assert.Single(probe.Capture());
            fail = true;
            var failed = Assert.Single(probe.Capture());
            Assert.Equal(ProbeExposure.Unknown, failed.Exposure);
            Assert.StartsWith("discovery_failed", failed.Diagnostic);
            fail = false;
            var recovered = Assert.Single(probe.Capture());
            Assert.True(recovered.Signals.Stable);
            Assert.Equal(before.Probe.Handle, recovered.Probe.Handle);
        }
        finally { DestroyWindow(parent); }
    }

    [Fact]
    public void ParentMismatchIsIsolatedAndNativeMessageJournalIsBounded()
    {
        var first = Parent();
        var second = Parent();
        try
        {
            using var probe = new Win32VisibilityProbe(() => [Target("a", first), Target("b", second)]);
            var initial = probe.Capture();
            var child = (nint)initial[0].Probe.Handle;
            for (var i = 0; i < 4200; i++) SendMessageW(child, 0x84, 0, 0);
            Assert.True(probe.DroppedTraceCount > 0);
            Assert.Equal(4096, probe.DrainTrace().Count);
            SetParent(child, second);
            var lost = probe.Capture();
            Assert.Equal(ProbeExposure.Unknown, lost[0].Exposure);
            Assert.Equal(initial[1].Probe.Handle, lost[1].Probe.Handle);
            Assert.True(IsWindow(second));
            var recovered = probe.Capture();
            Assert.Equal((long)first, recovered[0].ParentChain[0].Handle);
            probe.Dispose();
            probe.Dispose();
            Assert.Throws<ObjectDisposedException>(() => probe.Capture());
        }
        finally { DestroyWindow(first); DestroyWindow(second); }
    }

    private static ProbeTarget Target(string id, nint hwnd) => new(id, new(-30000, -30000, 400, 300), 96, (long)hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetParent(nint child, nint parent);
}

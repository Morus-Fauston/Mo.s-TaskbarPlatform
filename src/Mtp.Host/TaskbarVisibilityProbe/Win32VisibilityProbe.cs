using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Mtp.Platform.Core;

namespace Mtp.Host.Experiments;

/// <summary>
/// UI-thread-affine observation session. Caller must pump messages and capture periodically;
/// WinEvent is a diagnostic trigger, never proof of composited visibility. No product window is changed.
/// </summary>
public sealed class Win32VisibilityProbe : IDisposable
{
    private readonly uint thread = ProbeNative.GetCurrentThreadId();
    private readonly Func<IReadOnlyList<ProbeTarget>> discover;
    private readonly Dictionary<string, Child> children = new(StringComparer.Ordinal);
    private readonly Queue<ProbeTrace> trace = new();
    private readonly List<nint> hooks = new();
    private readonly ProbeNative.EventProcedure callback;
    private static readonly HashSet<Win32VisibilityProbe> LiveHooks = new();
    private long sequence, generation;
    private bool disposed, stopping;
    public long DroppedTraceCount { get; private set; }
    public string? HookError { get; private set; }

    public Win32VisibilityProbe(Func<IReadOnlyList<ProbeTarget>>? discover = null)
    {
        this.discover = discover ?? Discover;
        callback = OnEvent;
        foreach (var (first, last) in new (uint, uint)[] { (3, 3), (0x8001, 0x800B), (0x8017, 0x8018) })
        {
            var hook = ProbeNative.SetWinEventHook(first, last, 0, callback, 0, 0, 0);
            if (hook != 0) hooks.Add(hook);
            else HookError = $"WinEvent 0x{first:X}: {Marshal.GetLastPInvokeError()} (polling continues)";
        }
        // Native hooks do not root managed delegates. Retain the session until unhook is confirmed,
        // including the failure/retry path through Dispose.
        lock (LiveHooks) LiveHooks.Add(this);
    }

    public IReadOnlyList<ProbeSnapshot> Capture()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(disposed || stopping, this);
        IReadOnlyList<ProbeTarget> targets;
        try { targets = discover(); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return children.Values.Select(child => Read(child.Target, child, Stopwatch.GetTimestamp(),
                $"discovery_failed:{exception.GetType().Name}")).ToArray();
        }
        if (targets.Any(t => string.IsNullOrWhiteSpace(t.DisplayId)) ||
            targets.Select(t => t.DisplayId).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new InvalidOperationException("Each probe requires one unique display ID.");
        foreach (var id in children.Keys.Except(targets.Select(t => t.DisplayId), StringComparer.Ordinal).ToArray())
        {
            children[id].Close();
            children.Remove(id);
        }
        var samples = new List<ProbeSnapshot>();
        foreach (var target in targets)
        {
            var started = Stopwatch.GetTimestamp();
            var error = string.Empty;
            children.TryGetValue(target.DisplayId, out var child);
            // Report loss before replacing: the consumer must hide the old screen for this sample.
            var lost = child is not null && !child.Matches(target);
            if (lost)
            {
                try { child!.Close(); children.Remove(target.DisplayId); child = null; }
                catch (Win32Exception exception) { error = $"cleanup_pending:{exception.NativeErrorCode}"; }
                error = string.IsNullOrEmpty(error) ? "probe_lost_recreate_next_sample" : error;
            }
            if (child is null && !lost && target.Taskbar != 0 && ProbeNative.IsWindow((nint)target.Taskbar))
            {
                child = new Child(this, target, ++generation);
                children.Add(target.DisplayId, child); // Retain ownership even if creation/cleanup fails.
                try { child.Create(); }
                catch (Win32Exception exception) { error = $"create_failed:{exception.NativeErrorCode}"; }
            }
            samples.Add(Read(target, child, started, error));
        }
        return samples.AsReadOnly();
    }

    public IReadOnlyList<ProbeTrace> DrainTrace()
    {
        CheckThread();
        var result = trace.ToArray();
        trace.Clear();
        return result;
    }

    public void Dispose()
    {
        CheckThread();
        if (disposed) return;
        stopping = true;
        foreach (var id in children.Keys.ToArray())
        {
            children[id].Close();
            children.Remove(id);
        }
        for (var i = hooks.Count - 1; i >= 0; i--)
        {
            if (!ProbeNative.UnhookWinEvent(hooks[i])) throw new Win32Exception(Marshal.GetLastPInvokeError());
            hooks.RemoveAt(i);
        }
        disposed = true;
        lock (LiveHooks) LiveHooks.Remove(this);
        GC.KeepAlive(callback);
    }

    private void CheckThread()
    {
        if (ProbeNative.GetCurrentThreadId() != thread)
            throw new InvalidOperationException("Use the creating UI thread for capture, messages and cleanup.");
    }

    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint eventThread, uint time)
    {
        if (stopping || disposed || objectId != 0 || childId != 0) return;
        foreach (var child in children.Values)
            if (kind == 3 || (long)window == child.Target.Taskbar || window == child.Handle)
                Record("WinEvent", child.Target.DisplayId, (long)window, kind, objectId, childId, time);
    }

    private void Record(string source, string display, long window, uint code, long wParam, long lParam, uint time = 0)
    {
        if (trace.Count == 4096) { trace.Dequeue(); DroppedTraceCount++; }
        trace.Enqueue(new(++sequence, Stopwatch.GetTimestamp(), source, display, window, code, wParam, lParam, time));
    }

    private static ProbeWindowInfo WindowInfo(nint handle)
    {
        var exists = handle != 0 && ProbeNative.IsWindow(handle);
        var threadId = ProbeNative.GetWindowThreadProcessId(handle, out var process);
        return new((long)handle, exists,
            exists ? (uint)(long)ProbeNative.GetWindowLongPtrW(handle, -16) : 0,
            exists ? (uint)(long)ProbeNative.GetWindowLongPtrW(handle, -20) : 0,
            exists && ProbeNative.IsWindowVisible(handle),
            exists && ProbeNative.GetWindowRect(handle, out var rect) ? rect.Pixels : null, process, threadId);
    }

    private static ProbeSnapshot Read(ProbeTarget target, Child? child, long started, string error)
    {
        var taskbar = WindowInfo((nint)target.Taskbar);
        var probe = WindowInfo(child?.Handle ?? 0);
        var chain = new List<ProbeWindowInfo>();
        var parent = ProbeNative.GetAncestor((nint)probe.Handle, 1);
        for (var i = 0; parent != 0 && i < 16; i++)
        {
            if (chain.Any(p => p.Handle == (long)parent)) break;
            chain.Add(WindowInfo(parent));
            parent = ProbeNative.GetAncestor(parent, 1);
        }
        var layered = probe.Exists && ProbeNative.GetLayeredWindowAttributes((nint)probe.Handle, out _, out _, out _);
        byte? alpha = null;
        uint? flags = null;
        if (layered && ProbeNative.GetLayeredWindowAttributes((nint)probe.Handle, out _, out var a, out var f)) { alpha = a; flags = f; }
        uint? cloak = taskbar.Exists && ProbeNative.DwmGetWindowAttribute((nint)taskbar.Handle, 14, out var c, sizeof(uint)) >= 0 ? c : null;
        var defaultClip = ReadClip((nint)probe.Handle, false);
        var siblingClip = ReadClip((nint)probe.Handle, true);
        // A complex bounding box does not prove the absence of holes in the visible region.
        var clip = defaultClip.Exposure == siblingClip.Exposure ? defaultClip.Exposure : null;
        var taskbarAfter = WindowInfo((nint)target.Taskbar);
        var probeAfter = WindowInfo((nint)probe.Handle);
        var stable = taskbar == taskbarAfter && probe == probeAfter &&
            (long)ProbeNative.GetParent((nint)probe.Handle) == target.Taskbar &&
            Stopwatch.GetElapsedTime(started) <= TimeSpan.FromMilliseconds(100) && string.IsNullOrEmpty(error);
        var attributes = probe.Exists && (probe.Style & (ProbeNative.ChildStyle | 0x10000000)) == (ProbeNative.ChildStyle | 0x10000000) &&
            (probe.ExStyle & ProbeNative.ExtendedStyle) == ProbeNative.ExtendedStyle && alpha == 0 && flags == 2;
        bool? onScreen = target.Bounds.IsValid && taskbar.Bounds is PixelRect bar && probe.Bounds is PixelRect surface
            ? target.Bounds.Contains(bar) && target.Bounds.Contains(surface) && bar.Contains(surface) : null;
        if (taskbar.Bounds is PixelRect bounds && (bounds.Width <= bounds.Height || bounds.Bottom < target.Bounds.Bottom))
            onScreen = null; // A top/side/floating bar is outside this experiment's bottom-bar scope.
        if (cloak is > 0) onScreen = false;
        var signals = new ProbeSignals(taskbar.Exists, probe.Exists,
            chain.Count > 0 && chain[0].Handle == target.Taskbar, stable, attributes,
            taskbar.LogicallyVisible, probe.LogicallyVisible, onScreen, clip);
        var diagnostic = !string.IsNullOrEmpty(error) ? error : !taskbar.Exists ? "taskbar_missing" :
            !attributes ? "probe_attributes_invalid" : !stable ? "snapshot_changed_or_timed_out" :
            defaultClip.Exposure != siblingClip.Exposure ? "clip_signals_conflict" :
            clip is null ? "clip_unavailable_or_complex" : "experimental_gdi_signal_not_compositor_truth";
        return new(target, child?.Generation ?? 0, started, Stopwatch.GetTimestamp(), taskbar, probe,
            chain.AsReadOnly(), alpha, flags, cloak, defaultClip, siblingClip, signals,
            ProbeVisibilityPolicy.Evaluate(signals), diagnostic);
    }

    private static ProbeClipReading ReadClip(nint handle, bool siblings)
    {
        if (handle == 0 || !ProbeNative.GetClientRect(handle, out var client)) return new(0, null, null);
        var dc = siblings ? ProbeNative.GetDCEx(handle, 0, 0x2 | 0x10 | 0x8) : ProbeNative.GetDC(handle);
        if (dc == 0) return new(0, null, null);
        try
        {
            var kind = ProbeNative.GetClipBox(dc, out var bounds);
            ProbeClip? clip = kind switch
            {
                1 => ProbeClip.Empty,
                2 => bounds.Pixels == client.Pixels ? ProbeClip.Full : ProbeClip.Partial,
                _ => null,
            };
            return new(kind, kind == 0 ? null : bounds.Pixels, clip);
        }
        finally { ProbeNative.ReleaseDC(handle, dc); }
    }

    public static IReadOnlyList<ProbeTarget> Discover()
    {
        var targets = new List<ProbeTarget>();
        var monitorReadFailed = false;
        ProbeNative.MonitorProcedure callback = (nint monitor, nint dc, ref ProbeNative.Rect rect, nint data) =>
        {
            var info = new ProbeNative.MonitorInfo { Size = (uint)Marshal.SizeOf<ProbeNative.MonitorInfo>(), Device = string.Empty };
            if (!ProbeNative.GetMonitorInfoW(monitor, ref info)) { monitorReadFailed = true; return false; }
            var candidates = new List<nint>();
            foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                nint previous = 0;
                for (var i = 0; i < 32; i++)
                {
                    var window = ProbeNative.FindWindowExW(0, previous, cls, null);
                    if (window == 0) break;
                    if (ProbeNative.MonitorFromWindow(window, 2) == monitor) candidates.Add(window);
                    previous = window;
                }
            }
            var taskbar = candidates.Count == 1 ? candidates[0] : 0;
            var dpi = taskbar != 0 ? ProbeNative.GetDpiForWindow(taskbar) :
                ProbeNative.GetDpiForMonitor(monitor, 0, out var monitorDpi, out _) >= 0 ? monitorDpi : 0;
            targets.Add(new(info.Device, info.Bounds.Pixels, dpi, (long)taskbar));
            return true;
        };
        if (!ProbeNative.EnumDisplayMonitors(0, 0, callback, 0) || monitorReadFailed)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Monitor enumeration failed.");
        return targets.AsReadOnly();
    }

    private sealed class Child(Win32VisibilityProbe owner, ProbeTarget target, long generation)
    {
        private static readonly object ClassGate = new();
        private static readonly Dictionary<nint, Child> Windows = new();
        private static readonly ProbeNative.WindowProcedure Procedure = WindowProc;
        private static readonly string ClassName = $"Mtp.VisibilityProbe.05E.{Guid.NewGuid():N}";
        private static ushort classAtom;
        public ProbeTarget Target { get; } = target;
        public long Generation { get; } = generation;
        public nint Handle { get; private set; }
        private readonly uint parentThread = ProbeNative.GetWindowThreadProcessId((nint)target.Taskbar, out _);
        private bool ready;

        public bool Matches(ProbeTarget next) => ready && next.Taskbar == Target.Taskbar && next.Bounds == Target.Bounds && next.Dpi == Target.Dpi &&
            Handle != 0 && ProbeNative.IsWindow(Handle) && ProbeNative.GetParent(Handle) == (nint)Target.Taskbar &&
            ProbeNative.GetWindowThreadProcessId((nint)Target.Taskbar, out _) == parentThread;

        public void Create()
        {
            lock (ClassGate)
            {
                if (classAtom == 0)
                {
                    var wc = new ProbeNative.WindowClass
                    {
                        Size = (uint)Marshal.SizeOf<ProbeNative.WindowClass>(),
                        Procedure = Procedure,
                        Name = ClassName,
                        Instance = ProbeNative.GetModuleHandleW(null)
                    };
                    classAtom = ProbeNative.RegisterClassExW(ref wc);
                    if (classAtom == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            // Create hidden, make the layered surface truly transparent, then set WS_VISIBLE.
            Handle = ProbeNative.CreateWindowExW(ProbeNative.ExtendedStyle, ClassName, string.Empty,
                ProbeNative.ChildStyle, 1, 1, 4, 4, (nint)Target.Taskbar, 0, ProbeNative.GetModuleHandleW(null), 0);
            if (Handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            lock (ClassGate) Windows.Add(Handle, this);
            owner.Record("lifecycle", Target.DisplayId, (long)Handle, 1, Target.Taskbar, Generation);
            if (!ProbeNative.SetLayeredWindowAttributes(Handle, 0, 0, 2) ||
                ProbeNative.GetParent(Handle) != (nint)Target.Taskbar ||
                !ProbeNative.SetWindowPos(Handle, 0, 1, 1, 4, 4, 0x10 | 0x40))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            ready = true;
        }

        public void Close()
        {
            var owned = Handle;
            if (owned == 0) return;
            var nativeThread = ProbeNative.GetWindowThreadProcessId(owned, out var process);
            if (!ProbeNative.IsWindow(owned) || nativeThread != owner.thread || process != Environment.ProcessId ||
                ProbeNative.GetClassLongPtrW(owned, -32) != classAtom)
            {
                // An externally destroyed HWND may already be reused. Never destroy its replacement.
                lock (ClassGate) Windows.Remove(owned);
                Handle = 0;
                owner.Record("lifecycle", Target.DisplayId, (long)owned, 2, Target.Taskbar, Generation);
                return;
            }
            if (!ProbeNative.DestroyWindow(owned) && ProbeNative.IsWindow(owned))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (Handle != 0) throw new InvalidOperationException("Probe destruction was not confirmed by WM_NCDESTROY.");
        }

        private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
        {
            Child? child;
            lock (ClassGate) Windows.TryGetValue(window, out child);
            if (child is not null)
            {
                child.OwnerRecord(window, message, wParam, lParam);
                if (message == 0x82)
                {
                    child.Handle = 0;
                    lock (ClassGate) Windows.Remove(window);
                }
            }
            if (message == 0x84) return -1; // HTTRANSPARENT; layered alpha=0 and DISABLED also prevent input.
            if (message == 0x21) return 3; // MA_NOACTIVATE
            if (message == 0x14) return 1; // No background brush or content.
            if (message == 0xF)
            {
                // Begin/EndPaint also completes internal paints. ValidateRect alone left WM_PAINT
                // continuously pending and starved the message-loop timer in the native runner.
                ProbeNative.BeginPaint(window, out var paint);
                ProbeNative.EndPaint(window, ref paint);
                return 0;
            }
            return ProbeNative.DefWindowProcW(window, message, wParam, lParam);
        }

        private void OwnerRecord(nint window, uint message, nint wParam, nint lParam) =>
            owner.Record("message", Target.DisplayId, (long)window, message, (long)wParam, (long)lParam);
    }
}

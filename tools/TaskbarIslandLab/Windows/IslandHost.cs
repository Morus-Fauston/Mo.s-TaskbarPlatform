using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using TaskbarIslandLab.Logic;
using Windows.Graphics;

namespace TaskbarIslandLab.Windows;

internal sealed class IslandHost
{
    private readonly LabOptions options;
    private readonly Action<string, object?> record;
    private readonly IslandLifetime lifetime = new();
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private DesktopWindowXamlSource? source;
    private Window? window;
    private LabContent? content;
    private nint parent, host, bridge, dpiContext, threadContext;
    private uint dpi, parentProcess, parentThread;
    private int processDpiAwareness;
    private long token;
    private bool closing;
    private nint[] popupFixtureWindows = [];
    public event Action<string>? StopRequested;
    public long Token => token;
    public LifetimeState State => lifetime.State;
    public nint HostHandle => host;
    public nint ParentHandle => parent;
    public nint BridgeHandle => bridge;
    public bool Loaded => content?.IsLoaded == true;
    public bool HasExpectedTree => content?.HasExpectedTree == true;
    public double ContentWidth => content?.ActualWidth ?? 0;
    public double ContentHeight => content?.ActualHeight ?? 0;
    public uint Dpi => dpi;

    public IslandHost(LabOptions options, Action<string, object?> record) { this.options = options; this.record = record; }

    public void Start(string? failAfter = null)
    {
        token = lifetime.Begin();
        closing = false;
        var thisToken = token;
        try
        {
            threadContext = NativeWindows.GetThreadDpiAwarenessContext();
            processDpiAwareness = NativeWindows.ProcessDpiAwareness();
            if (options.Mode == "empty") { lifetime.Ready(token); return; }
            content = new LabContent(options, record);
            content.StopRequested += () => StopRequested?.Invoke("user-close");
            if (options.Mode == "top-level")
            {
                window = new Window();
                lifetime.Own("winui-window", () => { window.Close(); window = null; });
                host = WinRT.Interop.WindowNative.GetWindowHandle(window);
                NativeWindows.Show(host, false);
                NativeWindows.SetStyle(host, NativeWindows.Popup | NativeWindows.ClipChildren | NativeWindows.ClipSiblings);
                dpi = NativeWindows.GetDpiForWindow(host);
                NativeWindows.Place(host, options.X, options.Y, Pixels(260), Pixels(56));
                window.Content = content;
                window.Closed += (_, _) => { if (!closing) QueueStop(thisToken, "window-closed"); };
                lifetime.Own("content", () => { content.Release(); window.Content = null; content = null; });
                SetBackdrop(backdrop => window.SystemBackdrop = backdrop);
            }
            else
            {
                if (options.Mode == "explorer") parent = NativeWindows.ValidateExplorerTarget(options);
                else
                {
                    parent = NativeWindows.Create(options.X, options.Y, 520, 112, (message, _) =>
                    {
                        if (message is 0x10 or 0x82) QueueStop(thisToken, "parent-destroyed");
                    });
                    lifetime.Own("owned-parent", () => { NativeWindows.Destroy(parent); parent = 0; });
                }
                parentThread = NativeWindows.GetWindowThreadProcessId(parent, out parentProcess);
                dpi = NativeWindows.GetDpiForWindow(parent);
                record("parent-selected", new { chain = NativeWindows.ParentChain(parent), mode = options.Mode });
                if (options.Mode == "owned") NativeWindows.Place(parent, options.X, options.Y, Pixels(260), Pixels(56));
                host = NativeWindows.Create(options.X, options.Y, Pixels(260), Pixels(56), OnHostMessage);
                lifetime.Own("host", () => { NativeWindows.Destroy(host); host = 0; });
                if (!NativeWindows.IsOurs(host) || NativeWindows.IsWindowVisible(host)) throw new InvalidOperationException("Host must start hidden on its owning UI thread.");
                record("host-created-hidden", NativeWindows.Describe(host));
                FailAt(failAfter, "host");
                source = new DesktopWindowXamlSource();
                lifetime.Own("dwxs-and-owned-bridge", () =>
                {
                    record("release-source-begin", new { hostAlive = NativeWindows.IsWindow(host), bridgeAlive = NativeWindows.IsWindow(bridge) });
                    source.TakeFocusRequested -= OnTakeFocus;
                    source.GotFocus -= OnGotFocus;
                    record("release-source-events", null);
                    if (NativeWindows.IsWindow(bridge)) source.SystemBackdrop = null;
                    record("release-source-backdrop", null);
                    if (NativeWindows.IsWindow(bridge)) source.Content = null;
                    record("release-source-content", null);
                    source.Dispose(); // DWXS owns/Closes SiteBridge. Do not close it twice.
                    record("release-source-disposed", null);
                    source = null;
                    if (NativeWindows.IsWindow(bridge)) throw new InvalidOperationException("DWXS disposal left its bridge HWND alive.");
                    bridge = 0;
                });
                record("dwxs-initialize-begin", new { directParent = $"0x{host:X}", uiThread = NativeWindows.GetCurrentThreadId() });
                source.Initialize(Win32Interop.GetWindowIdFromWindow(host));
                bridge = Win32Interop.GetWindowFromWindowId(source.SiteBridge.WindowId);
                source.TakeFocusRequested += OnTakeFocus;
                source.GotFocus += OnGotFocus;
                source.ShouldConstrainPopupsToWorkArea = false;
                source.Content = content;
                lifetime.Own("content", () =>
                {
                    record("release-content-begin", null);
                    content.Release();
                    record("release-content-transients", null);
                    // Parent destruction can already close the native ContentIsland. WinUI's
                    // Content setter then dereferences that closed island; only Dispose is safe.
                    if (NativeWindows.IsWindow(bridge)) source.Content = null;
                    record("release-content-detached", null);
                    content = null;
                });
                source.SiteBridge.MoveAndResize(new RectInt32(0, 0, Pixels(260), Pixels(56)));
                record("bridge-created-under-hidden-host", new { bridge = NativeWindows.Describe(bridge), hostVisible = NativeWindows.IsWindowVisible(host) });
                FailAt(failAfter, "source");
                SetBackdrop(backdrop => source.SystemBackdrop = backdrop);
                record("attach-begin", new { host = NativeWindows.Describe(host), parent = NativeWindows.Describe(parent) });
                NativeWindows.Attach(host, parent);
                NativeWindows.Place(host, options.Mode == "explorer" ? options.X : 0, 0, Pixels(260), Pixels(56));
                FailAt(failAfter, "attach");
                source.SiteBridge.Show();
            }
            dpiContext = NativeWindows.GetWindowDpiAwarenessContext(host);
            if (!NativeWindows.AreDpiAwarenessContextsEqual(threadContext, NativeWindows.GetThreadDpiAwarenessContext()))
                throw new InvalidOperationException("DPI context changed during attachment; no DPI-reset workaround is permitted.");
            if (NativeWindows.ProcessDpiAwareness() != processDpiAwareness)
                throw new InvalidOperationException("Process DPI awareness reset during attachment.");
            if (options.Mode != "top-level" && !NativeWindows.AreDpiAwarenessContextsEqual(threadContext, dpiContext))
                throw new InvalidOperationException("Host DPI context changed during attachment.");
            lifetime.Ready(token);
            var fault = CheckHealth();
            if (fault != null) throw new InvalidOperationException(fault);
            SetVisible(true);
            record("initialized", new { mode = options.Mode, generation = token, dpi, processDpiAwareness, dip = new { width = 260, height = 56 }, pixels = new { width = Pixels(260), height = Pixels(56) }, chain = NativeWindows.ParentChain(bridge != 0 ? bridge : host), nativePresentation = "pending-human", renderedFps = "unknown" });
        }
        catch
        {
            Close("initialization-failed");
            throw;
        }
    }

    private static void FailAt(string? requested, string stage)
    {
        if (requested == stage) throw new InvalidOperationException("Injected initialization failure: " + stage);
    }
    private int Pixels(int dip) => (int)Math.Round(dip * dpi / 96.0);
    private void SetBackdrop(Action<SystemBackdrop?> set)
    {
        try
        {
            if (options.Material == "acrylic")
            {
                // Explorer is not ours. Do not change its root HWND to enable material.
                if (options.Mode == "explorer")
                    record("host-backdrop-unverified", new { reason = "external-parent-not-modified", appearance = "pending-human" });
                else
                {
                    var root = options.Mode == "owned" ? parent : host;
                    var result = NativeWindows.EnableHostBackdrop(root);
                    record("host-backdrop-initialized", new { hwnd = $"0x{root:X}", hresult = $"0x{result:X8}", ownedTopLevel = true, appearance = "pending-human" });
                }
            }
            set(options.Material switch { "acrylic" => new NonActivatingAcrylicBackdrop(record), "mica" => new MicaBackdrop(), _ => null });
            record("material-api", new { requested = options.Material, options.Alpha, result = "assigned", appearance = "pending-human", note = options.Material == "mica" ? "Mica is opaque, not a transparency pass." : "API assignment is not visual evidence." });
        }
        catch (Exception error)
        {
            set(null);
            record("material-api", new { requested = options.Material, result = "failed", error = error.ToString(), appearance = "failed-or-unknown; no fallback claimed as success" });
        }
    }
    private void QueueStop(long generation, string reason)
    {
        dispatcher.TryEnqueue(() => { if (lifetime.Accepts(generation) && !closing) StopRequested?.Invoke(reason); });
    }
    private void OnHostMessage(uint message, nuint wparam)
    {
        var generation = token;
        if (message is 0x10 or 0x82) QueueStop(generation, message == 0x10 ? "user-close" : "host-destroyed");
        if (message == 0x7 || (message == 0x100 && wparam == 9))
            dispatcher.TryEnqueue(() =>
            {
                if (!lifetime.Accepts(generation) || source == null) return;
                try { source.NavigateFocus(new XamlSourceFocusNavigationRequest(NativeWindows.GetKeyState(0x10) < 0 ? XamlSourceFocusNavigationReason.Last : XamlSourceFocusNavigationReason.First)); }
                catch (Exception error) { record("focus-failed", error.Message); }
            });
    }
    private void OnGotFocus(DesktopWindowXamlSource sender, object args) => record("island-got-focus", null);
    private void OnTakeFocus(DesktopWindowXamlSource sender, DesktopWindowXamlSourceTakeFocusRequestedEventArgs args)
    {
        if (!lifetime.Accepts(token)) return;
        // This fixture has a single island; wrap forward/backward traversal inside it.
        var reason = args.Request.Reason == XamlSourceFocusNavigationReason.Last ? XamlSourceFocusNavigationReason.Last : XamlSourceFocusNavigationReason.First;
        sender.NavigateFocus(new XamlSourceFocusNavigationRequest(reason));
        record("focus-wrap", args.Request.Reason.ToString());
    }
    public bool Update(long generation, long value)
    {
        if (!lifetime.Accepts(generation) || content == null) return false;
        content.Update(value);
        return true;
    }
    public string? CheckHealth()
    {
        if (State != LifetimeState.Running) return "instance-not-running";
        if (options.Mode == "empty") return null;
        if (!NativeWindows.IsWindow(host) || !NativeWindows.IsOurs(host)) return "host-lost";
        if (!NativeWindows.AreDpiAwarenessContextsEqual(dpiContext, NativeWindows.GetWindowDpiAwarenessContext(host)) ||
            !NativeWindows.AreDpiAwarenessContextsEqual(threadContext, NativeWindows.GetThreadDpiAwarenessContext()) ||
            NativeWindows.ProcessDpiAwareness() != processDpiAwareness || NativeWindows.GetDpiForWindow(host) != dpi)
            return "dpi-context-or-scale-changed";
        if (options.Mode == "top-level") return null;
        if (!NativeWindows.IsWindow(parent) || NativeWindows.GetWindowThreadProcessId(parent, out var pid) != parentThread || pid != parentProcess) return "parent-lost";
        if (NativeWindows.GetParent(host) != parent) return "parent-relation-lost";
        if (!NativeWindows.IsWindow(bridge) || NativeWindows.GetParent(bridge) != host) return "bridge-lost";
        return null;
    }
    public void SetVisible(bool visible)
    {
        if (!visible) content?.CloseTransient();
        if (options.Mode == "empty") return;
        // Never change Explorer's visibility. Its hide/restore stages are human operated.
        NativeWindows.Show(options.Mode == "owned" ? parent : host, visible);
        if (options.Mode == "owned") NativeWindows.Show(host, visible);
    }
    public void DismissIfParentHidden()
    {
        if (options.Mode == "explorer" && !NativeWindows.IsWindowVisible(parent)) content?.CloseTransient();
    }
    public void ResizeOwnedFixture(int widthDip, int heightDip)
    {
        if (options.Mode != "owned" || source == null) throw new InvalidOperationException("Owned fixture only.");
        NativeWindows.Place(parent, options.X, options.Y, Pixels(widthDip), Pixels(heightDip));
        NativeWindows.Place(host, 0, 0, Pixels(widthDip), Pixels(heightDip));
        source.SiteBridge.MoveAndResize(new RectInt32(0, 0, Pixels(widthDip), Pixels(heightDip)));
    }
    public void DestroyOwnedParentFixture()
    {
        if (options.Mode != "owned") throw new InvalidOperationException("Owned fixture only.");
        NativeWindows.Destroy(parent);
    }
    internal bool PopupIsOpen => content?.PopupIsOpen == true;
    internal void OpenPopupAtEdgeFixture(bool bottomRight)
    {
        if (options.Mode != "owned" || content == null) throw new InvalidOperationException("Owned fixture only.");
        var area = NativeWindows.DisplayBounds(parent);
        // Simulate the reported bottom taskbar: 48 DIP high, with a 56 DIP island.
        NativeWindows.Place(parent, bottomRight ? area.Right - Pixels(260) : area.Left,
            bottomRight ? area.Bottom - Pixels(48) : area.Top, Pixels(260), Pixels(56));
        content.TogglePopupFixture();
    }
    internal bool PopupFitsDisplayFixture()
    {
        if (options.Mode != "owned" || content == null) throw new InvalidOperationException("Owned fixture only.");
        var layout = content.ReadPopupFixture();
        var display = NativeWindows.DisplayBounds(parent);
        var root = NativeWindows.Client(bridge);
        var origin = NativeWindows.WindowBounds(bridge);
        var constrained = content.PopupConstrainedToRoot;
        var visibleWindows = NativeWindows.OwnThreadTopLevelWindows().Where(NativeWindows.IsWindowVisible).ToArray();
        popupFixtureWindows = visibleWindows.Where(hwnd => hwnd != parent && hwnd != host && hwnd != bridge).ToArray();
        global::Windows.Foundation.Rect ToScreen(global::Windows.Foundation.Rect rect) => new(rect.X + origin.Left, rect.Y + origin.Top, rect.Width, rect.Height);
        bool Fits(global::Windows.Foundation.Rect rect, NativeWindows.Rect bounds) => rect.Width > 0 && rect.Height > 0 &&
            rect.Left >= bounds.Left && rect.Top >= bounds.Top && rect.Right <= bounds.Right && rect.Bottom <= bounds.Bottom;
        // In-place Popup UIA bounds include content outside the root clip. Check the
        // actual presentation surface as well; UIA IsOffscreen alone misses this bug.
        var fitsRoot = layout.Panel.Left >= 0 && layout.Panel.Top >= 0 && layout.Panel.Right <= root.Right && layout.Panel.Bottom <= root.Bottom;
        var panel = ToScreen(layout.Panel);
        var label = ToScreen(layout.Label);
        var closeButton = ToScreen(layout.CloseButton);
        var presentationFits = constrained ? fitsRoot : visibleWindows
            .Where(hwnd => hwnd != parent && hwnd != host && hwnd != bridge)
            .Select(NativeWindows.WindowBounds)
            .Any(bounds => Fits(panel, bounds) && Fits(label, bounds) && Fits(closeButton, bounds));
        record("popup-layout-fixture", new { panel, label, closeButton, layout.CloseOffscreen, display, root, origin, constrained, fitsRoot, presentationFits, windows = visibleWindows.Select(NativeWindows.Describe).ToArray(), isOpen = content.PopupIsOpen });
        return content.PopupIsOpen && presentationFits && Fits(panel, display) && Fits(label, display) && Fits(closeButton, display) && !layout.CloseOffscreen;
    }
    internal void InvokePopupCloseFixture() => content!.InvokePopupCloseFixture();
    internal bool PopupWindowsHiddenFixture() => popupFixtureWindows.All(hwnd => !NativeWindows.IsWindowVisible(hwnd));
    internal bool PopupWindowsDestroyedFixture() => popupFixtureWindows.All(hwnd => !NativeWindows.IsWindow(hwnd));
    public void LoseOwnedParentRelationFixture()
    {
        if (options.Mode != "owned") throw new InvalidOperationException("Owned fixture only.");
        NativeWindows.SetParent(host, 0);
    }
    public void Close(string reason)
    {
        closing = true;
        if (host != 0 && NativeWindows.IsOurs(host)) NativeWindows.Show(host, false);
        lifetime.Stop(reason);
        record("cleanup", new { reason = lifetime.StopReason, state = State.ToString(), errors = lifetime.CleanupErrors, hostAlive = NativeWindows.IsWindow(host), bridgeAlive = NativeWindows.IsWindow(bridge), ownedParentAlive = options.Mode == "owned" && NativeWindows.IsWindow(parent) });
        if (State != LifetimeState.Closed) throw new InvalidOperationException("Cleanup incomplete: " + string.Join("; ", lifetime.CleanupErrors));
    }
}

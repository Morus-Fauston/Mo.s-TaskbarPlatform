using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Mtp.Platform.Core;
using Windows.Graphics;

namespace Mtp.Host.Islands;

/// <summary>Same UI-thread native host for declaration display, diagnostics and owned-parent tests.</summary>
internal sealed class ContentIslandHost
{
    private readonly IslandLifetime lifetime = new();
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Action<string, object?> record;
    private readonly Func<Mtp.Contracts.ActionSlotReference, Task>? invokeAction;
    private readonly Func<HostComponentDisplayModel, Templates.TemplateRenderer?>? createTemplate;
    private readonly Action<ItemInteractionHandle, string?>? activate;
    private DesktopWindowXamlSource? source;
    private IslandContent? content;
    private nint host, bridge, parent, threadDpi;
    private uint parentThread, parentProcess, dpi;
    private long token;
    private long hostOwnership;
    private bool closing;
    private PixelRect? lastLocalBounds;
    public ContentIslandHost(Action<string, object?> record, Func<Mtp.Contracts.ActionSlotReference, Task>? invokeAction = null,
        Func<HostComponentDisplayModel, Templates.TemplateRenderer?>? createTemplate = null,
        Action<ItemInteractionHandle, string?>? activate = null)
    { this.record = record; this.invokeAction = invokeAction; this.createTemplate = createTemplate; this.activate = activate; }
    public event Action? Lost;
    public nint Handle => host;
    public nint Bridge => bridge;
    internal Microsoft.UI.Xaml.FrameworkElement? ContentRoot => content;
    internal object PresentationSnapshot() => new
    {
        host = NativeWindows.Describe(host),
        bridge = NativeWindows.Describe(bridge),
        parent = NativeWindows.Describe(parent),
        loaded = content?.IsLoaded,
        width = content?.ActualWidth,
        height = content?.ActualHeight,
        xamlVisible = content?.XamlRoot?.IsHostVisible,
        xamlSize = content?.XamlRoot?.Size,
        xamlScale = content?.XamlRoot?.RasterizationScale,
        MaterialStatus
    };
    public bool IsAlive => lifetime.State == LifetimeState.Running && NativeWindows.HasOwnership(host, hostOwnership) && NativeWindows.IsWindow(host) && NativeWindows.IsOurs(host) &&
        NativeWindows.IsWindow(parent) && NativeWindows.GetWindowThreadProcessId(parent, out var pid) == parentThread && pid == parentProcess &&
        NativeWindows.GetParent(host) == parent && NativeWindows.IsWindow(bridge) && NativeWindows.GetParent(bridge) == host &&
        NativeWindows.GetDpiForWindow(parent) == dpi;
    public bool PopupOpen => content?.PopupOpen == true;
    public string MaterialStatus { get; private set; } = "未运行";
    public HostTestConfiguration? Configuration { get; private set; }

    public void Start(nint target, PixelRect screenBounds, HostComponentDisplayModel component, HostTestConfiguration config, string? failAfter = null)
    {
        token = lifetime.Begin();
        closing = false;
        lastLocalBounds = null;
        var generation = token;
        parent = target;
        try
        {
            parentThread = NativeWindows.GetWindowThreadProcessId(parent, out parentProcess);
            dpi = NativeWindows.GetDpiForWindow(parent);
            threadDpi = NativeWindows.GetThreadDpiAwarenessContext();
            host = NativeWindows.Create(screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height,
                (message, wparam) => OnMessage(generation, message, wparam));
            hostOwnership = NativeWindows.Ownership(host);
            lifetime.Own("host", () => { NativeWindows.Destroy(host, hostOwnership); host = 0; hostOwnership = 0; });
            Fail(failAfter, "host");
            source = new DesktopWindowXamlSource();
            lifetime.Own("source", () =>
            {
                source.TakeFocusRequested -= TakeFocus;
                if (NativeWindows.IsWindow(bridge)) { source.SystemBackdrop = null; source.Content = null; }
                source.Dispose();
                source = null;
                if (NativeWindows.IsWindow(bridge)) throw new InvalidOperationException("内容岛桥接窗口未销毁。");
                bridge = 0;
            });
            // DWXS's direct parent is always MTP's own same-thread HWND, never Explorer.
            source.Initialize(Win32Interop.GetWindowIdFromWindow(host));
            bridge = Win32Interop.GetWindowFromWindowId(source.SiteBridge.WindowId);
            source.ShouldConstrainPopupsToWorkArea = false;
            source.TakeFocusRequested += TakeFocus;
            content = new IslandContent(component, config, record, invokeAction, createTemplate);
            lifetime.Own("content", () => { content.Release(); if (NativeWindows.IsWindow(bridge)) source.Content = null; content = null; });
            source.Content = content;
            source.SiteBridge.MoveAndResize(new RectInt32(0, 0, screenBounds.Width, screenBounds.Height));
            Fail(failAfter, "source");
            ApplyMaterial(config);
            NativeWindows.Attach(host, parent);
            Move(screenBounds);
            Fail(failAfter, "attach");
            if (!NativeWindows.AreDpiAwarenessContextsEqual(threadDpi, NativeWindows.GetThreadDpiAwarenessContext()) ||
                !NativeWindows.AreDpiAwarenessContextsEqual(threadDpi, NativeWindows.GetWindowDpiAwarenessContext(host)))
                throw new InvalidOperationException("嵌入改变了 DPI 上下文，停止本次承载。");
            lifetime.Ready(token);
            if (!IsAlive) throw new InvalidOperationException("内容岛父子关系校验失败。");
            source.SiteBridge.Show();
            NativeWindows.Show(host, true);
            Configuration = config;
            record("island-started", new { chain = NativeWindows.ParentChain(bridge), dpi, screenBounds, config, appearance = "pending-human" });
        }
        catch { Close(); throw; }
    }

    private static void Fail(string? requested, string stage) { if (requested == stage) throw new InvalidOperationException("Injected " + stage); }
    private void ApplyMaterial(HostTestConfiguration config)
    {
        try
        {
            if (config.Material == "acrylic" && !Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
                throw new NotSupportedException("当前 Windows 不支持 Desktop Acrylic 控制器。");
            if (config.Material == "mica" && !Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                throw new NotSupportedException("当前 Windows 不支持 Mica 控制器。");
            source!.SystemBackdrop = config.Material switch
            {
                "acrylic" => new NonActivatingAcrylicBackdrop((kind, value) => { if (kind == "acrylic-controller-state") MaterialStatus = $"Acrylic：{System.Text.Json.JsonSerializer.Serialize(value)}"; record(kind, value); }),
                "mica" => new MicaBackdrop(),
                _ => null
            };
            MaterialStatus = $"{config.Material} API 已设置；外观待人工。" + (config.Material == "mica" ? "Mica 为不透明系统材质。" : "");
            record("material-assigned", new { config.Material, config.Alpha, appearance = "pending-human", explorerRootModified = false });
        }
        catch (Exception error)
        {
            source!.SystemBackdrop = null;
            MaterialStatus = "材质不可用：" + error.Message;
            record("material-failed", MaterialStatus);
        }
    }
    public void ApplyAppearance(HostAppearancePreferences appearance)
    {
        if (!IsAlive) return;
        var request = new MaterialSpec(appearance.Material, appearance.Opacity);
        bool highContrast = content?.HighContrast == true;
        var resolution = MaterialResolver.Resolve(request, new(content is not null && source?.Content == content && !highContrast,
            Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported(),
            Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()));
        var effective = highContrast ? new MaterialSpec(MaterialKind.None, 1) : resolution.Effective;
        var config = new HostTestConfiguration(effective.Kind is MaterialKind.None or MaterialKind.Solid ? "none" : effective.Kind.ToString().ToLowerInvariant(),
            effective.Kind == MaterialKind.None ? 1 : effective.Opacity, appearance.Theme.ToString().ToLowerInvariant());
        Configuration = config;
        content?.ApplyAppearance(config);
        try
        {
            source!.SystemBackdrop = effective.Kind switch
            {
                MaterialKind.Acrylic => new ConfiguredMaterialBackdrop(MaterialKind.Acrylic, effective.Opacity, record),
                MaterialKind.Mica => new ConfiguredMaterialBackdrop(MaterialKind.Mica, effective.Opacity, record),
                _ => null,
            };
            MaterialStatus = $"请求：{MaterialResolver.Describe(request.Kind)}；实际API：{MaterialResolver.Describe(effective.Kind)}。{(highContrast ? "高对比使用系统不透明背景。" : resolution.DowngradeReason)} 外观待人工确认。";
        }
        catch (Exception error)
        {
            source!.SystemBackdrop = null;
            content?.ApplyAppearance(config with { Material = "none", Alpha = 1 });
            MaterialStatus = $"请求：{MaterialResolver.Describe(request.Kind)}；实际：不透明回退。{error.Message}";
        }
    }
    public void Move(PixelRect screenBounds)
    {
        var origin = NativeWindows.ClientOrigin(parent);
        long localX = (long)screenBounds.X - origin.X, localY = (long)screenBounds.Y - origin.Y;
        if (localX < int.MinValue || localX > int.MaxValue || localY < int.MinValue || localY > int.MaxValue ||
            localX + screenBounds.Width > int.MaxValue || localY + screenBounds.Height > int.MaxValue)
            throw new InvalidOperationException("内容岛相对父窗口的坐标超出原生整数范围。");
        var local = new PixelRect((int)localX, (int)localY, screenBounds.Width, screenBounds.Height);
        if (local == lastLocalBounds) return;
        NativeWindows.Place(host, local.X, local.Y, local.Width, local.Height);
        source!.SiteBridge.MoveAndResize(new RectInt32(0, 0, screenBounds.Width, screenBounds.Height));
        lastLocalBounds = local;
    }
    public void UpdateGroup(HostGroupPresentationSnapshot snapshot, TaskbarGroupAnimationFrame frame)
    {
        if (!IsAlive) return;
        content?.ApplyGroup(snapshot, frame, invokeAction, createTemplate, activate);
        // Commit the Canvas positions with the same native size update, before observers/input use this frame.
        content?.UpdateLayout();
        NativeWindows.Show(host, frame.WidthDip > 0);
    }
    public void Update(long value) { if (IsAlive) content?.Update(value); }
    public void UpdateConfirmed(HostComponentDisplayModel component) { if (IsAlive) content?.UpdateConfirmed(component); }
    public void Observe() => record("island-observed", new
    {
        IsAlive,
        Configuration,
        MaterialStatus,
        contentLoaded = content?.IsLoaded == true,
        chain = NativeWindows.ParentChain(bridge),
        dpi,
        localBounds = lastLocalBounds
    });
    public void SetPopup(bool open) { if (IsAlive) content?.SetPopup(open); }
    public void CloseTransientIfParentHidden() { if (!NativeWindows.IsWindowVisible(parent)) content?.SetPopup(false); }
    private void OnMessage(long generation, uint message, nuint wparam)
    {
        if (message is 0x10 or 0x82)
            dispatcher.TryEnqueue(() => { if (!closing && lifetime.Accepts(generation)) Lost?.Invoke(); });
        if (message == 7 || message == 0x100 && wparam == 9)
            dispatcher.TryEnqueue(() =>
            {
                if (!lifetime.Accepts(generation) || !IsAlive) return;
                try { source!.NavigateFocus(new XamlSourceFocusNavigationRequest(NativeWindows.GetKeyState(0x10) < 0 ? XamlSourceFocusNavigationReason.Last : XamlSourceFocusNavigationReason.First)); }
                catch (Exception error) { record("focus-failed", error.Message); }
            });
    }
    private void TakeFocus(DesktopWindowXamlSource sender, DesktopWindowXamlSourceTakeFocusRequestedEventArgs args)
    {
        if (!IsAlive || closing) return;
        sender.NavigateFocus(new XamlSourceFocusNavigationRequest(args.Request.Reason == XamlSourceFocusNavigationReason.Last ? XamlSourceFocusNavigationReason.Last : XamlSourceFocusNavigationReason.First));
        record("focus-wrap", args.Request.Reason.ToString());
    }
    public void Close()
    {
        closing = true;
        if (NativeWindows.HasOwnership(host, hostOwnership) && NativeWindows.IsWindow(host) && NativeWindows.IsOurs(host)) NativeWindows.Show(host, false);
        lifetime.Stop("close");
        record("island-cleanup", new { state = lifetime.State.ToString(), lifetime.CleanupErrors });
        if (lifetime.State != LifetimeState.Closed) throw new InvalidOperationException(string.Join("; ", lifetime.CleanupErrors));
    }
}

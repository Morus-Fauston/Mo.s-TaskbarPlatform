using Microsoft.UI.Dispatching;
using Mtp.Platform.Core;

namespace Mtp.Host.Islands;

/// <summary>Explicit visual preview using the same island as taskbar display. Never a fallback.</summary>
internal sealed class IslandPreviewWindow
{
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private ContentIslandHost? island;
    private HostComponentDisplayModel? component;
    private long ownership;
    private bool queued;
    private bool closing;
    private long generation;
    internal Action<string, object?>? DiagnosticObserver { get; set; }
    internal object? PresentationSnapshot() => island?.PresentationSnapshot();
    public nint Handle { get; private set; }
    internal nint IslandHandle => island?.Handle ?? 0;
    internal nint Bridge => island?.Bridge ?? 0;
    internal Microsoft.UI.Xaml.FrameworkElement? ContentRoot => island?.ContentRoot;
    public bool IsOpen => NativeWindows.HasOwnership(Handle, ownership) && NativeWindows.IsWindow(Handle);
    public bool IsAlive => IsOpen && island?.IsAlive == true;
    public bool PopupOpen => island?.PopupOpen == true;
    public HostTestConfiguration? Configuration { get; private set; }
    public string Status { get; private set; } = "尚未打开独立预览。";
    public event Action? Changed;

    public void Open(nint owner, HostComponentDisplayModel value, HostTestConfiguration config)
    {
        if (!config.IsValid) throw new ArgumentException("预览参数无效。", nameof(config));
        component = value;
        if (IsAlive && Configuration == config) return;
        closing = false;
        try
        {
            if (!IsOpen)
            {
                generation++;
                queued = false;
                var origin = NativeWindows.WindowBounds(owner);
                var scale = NativeWindows.GetDpiForWindow(owner) / 96d;
                Handle = NativeWindows.CreatePreview(origin.Left + (int)(40 * scale), origin.Top + (int)(60 * scale),
                    (int)(520 * scale), (int)(200 * scale), owner, OnMessage);
                ownership = NativeWindows.Ownership(Handle);
            }
            Configuration = config;
            // DWXS caches site visibility while initializing. Its parent must already
            // be visible, otherwise a later parent Show leaves XamlRoot hidden.
            NativeWindows.Show(Handle, true);
            Rebuild();
            DiagnosticObserver?.Invoke("preview-opened", PresentationSnapshot());
            Changed?.Invoke();
        }
        catch (Exception error)
        {
            Status = "独立预览失败：" + error.Message;
            CloseResources();
            Changed?.Invoke();
            throw;
        }
    }

    private PixelRect Bounds()
    {
        var origin = NativeWindows.ClientOrigin(Handle);
        var client = NativeWindows.Client(Handle);
        return new(origin.X, origin.Y, client.Right, client.Bottom);
    }
    private void Rebuild()
    {
        island?.Close();
        if (Configuration!.Material == "acrylic")
        {
            // Same prerequisite as the verified owned-parent experiment. Never write Explorer.
            var result = NativeWindows.EnableHostBackdrop(Handle);
            DiagnosticObserver?.Invoke("preview-backdrop-initialized", new { hresult = result, ownedTopLevel = true });
        }
        island = new ContentIslandHost((kind, value) => DiagnosticObserver?.Invoke(kind, value));
        island.Start(Handle, Bounds(), component!, Configuration!);
        Status = $"独立预览 · {Configuration!.Material} / alpha {Configuration.Alpha} / {Configuration.Theme} · " +
            (Configuration.Controls ? "测试控件已打开。" : "仅显示声明文字。") + " " + island.MaterialStatus;
    }
    private void OnMessage(uint message, nuint wparam)
    {
        if (closing) return;
        var current = generation;
        if (message is 0x10 or 0x82)
        {
            dispatcher.TryEnqueue(() =>
            {
                if (closing || generation != current) return;
                try { Close(); }
                catch (Exception error) { Status = "预览清理失败，可再次关闭：" + error.Message; Changed?.Invoke(); }
            });
            return;
        }
        if (message == 7 && IsAlive) NativeWindows.SetFocus(island!.Handle);
        // Moving a top-level window moves its children natively. Do not rebuild on WM_MOVE.
        if (message is not (5 or 0x2E0) || message == 5 && wparam == 1 || queued) return;
        queued = true;
        dispatcher.TryEnqueue(() =>
        {
            if (generation != current) return;
            queued = false;
            if (closing || !IsOpen) return;
            try
            {
                if (!IsAlive) Rebuild(); // A DPI change requires a fresh host generation.
                else island!.Move(Bounds());
            }
            catch (Exception error)
            {
                Status = "预览调整失败：" + error.Message;
                CloseResources();
            }
            Changed?.Invoke();
        });
    }
    public void SetPopup(bool open)
    {
        if (!IsAlive || Configuration?.Controls != true) throw new InvalidOperationException("先打开含测试控件的独立预览。");
        island!.SetPopup(open);
        Changed?.Invoke();
    }
    public void Close()
    {
        CloseResources();
        Status = "独立预览已关闭。";
        Changed?.Invoke();
    }
    private void CloseResources()
    {
        closing = true;
        generation++;
        island?.Close();
        island = null;
        NativeWindows.Destroy(Handle, ownership);
        Handle = 0;
        ownership = 0;
    }
}

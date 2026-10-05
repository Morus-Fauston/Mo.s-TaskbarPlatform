using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mtp.Host.Flyouts;

/// <summary>Aggregates self-owned panel/header/menu input into four bounded lifetime sources.</summary>
internal sealed class EventGroupInteraction : IDisposable
{
    private readonly Func<string, bool, bool> changed;
    private readonly Dictionary<FrameworkElement, Subscription> roots = [];
    private readonly Dictionary<FlyoutBase, MenuSubscription> menus = [];
    private readonly HashSet<FlyoutBase> openMenus = [];
    private readonly Dictionary<string, bool> published = new(StringComparer.Ordinal)
        { ["hover"] = false, ["pressed"] = false, ["capture"] = false, ["keyboard"] = false };
    private bool disposed;

    internal EventGroupInteraction(Func<string, bool, bool> changed) => this.changed = changed;
    internal void Attach(FrameworkElement root, Func<nint> handle)
    {
        if (disposed || roots.ContainsKey(root)) return;
        if (roots.Count >= 16) throw new InvalidOperationException("事件组交互窗口预算已满");
        var subscription = new Subscription(handle);
        subscription.Down = (_, _) => { subscription.Pressed = true; Poll(); };
        subscription.Up = (_, _) => { subscription.Pressed = false; Poll(); };
        subscription.Focus = (_, _) => QueuePoll(root);
        root.AddHandler(UIElement.PointerPressedEvent, subscription.Down, true);
        root.AddHandler(UIElement.PointerReleasedEvent, subscription.Up, true);
        root.AddHandler(UIElement.PointerCanceledEvent, subscription.Up, true);
        root.AddHandler(UIElement.PointerCaptureLostEvent, subscription.Up, true);
        root.GotFocus += subscription.Focus; root.LostFocus += subscription.Focus;
        roots.Add(root, subscription);
        SynchronizeMenus();
    }
    internal void Detach(FrameworkElement root)
    {
        if (!roots.Remove(root, out var item)) return;
        root.RemoveHandler(UIElement.PointerPressedEvent, item.Down);
        root.RemoveHandler(UIElement.PointerReleasedEvent, item.Up);
        root.RemoveHandler(UIElement.PointerCanceledEvent, item.Up);
        root.RemoveHandler(UIElement.PointerCaptureLostEvent, item.Up);
        root.GotFocus -= item.Focus; root.LostFocus -= item.Focus;
        SynchronizeMenus();
        Poll();
    }
    private bool queued;
    private void QueuePoll(FrameworkElement root)
    {
        if (queued || disposed) return;
        queued = true;
        if (!root.DispatcherQueue.TryEnqueue(() => { queued = false; if (!disposed) Poll(); })) queued = false;
    }
    internal void SynchronizeMenus()
    {
        if (disposed) return;
        var current = roots.Keys.SelectMany(Descendants).OfType<Button>().Select(button => button.Flyout)
            .OfType<FlyoutBase>().ToHashSet();
        if (current.Count > 16) throw new InvalidOperationException("事件组受控菜单预算已满");
        foreach (var menu in menus.Keys.Where(value => !current.Contains(value)).ToArray())
        {
            var old = menus[menu]; menu.Opened -= old.Opened; menu.Closed -= old.Closed;
            menus.Remove(menu); openMenus.Remove(menu);
        }
        foreach (var menu in current.Where(value => !menus.ContainsKey(value)))
        {
            EventHandler<object> opened = (_, _) => { openMenus.Add(menu); Poll(); };
            EventHandler<object> closed = (_, _) => { openMenus.Remove(menu); Poll(); };
            menus.Add(menu, new(opened, closed)); menu.Opened += opened; menu.Closed += closed;
        }
    }
    internal void Poll()
    {
        if (disposed) return;
        nint[] handles = roots.Values.Select(value => value.Handle()).Where(value => value != 0 && FlyoutNative.IsWindow(value)).ToArray();
        bool hover = openMenus.Count > 0 || FlyoutNative.GetCursorPos(out var point) &&
            FlyoutNative.BelongsTo(FlyoutNative.WindowFromPoint(point), handles);
        bool pressed = roots.Values.Any(value => value.Pressed);
        if ((GetAsyncKeyState(1) & 0x8000) == 0 && (GetAsyncKeyState(2) & 0x8000) == 0)
        { foreach (var item in roots.Values) item.Pressed = false; pressed = false; }
        bool captured = FlyoutNative.BelongsTo(GetCapture(), handles);
        bool keyboard = openMenus.Count > 0 || FlyoutNative.BelongsTo(FlyoutNative.CurrentFocus(), handles);
        Publish("hover", hover); Publish("pressed", pressed); Publish("capture", captured); Publish("keyboard", keyboard);
    }
    private void Publish(string source, bool active)
    {
        if (published[source] == active) return;
        if (changed(source, active)) published[source] = active;
    }
    public void Dispose()
    {
        if (disposed) return;
        foreach (var root in roots.Keys.ToArray()) Detach(root);
        foreach (var menu in menus.ToArray())
        { menu.Key.Opened -= menu.Value.Opened; menu.Key.Closed -= menu.Value.Closed; }
        menus.Clear(); openMenus.Clear();
        foreach (string source in published.Keys.ToArray()) Publish(source, false);
        disposed = true;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private sealed class Subscription(Func<nint> handle)
    {
        internal readonly Func<nint> Handle = handle;
        internal bool Pressed;
        internal PointerEventHandler Down = null!, Up = null!;
        internal RoutedEventHandler Focus = null!;
    }
    private sealed record MenuSubscription(EventHandler<object> Opened, EventHandler<object> Closed);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetCapture();
}

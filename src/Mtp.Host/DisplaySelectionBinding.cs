using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Mtp.Host;

/// <summary>Keeps ComboBox containers stable while its popup owns selection/focus.</summary>
internal sealed class DisplaySelectionBinding
{
    private readonly ComboBox combo;
    private readonly NumberBox gap;
    private readonly HostConsoleController controller;
    private readonly Func<IReadOnlyList<TaskbarDockDisplay>> displays;
    private readonly DispatcherQueue dispatcher;
    private bool applying, queued, stopped;
    private string key = "";
    public string? Error { get; private set; }
    public DisplaySelectionBinding(ComboBox combo, NumberBox gap, HostConsoleController controller, Func<IReadOnlyList<TaskbarDockDisplay>> displays, DispatcherQueue dispatcher)
    {
        this.combo = combo; this.gap = gap; this.controller = controller; this.displays = displays; this.dispatcher = dispatcher;
        combo.DropDownClosed += OnClosed;
    }
    public void Refresh()
    {
        if (stopped || queued || applying || combo.IsDropDownOpen) return;
        applying = true;
        try
        {
            var current = displays();
            Error = null;
            combo.IsEnabled = true;
            var preferred = controller.Preferences.TargetDisplayId;
            var missing = preferred is not null && current.All(d => d.Id != preferred);
            var nextKey = string.Join("|", current.Select(d => $"{d.Id}:{d.IsPrimary}")) + ":" + (missing ? preferred : null);
            if (key != nextKey || combo.Items.Count == 0)
            {
                combo.Items.Clear();
                combo.Items.Add(new ComboBoxItem { Content = "主显示器（自动）", Tag = null });
                foreach (var display in current) combo.Items.Add(new ComboBoxItem { Content = display.Id + (display.IsPrimary ? "（主显示器）" : ""), Tag = display.Id });
                if (missing) combo.Items.Add(new ComboBoxItem { Content = preferred + "（暂时不可用）", Tag = preferred });
                key = nextKey;
            }
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(item => Equals(item.Tag, preferred));
            gap.Value = controller.Preferences.RightGapDip;
        }
        catch (Exception error)
        {
            Error = "显示器列表暂时不可用，保留原选择：" + error.Message;
            combo.IsEnabled = false;
        }
        finally { applying = false; }
    }
    public void SelectionChanged()
    {
        if (applying || stopped || combo.SelectedItem is not ComboBoxItem selected) return;
        Queue();
        controller.Execute(() => controller.SetDisplay(selected.Tag as string));
    }
    public void GapChanged(double value)
    {
        if (applying || stopped) return;
        Queue();
        controller.Execute(() =>
        {
            if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 0 || value > 64) throw new ArgumentException("间距必须是 0 到 64 的整数。");
            controller.SetGap((int)value);
        });
    }
    private void OnClosed(object? sender, object args) => Queue();
    private void Queue()
    {
        if (queued || stopped) return;
        queued = true;
        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => { queued = false; if (!stopped) controller.Execute(Refresh); })) queued = false;
    }
    public void Stop() { stopped = true; combo.DropDownClosed -= OnClosed; }
}

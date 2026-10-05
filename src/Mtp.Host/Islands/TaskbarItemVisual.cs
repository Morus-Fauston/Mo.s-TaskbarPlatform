using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mtp.Contracts;
using Mtp.Platform.Core;

namespace Mtp.Host.Islands;

/// <summary>Sibling input regions prevent internal controls from invoking the item's blank activation.</summary>
internal sealed class TaskbarItemVisual : IDisposable
{
    public Grid Root { get; } = new();
    public RectangleGeometry Clip { get; } = new();
    private readonly Button blank = NewButton();
    private readonly Button primary = NewButton();
    private readonly Button secondary = NewButton();
    private readonly Grid content = new();
    private readonly SymbolIcon primaryIcon = new(Symbol.Play);
    private readonly Ellipse ring = new() { Width = 26, Height = 26, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly ProgressRing progress = new() { Width = 26, Height = 26, IsIndeterminate = false, Minimum = 0, Maximum = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock label = new()
    {
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        IsHitTestVisible = false
    };
    private readonly Func<ItemInteractionHandle, string?, Task> activate;
    private HostItemPresentation? model;
    private bool disposed;
    private bool interactive;
    private bool timerMode;
    private readonly HashSet<string> pending = [];
    private long inputGeneration;
    private static Button NewButton() => new()
    {
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };

    public TaskbarItemVisual(TaskbarItemKey key, Func<ItemInteractionHandle, string?, Task> activate)
    {
        this.activate = activate;
        Root.Clip = Clip;
        Root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(primary, 1); Grid.SetColumn(secondary, 2);
        primary.Width = secondary.Width = 32;
        primary.Content = primaryIcon;
        secondary.Content = new SymbolIcon(Symbol.Switch);
        content.Children.Add(ring); content.Children.Add(progress); content.Children.Add(label);
        blank.Content = content;
        Root.Children.Add(blank); Root.Children.Add(primary); Root.Children.Add(secondary);
        string id = "mtp-item/" + string.Join("/", Uri.EscapeDataString(key.Component.ApplicationId),
            Uri.EscapeDataString(key.Component.FeatureGroupId), Uri.EscapeDataString(key.Component.ComponentId),
            Uri.EscapeDataString(key.ItemId), key.PresenceGeneration.ToString(CultureInfo.InvariantCulture));
        AutomationProperties.SetAutomationId(blank, id);
        AutomationProperties.SetAutomationId(primary, id + "/PrimaryButton");
        AutomationProperties.SetAutomationId(secondary, id + "/SecondaryButton");
        blank.Click += ActivateBlank; primary.Click += ActivatePrimary; secondary.Click += ActivateSecondary;
        ring.SetBinding(Shape.StrokeProperty, new Binding { Source = blank, Path = new PropertyPath(nameof(Control.Foreground)), Mode = BindingMode.OneWay });
        label.SetBinding(TextBlock.ForegroundProperty, new Binding { Source = blank, Path = new PropertyPath(nameof(Control.Foreground)), Mode = BindingMode.OneWay });
    }

    public void Update(HostItemPresentation value)
    {
        if (model?.SessionId != value.SessionId) { inputGeneration++; pending.Clear(); }
        model = value;
        bool timer = value.Presentation.Fields.HasFlag(ContentFields.Timer);
        if (timerMode != timer)
        {
            timerMode = timer;
            content.ColumnDefinitions.Clear();
            if (timer)
            {
                content.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
                content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            }
            Grid.SetColumn(label, timer ? 1 : 0);
            label.FontSize = timer ? 10 : 12;
        }
        primary.Visibility = value.Expanded && Binding(ItemControlKind.PrimaryButton) is not null ? Visibility.Visible : Visibility.Collapsed;
        secondary.Visibility = value.Expanded && Binding(ItemControlKind.SecondaryButton) is not null ? Visibility.Visible : Visibility.Collapsed;
        NameControl(primary, ItemControlKind.PrimaryButton);
        NameControl(secondary, ItemControlKind.SecondaryButton);
        if (!timer)
        {
            progress.IsActive = false; progress.Visibility = Visibility.Collapsed;
            ring.Visibility = value.Presentation.Template == PresetTemplate.Counter ? Visibility.Visible : Visibility.Collapsed;
            label.Text = value.Presentation.Template switch
            {
                PresetTemplate.Counter => value.Item.Fields.Counter?.Value.ToString(CultureInfo.InvariantCulture) ?? "—",
                PresetTemplate.Status => value.Item.Fields.Status?.Text ?? "—",
                _ => "—"
            };
            SetDescription(label.Text);
        }
        SetInteractive(interactive);
    }

    public void UpdateTimer(TimerDisplayReading value)
    {
        if (disposed || !timerMode || model is null) return;
        label.Text = value.Text;
        ring.Visibility = value.ProgressFraction.HasValue ? Visibility.Collapsed : Visibility.Visible;
        progress.Visibility = value.ProgressFraction.HasValue ? Visibility.Visible : Visibility.Collapsed;
        progress.IsActive = value.ProgressFraction.HasValue;
        progress.Value = value.ProgressFraction ?? 0;
        primaryIcon.Symbol = value.IsPaused ? Symbol.Play : Symbol.Pause;
        SetDescription(value.Text + (value.IsPaused ? " · 已暂停" : ""));
    }

    private ItemActivationBinding? Binding(ItemControlKind kind) => model?.Structure.ControlActivations?.FirstOrDefault(value => value.Control == kind)?.Binding;
    private void NameControl(Button button, ItemControlKind kind)
    {
        string name = Binding(kind)?.Kind switch
        {
            ItemActivationKind.ToggleSelf => "展开或收起此项",
            ItemActivationKind.ToggleDeclaredTargets => "展开或收起关联项",
            ItemActivationKind.TaskbarFlyout => "打开关联面板",
            ItemActivationKind.BusinessAction => "执行此项操作",
            _ => "无可用操作"
        };
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
    }
    private void SetDescription(string text)
    {
        AutomationProperties.SetName(blank, text); ToolTipService.SetToolTip(blank, text);
    }
    public void SetInteractive(bool value)
    {
        interactive = value && !disposed;
        Root.IsHitTestVisible = interactive;
        blank.IsHitTestVisible = primary.IsHitTestVisible = secondary.IsHitTestVisible = interactive;
        blank.IsEnabled = interactive && !pending.Contains("");
        primary.IsEnabled = interactive && !pending.Contains(nameof(ItemControlKind.PrimaryButton));
        secondary.IsEnabled = interactive && !pending.Contains(nameof(ItemControlKind.SecondaryButton));
        blank.IsTabStop = interactive;
        primary.IsTabStop = interactive && primary.Visibility == Visibility.Visible;
        secondary.IsTabStop = interactive && secondary.Visibility == Visibility.Visible;
    }
    private async void ActivateBlank(object sender, RoutedEventArgs args) => await InvokeAsync(null, blank);
    private async void ActivatePrimary(object sender, RoutedEventArgs args) => await InvokeAsync(nameof(ItemControlKind.PrimaryButton), primary);
    private async void ActivateSecondary(object sender, RoutedEventArgs args) => await InvokeAsync(nameof(ItemControlKind.SecondaryButton), secondary);
    private async Task InvokeAsync(string? control, Button button)
    {
        if (!interactive || disposed || !button.IsEnabled || button.Visibility != Visibility.Visible || model is not { IsInteractive: true } current) return;
        string key = control ?? "";
        if (!pending.Add(key)) return;
        long generation = inputGeneration;
        SetInteractive(interactive);
        try { await activate(current.Handle, control); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Item action failed: {0}", error);
            if (!disposed && generation == inputGeneration) AutomationProperties.SetHelpText(button, "项操作失败，请重试");
        }
        finally
        {
            if (!disposed && generation == inputGeneration) { pending.Remove(key); SetInteractive(interactive); }
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; inputGeneration++; pending.Clear(); SetInteractive(false);
        blank.Click -= ActivateBlank; primary.Click -= ActivatePrimary; secondary.Click -= ActivateSecondary;
        ring.ClearValue(Shape.StrokeProperty); label.ClearValue(TextBlock.ForegroundProperty);
        progress.IsActive = false; model = null;
        blank.Content = primary.Content = secondary.Content = null;
        content.Children.Clear(); Root.Children.Clear();
    }
}

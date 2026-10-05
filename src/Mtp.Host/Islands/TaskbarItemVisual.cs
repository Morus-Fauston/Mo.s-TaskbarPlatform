using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mtp.Contracts;
using Mtp.Platform.Core;
using Windows.Foundation;

namespace Mtp.Host.Islands;

internal sealed record ItemActivationTrigger(Rect Bounds, bool Keyboard);

/// <summary>Host-owned preset fields and sibling input regions; all motion is supplied by one external driver.</summary>
internal sealed class TaskbarItemVisual : IDisposable
{
    public Grid Root { get; } = new();
    public RectangleGeometry Clip { get; } = new();
    private readonly TaskbarItemKey key;
    private readonly Button blank = NewButton(), primary = NewButton(), secondary = NewButton();
    private readonly Grid content = new(), graph = new(), textGrid = new(), textViewport = new(), metrics = new();
    private readonly RectangleGeometry textClip = new();
    private readonly TextBlock label = Label(), countLabel = Label(), timerLabel = Label(), progressLabel = Label();
    private readonly TextBlock measure = Label();
    private readonly TranslateTransform textTranslation = new(), busyTranslation = new();
    private readonly SymbolIcon primaryIcon = new(Symbol.Play);
    private readonly FontIcon statusIcon = new() { FontSize = 11, IsHitTestVisible = false };
    private readonly Ellipse ring = new() { Width = 24, Height = 24, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Ellipse busyDot = new() { Width = 4, Height = 4, IsHitTestVisible = false };
    private readonly Rectangle barTrack = new() { Width = 24, Height = 3, Opacity = 0.25, IsHitTestVisible = false };
    private readonly Rectangle barFill = new() { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly Microsoft.UI.Xaml.Shapes.Path arc = new() { StrokeThickness = 2, IsHitTestVisible = false };
    private readonly PathFigure arcFigure = new() { StartPoint = new Point(13, 1), IsClosed = false };
    private readonly ArcSegment arcSegment = new() { Size = new Size(12, 12), SweepDirection = SweepDirection.Clockwise };
    private readonly Func<ItemInteractionHandle, string?, Task> activate;
    private readonly HashSet<string> pending = [];
    private HostItemPresentation? model;
    private TimerDisplayReading? timerReading;
    private bool disposed, interactive, composite, graphVisible, barMode, neutralCounter;
    private long inputGeneration;

    private static TextBlock Label() => new()
    {
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        IsHitTestVisible = false
    };
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
        this.key = key; this.activate = activate;
        Root.Clip = Clip;
        Root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(primary, 1); Grid.SetColumn(secondary, 2);
        primary.Width = secondary.Width = 32;
        primary.Content = primaryIcon; secondary.Content = new SymbolIcon(Symbol.Switch);
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        graph.Width = 28; graph.Height = 28;
        arcFigure.Segments.Add(arcSegment);
        var geometry = new PathGeometry(); geometry.Figures.Add(arcFigure); arc.Data = geometry;
        graph.Children.Add(ring); graph.Children.Add(arc); graph.Children.Add(barTrack); graph.Children.Add(barFill); graph.Children.Add(busyDot);
        arc.Margin = new Thickness(1); barFill.Margin = new Thickness(2, 0, 0, 0);
        busyDot.RenderTransform = busyTranslation;
        Grid.SetColumn(textGrid, 1);
        textViewport.Clip = textClip;
        label.RenderTransform = textTranslation;
        textViewport.Children.Add(label);
        content.Children.Add(graph); content.Children.Add(textGrid);
        blank.Content = content;
        Root.Children.Add(blank); Root.Children.Add(primary); Root.Children.Add(secondary);
        string id = "mtp-item/" + string.Join("/", Uri.EscapeDataString(key.Component.ApplicationId), Uri.EscapeDataString(key.Component.FeatureGroupId),
            Uri.EscapeDataString(key.Component.ComponentId), Uri.EscapeDataString(key.ItemId), key.PresenceGeneration.ToString(CultureInfo.InvariantCulture));
        AutomationProperties.SetAutomationId(blank, id);
        AutomationProperties.SetAutomationId(primary, id + "/PrimaryButton"); AutomationProperties.SetAutomationId(secondary, id + "/SecondaryButton");
        AutomationProperties.SetAutomationId(countLabel, id + "/Counter"); AutomationProperties.SetAutomationId(timerLabel, id + "/Timer");
        AutomationProperties.SetAutomationId(label, id + "/Text"); AutomationProperties.SetAutomationId(progressLabel, id + "/ProgressText");
        AutomationProperties.SetAutomationId(arc, id + "/Ring"); AutomationProperties.SetAutomationId(barFill, id + "/Bar");
        AutomationProperties.SetAutomationId(statusIcon, id + "/StatusMarker"); AutomationProperties.SetAutomationId(busyDot, id + "/Busy");
        blank.Click += ActivateBlank; primary.Click += ActivatePrimary; secondary.Click += ActivateSecondary;
        foreach (var text in new[] { label, countLabel, timerLabel, progressLabel })
            text.SetBinding(TextBlock.ForegroundProperty, ForegroundBinding());
        ring.SetBinding(Shape.StrokeProperty, ForegroundBinding());
        barTrack.SetBinding(Shape.FillProperty, ForegroundBinding());
        busyDot.SetBinding(Shape.FillProperty, ForegroundBinding());
        statusIcon.SetBinding(IconElement.ForegroundProperty, ForegroundBinding());
        var accent = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
        arc.Stroke = barFill.Fill = accent;
    }

    private Binding ForegroundBinding() => new() { Source = blank, Path = new PropertyPath(nameof(Control.Foreground)), Mode = BindingMode.OneWay };

    public void Update(HostItemPresentation value)
    {
        if (model?.SessionId != value.SessionId) { inputGeneration++; pending.Clear(); activationTriggers.Clear(); timerReading = null; }
        bool rebuild = model?.Presentation != value.Presentation;
        model = value;
        composite = value.Presentation.Template == PresetTemplate.Composite;
        neutralCounter = value.Presentation.Template == PresetTemplate.Counter && value.Presentation.Variant == PresetVariant.Default;
        graphVisible = value.Presentation.Variant != PresetVariant.Text && (value.Presentation.Fields.HasFlag(ContentFields.Progress) ||
            value.Presentation.Template is PresetTemplate.Timer or PresetTemplate.Counter);
        barMode = value.Presentation.Variant == PresetVariant.Bar;
        graph.Visibility = graphVisible ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(graph, neutralCounter ? 2 : 1);
        Grid.SetColumn(textGrid, neutralCounter ? 0 : 1); Grid.SetColumnSpan(textGrid, neutralCounter ? 2 : 1);
        if (rebuild) BuildTextLayout();
        primary.Visibility = value.Expanded && Binding(ItemControlKind.PrimaryButton) is not null ? Visibility.Visible : Visibility.Collapsed;
        secondary.Visibility = value.Expanded && Binding(ItemControlKind.SecondaryButton) is not null ? Visibility.Visible : Visibility.Collapsed;
        NameControl(primary, ItemControlKind.PrimaryButton); NameControl(secondary, ItemControlKind.SecondaryButton);
        var fields = value.Item.Fields;
        countLabel.Text = fields.Counter?.Value.ToString(CultureInfo.InvariantCulture) ?? "";
        timerLabel.Text = timerReading?.Text ?? "00:00";
        progressLabel.Text = ProgressText(fields.Progress);
        label.Text = fields.Status?.Text ?? (value.Presentation.Template == PresetTemplate.Counter ? countLabel.Text :
            value.Presentation.Template == PresetTemplate.Timer ? timerLabel.Text : progressLabel.Text);
        statusIcon.Glyph = fields.Status?.Marker switch { StatusMarker.Attention => "\uE7BA", StatusMarker.Error => "\uEA39", _ => "\uE73E" };
        statusIcon.Visibility = fields.Status is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(statusIcon, Marker(fields.Status?.Marker ?? StatusMarker.Normal));
        UpdateDescription(); SetInteractive(interactive);
        // A newly created or replaced neutral counter must not retain any previous progress geometry.
        if (!value.Presentation.Fields.HasFlag(ContentFields.Progress) && value.Presentation.Template != PresetTemplate.Timer) ApplyGraph(null, false, 0);
    }

    private void BuildTextLayout()
    {
        textGrid.Children.Clear(); textGrid.ColumnDefinitions.Clear(); textGrid.RowDefinitions.Clear();
        metrics.Children.Clear(); metrics.ColumnDefinitions.Clear();
        textGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        textGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(statusIcon, 0); Grid.SetColumn(textViewport, 1);
        if (composite)
        {
            textGrid.RowDefinitions.Add(new() { Height = new GridLength(16) });
            textGrid.RowDefinitions.Add(new() { Height = new GridLength(16) });
            foreach (var text in new[] { countLabel, timerLabel, progressLabel }.Where(text => text == countLabel
                ? model!.Presentation.Fields.HasFlag(ContentFields.Counter) : text == timerLabel
                    ? model!.Presentation.Fields.HasFlag(ContentFields.Timer) : model!.Presentation.Fields.HasFlag(ContentFields.Progress)))
            {
                metrics.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(text, metrics.Children.Count); metrics.Children.Add(text);
            }
            Grid.SetColumnSpan(metrics, 2); textGrid.Children.Add(metrics);
            Grid.SetRow(statusIcon, 1); Grid.SetRow(textViewport, 1);
        }
        else { Grid.SetRow(statusIcon, 0); Grid.SetRow(textViewport, 0); }
        textGrid.Children.Add(statusIcon); textGrid.Children.Add(textViewport);
    }

    public void UpdateTimer(TimerDisplayReading value)
    {
        if (disposed || model is null || !model.Presentation.Fields.HasFlag(ContentFields.Timer)) return;
        timerReading = value; timerLabel.Text = value.Text;
        if (!composite) label.Text = value.Text;
        primaryIcon.Symbol = value.IsPaused ? Symbol.Play : Symbol.Pause;
        if (model.Presentation.Template == PresetTemplate.Timer) ApplyGraph(value.ProgressFraction, false, 0);
        UpdateDescription();
    }

    public void UpdatePreset(PresetMotionReading value, bool reduced)
    {
        if (disposed || model is null) return;
        if (model.Presentation.Template != PresetTemplate.Timer) ApplyGraph(value.ProgressFraction, value.IsIndeterminate, value.BusyPhase);
        bool scroll = model.Structure.Overflow == TextOverflow.Scroll && !reduced && GetMeasurement().TextWidthDip > GetMeasurement().ViewportWidthDip;
        label.TextTrimming = scroll ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        label.Width = scroll ? GetMeasurement().TextWidthDip : double.NaN;
        label.HorizontalAlignment = neutralCounter ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        textTranslation.X = scroll ? value.TextOffsetDip : 0;
        double height = composite ? 16 : Math.Max(1, Root.Height);
        textClip.Rect = new Rect(0, 0, GetMeasurement().ViewportWidthDip, height);
    }

    private void ApplyGraph(double? fraction, bool busy, double phase)
    {
        ring.Visibility = graphVisible && !barMode ? Visibility.Visible : Visibility.Collapsed;
        barTrack.Visibility = graphVisible && barMode ? Visibility.Visible : Visibility.Collapsed;
        arc.Visibility = graphVisible && !barMode && fraction.HasValue && fraction > 0 ? Visibility.Visible : Visibility.Collapsed;
        barFill.Visibility = graphVisible && barMode && fraction.HasValue ? Visibility.Visible : Visibility.Collapsed;
        busyDot.Visibility = graphVisible && busy ? Visibility.Visible : Visibility.Collapsed;
        var amount = Math.Clamp(fraction ?? 0, 0, 1);
        double angle = Math.Min(amount, 0.999999) * Math.PI * 2;
        arcSegment.Point = new Point(13 + 12 * Math.Sin(angle), 13 - 12 * Math.Cos(angle)); arcSegment.IsLargeArc = amount > 0.5;
        barFill.Width = 24 * amount;
        busyTranslation.X = barMode ? -10 + 20 * phase : 10 * Math.Sin(phase * Math.PI * 2);
        busyTranslation.Y = barMode ? 0 : -10 * Math.Cos(phase * Math.PI * 2);
        AutomationProperties.SetName(busyDot, "处理中");
        AutomationProperties.SetName(arc, fraction.HasValue ? FormattableString.Invariant($"进度 {amount:P0}") : "无确定进度");
        AutomationProperties.SetName(barFill, AutomationProperties.GetName(arc));
    }

    public PresetTextMeasurement GetMeasurement()
    {
        measure.Text = label.Text; measure.FontSize = label.FontSize; measure.FontFamily = label.FontFamily;
        measure.Measure(new Size(double.PositiveInfinity, 32));
        double available = textViewport.ActualWidth;
        if (available <= 0) available = Math.Max(0, Root.Width - (graphVisible && !neutralCounter ? 28 : 0) -
            (primary.Visibility == Visibility.Visible ? 32 : 0) - (secondary.Visibility == Visibility.Visible ? 32 : 0) -
            (statusIcon.Visibility == Visibility.Visible ? 12 : 0));
        return new(key, Math.Min(TaskbarGroupLayout.MaximumDimensionDip, measure.DesiredSize.Width), Math.Max(0, available));
    }

    private readonly Dictionary<string, ItemActivationTrigger> activationTriggers = [];
    public ItemActivationTrigger? GetTrigger(ItemInteractionHandle handle, string? control)
    {
        if (disposed || !interactive || model?.Handle != handle) return null;
        if (pending.Contains(control ?? "") && activationTriggers.TryGetValue(control ?? "", out var trigger)) return trigger;
        var button = control switch { null => blank, nameof(ItemControlKind.PrimaryButton) => primary, nameof(ItemControlKind.SecondaryButton) => secondary, _ => null };
        if (button is null || !button.IsEnabled || button.Visibility != Visibility.Visible || button.ActualWidth <= 0 || button.ActualHeight <= 0) return null;
        return new(button.TransformToVisual(null).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight)), button.FocusState == FocusState.Keyboard);
    }

    private static string ProgressText(ProgressReading? progress) => progress switch
    {
        { Mode: ProgressMode.Indeterminate } => "处理中",
        { Value: { } value, Maximum: { } maximum } => FormattableString.Invariant($"{value / maximum:P0}"),
        _ => ""
    };
    private static string Marker(StatusMarker marker) => marker switch { StatusMarker.Attention => "注意", StatusMarker.Error => "错误", _ => "正常" };
    private void UpdateDescription()
    {
        if (model is null) return;
        var fields = model.Item.Fields; var parts = new List<string>(4);
        if (model.Presentation.Fields.HasFlag(ContentFields.Counter) && fields.Counter is { } counter)
            parts.Add((counter.Semantics == CounterSemantics.CurrentIndex ? $"第{counter.Value}项" : $"已完成{counter.Value}个") +
                (counter.Total is { } total ? $"，共{total}项" : ""));
        if (model.Presentation.Fields.HasFlag(ContentFields.Timer)) parts.Add((timerReading?.Text ?? "00:00") + (timerReading?.IsPaused == true ? " · 已暂停" : ""));
        if (model.Presentation.Fields.HasFlag(ContentFields.Progress)) parts.Add("进度 " + ProgressText(fields.Progress));
        if (model.Presentation.Fields.HasFlag(ContentFields.Status) && fields.Status is { } status) parts.Add(Marker(status.Marker) + "：" + status.Text);
        string text = string.Join(" · ", parts); AutomationProperties.SetName(blank, text); ToolTipService.SetToolTip(blank, text);
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
    public void SetInteractive(bool value)
    {
        interactive = value && !disposed; Root.IsHitTestVisible = interactive;
        blank.IsHitTestVisible = primary.IsHitTestVisible = secondary.IsHitTestVisible = interactive;
        blank.IsEnabled = interactive && !pending.Contains(""); primary.IsEnabled = interactive && !pending.Contains(nameof(ItemControlKind.PrimaryButton));
        secondary.IsEnabled = interactive && !pending.Contains(nameof(ItemControlKind.SecondaryButton)); blank.IsTabStop = interactive;
        primary.IsTabStop = interactive && primary.Visibility == Visibility.Visible; secondary.IsTabStop = interactive && secondary.Visibility == Visibility.Visible;
    }
    private async void ActivateBlank(object sender, RoutedEventArgs args) => await InvokeAsync(null, blank);
    private async void ActivatePrimary(object sender, RoutedEventArgs args) => await InvokeAsync(nameof(ItemControlKind.PrimaryButton), primary);
    private async void ActivateSecondary(object sender, RoutedEventArgs args) => await InvokeAsync(nameof(ItemControlKind.SecondaryButton), secondary);
    private async Task InvokeAsync(string? control, Button button)
    {
        if (!interactive || disposed || !button.IsEnabled || button.Visibility != Visibility.Visible || model is not { IsInteractive: true } current) return;
        var trigger = GetTrigger(current.Handle, control);
        string token = control ?? ""; if (!pending.Add(token)) return;
        if (trigger is not null) activationTriggers[token] = trigger;
        long generation = inputGeneration; SetInteractive(interactive);
        try { await activate(current.Handle, control); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Item action failed: {0}", error);
            if (!disposed && generation == inputGeneration) AutomationProperties.SetHelpText(button, "项操作失败，请重试");
        }
        finally { if (!disposed && generation == inputGeneration) { pending.Remove(token); activationTriggers.Remove(token); SetInteractive(interactive); } }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; inputGeneration++; pending.Clear(); activationTriggers.Clear(); SetInteractive(false);
        blank.Click -= ActivateBlank; primary.Click -= ActivatePrimary; secondary.Click -= ActivateSecondary;
        foreach (var text in new[] { label, countLabel, timerLabel, progressLabel }) text.ClearValue(TextBlock.ForegroundProperty);
        ring.ClearValue(Shape.StrokeProperty); barTrack.ClearValue(Shape.FillProperty); busyDot.ClearValue(Shape.FillProperty); statusIcon.ClearValue(IconElement.ForegroundProperty);
        model = null; timerReading = null; blank.Content = primary.Content = secondary.Content = null;
        textGrid.Children.Clear(); textViewport.Children.Clear(); graph.Children.Clear(); content.Children.Clear(); Root.Children.Clear();
    }
}

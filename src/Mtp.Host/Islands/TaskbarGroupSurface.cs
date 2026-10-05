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

/// <summary>Projects one externally sampled frame into native controls; owns neither a clock nor a window.</summary>
internal sealed class TaskbarGroupSurface : UserControl, IDisposable
{
    private readonly Canvas canvas = new();
    private readonly RectangleGeometry clip = new();
    private readonly Func<HostComponentDisplayModel, Templates.TemplateRenderer?> createTemplate;
    private readonly Func<ActionSlotReference, Task> invokeAction;
    private readonly Func<ItemInteractionHandle, string?, Task> activate;
    private readonly Dictionary<TaskbarComponentKey, ComponentVisual> components = [];
    private readonly Dictionary<TaskbarItemKey, TaskbarItemVisual> items = [];
    private HostGroupPresentationSnapshot? previousSnapshot;
    private long generation = -1;
    private long revision = -1;
    private bool disposed;

    public TaskbarGroupSurface(Func<HostComponentDisplayModel, Templates.TemplateRenderer?> createTemplate,
        Func<ActionSlotReference, Task> invokeAction, Func<ItemInteractionHandle, string?, Task> activate)
    {
        this.createTemplate = createTemplate ?? throw new ArgumentNullException(nameof(createTemplate));
        this.invokeAction = invokeAction ?? throw new ArgumentNullException(nameof(invokeAction));
        this.activate = activate ?? throw new ArgumentNullException(nameof(activate));
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        canvas.HorizontalAlignment = HorizontalAlignment.Left;
        canvas.VerticalAlignment = VerticalAlignment.Top;
        Content = canvas;
        Clip = clip;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetAutomationId(this, "MtpTaskbarGroupSurface");
    }

    public void Apply(HostGroupPresentationSnapshot snapshot, TaskbarGroupAnimationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(frame);
        if (disposed || frame.Generation < generation || (frame.Generation == generation && frame.Revision < revision)) return;
        if (frame.Items.Count > TaskbarGroupLayout.MaximumItems + TaskbarGroupAnimation.MaximumRetiredItems ||
            frame.Components.Count > TaskbarGroupLayout.MaximumComponents + TaskbarGroupAnimation.MaximumRetiredItems)
            throw new ArgumentOutOfRangeException(nameof(frame), "The frame exceeds the Host visual budget.");
        if (frame.Generation != generation) ClearVisuals();
        generation = frame.Generation;
        revision = frame.Revision;
        bool updateFields = !ReferenceEquals(previousSnapshot, snapshot);
        Width = canvas.Width = frame.WidthDip;
        Height = canvas.Height = frame.HeightDip;
        clip.Rect = new Rect(0, 0, frame.WidthDip, frame.HeightDip);

        var dynamicComponents = snapshot.ItemsByKey.Keys.Select(key => key.Component).ToHashSet();
        var componentKeys = new HashSet<TaskbarComponentKey>();
        foreach (var placement in frame.Components)
        {
            // A retired item's ancestor is present in the frame but has no current component model.
            if (dynamicComponents.Contains(placement.Key) ||
                !snapshot.ComponentsByKey.TryGetValue(placement.Key, out var model)) continue;
            componentKeys.Add(placement.Key);
            if (!components.TryGetValue(placement.Key, out var visual))
            {
                visual = new ComponentVisual(placement.Key, createTemplate, invokeAction);
                components.Add(placement.Key, visual);
                canvas.Children.Add(visual.Root);
                visual.Update(model);
            }
            else if (updateFields) visual.Update(model);
            SetBounds(visual.Root, visual.Clip, placement.Bounds, frame.WidthDip, 1);
            visual.Root.IsHitTestVisible = placement.Bounds.Width > 0 && placement.Bounds.Height > 0;
            visual.Root.Visibility = visual.Root.IsHitTestVisible ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var key in components.Keys.Where(key => !componentKeys.Contains(key)).ToArray())
        {
            var visual = components[key];
            visual.Dispose();
            canvas.Children.Remove(visual.Root);
            components.Remove(key);
        }

        var itemKeys = new HashSet<TaskbarItemKey>();
        foreach (var placement in frame.Items)
        {
            itemKeys.Add(placement.Key);
            bool current = snapshot.ItemsByKey.TryGetValue(placement.Key, out var model);
            if (!items.TryGetValue(placement.Key, out var visual))
            {
                // Never reconstruct an old session's visual from a newer declaration.
                if (!current || placement.IsExiting) continue;
                visual = new TaskbarItemVisual(placement.Key, activate);
                items.Add(placement.Key, visual);
                canvas.Children.Add(visual.Root);
                visual.Update(model!);
            }
            else if (current && updateFields) visual.Update(model!);
            SetBounds(visual.Root, visual.Clip, placement.Bounds, frame.WidthDip, placement.Opacity);
            visual.SetInteractive(current && model!.IsInteractive && placement.IsInteractive && !placement.IsExiting &&
                placement.Bounds.Width > 0 && placement.Bounds.Height > 0 && placement.Opacity > 0);
        }
        foreach (var key in items.Keys.Where(key => !itemKeys.Contains(key)).ToArray())
        {
            var visual = items[key];
            visual.Dispose();
            canvas.Children.Remove(visual.Root);
            items.Remove(key);
        }
        previousSnapshot = snapshot;
    }

    private static void SetBounds(FrameworkElement element, RectangleGeometry geometry, TaskbarDipRect bounds,
        double groupWidth, double opacity)
    {
        Canvas.SetLeft(element, bounds.X + groupWidth);
        Canvas.SetTop(element, bounds.Y);
        element.Width = bounds.Width;
        element.Height = bounds.Height;
        element.Opacity = opacity;
        geometry.Rect = new Rect(0, 0, bounds.Width, bounds.Height);
    }

    private static string ComponentId(TaskbarComponentKey key) => string.Join("/",
        Uri.EscapeDataString(key.ApplicationId), Uri.EscapeDataString(key.FeatureGroupId), Uri.EscapeDataString(key.ComponentId));

    private void ClearVisuals()
    {
        foreach (var visual in components.Values) visual.Dispose();
        foreach (var visual in items.Values) visual.Dispose();
        components.Clear();
        items.Clear();
        canvas.Children.Clear();
        previousSnapshot = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearVisuals();
    }

    private sealed class ComponentVisual : IDisposable
    {
        public Grid Root { get; } = new();
        public RectangleGeometry Clip { get; } = new();
        private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button action = new() { Content = "执行", MinWidth = 0, MinHeight = 0, Padding = new Thickness(8, 0, 8, 0) };
        private readonly Func<HostComponentDisplayModel, Templates.TemplateRenderer?> createTemplate;
        private readonly Func<ActionSlotReference, Task> invokeAction;
        private Templates.TemplateRenderer? template;
        private HostComponentDisplayModel? model;
        private bool busy;
        private bool disposed;

        public ComponentVisual(TaskbarComponentKey key, Func<HostComponentDisplayModel, Templates.TemplateRenderer?> createTemplate,
            Func<ActionSlotReference, Task> invokeAction)
        {
            this.createTemplate = createTemplate;
            this.invokeAction = invokeAction;
            Root.Clip = Clip;
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnSpacing = 4;
            Grid.SetColumn(action, 1);
            Root.Children.Add(label);
            Root.Children.Add(action);
            AutomationProperties.SetAutomationId(Root, "mtp-component/" + ComponentId(key));
            AutomationProperties.SetAutomationId(action, "mtp-action/" + ComponentId(key));
            action.Click += Invoke;
        }

        public void Update(HostComponentDisplayModel value)
        {
            model = value;
            if (!value.HasTemplate && template is not null)
            {
                Root.Children.Remove(template);
                template.Dispose();
                template = null;
            }
            if (value.HasTemplate && template is null)
            {
                template = createTemplate(value);
                if (template is not null)
                {
                    Grid.SetColumnSpan(template, 2);
                    Root.Children.Add(template);
                }
            }
            template?.Refresh();
            label.Text = value.Text;
            ToolTipService.SetToolTip(Root, value.Text + " · " + value.StatusLabel);
            AutomationProperties.SetName(Root, value.Text + " · " + value.StatusLabel);
            label.Visibility = template is null ? Visibility.Visible : Visibility.Collapsed;
            // A declared template owns its actions even while its renderer is unavailable.
            action.Visibility = !value.HasTemplate && value.Action is not null ? Visibility.Visible : Visibility.Collapsed;
            action.IsEnabled = !disposed && !busy && value.CanInvokeAction && !value.ActionBusy;
        }

        private async void Invoke(object sender, RoutedEventArgs args)
        {
            if (disposed || busy || model is not { CanInvokeAction: true, ActionBusy: false, Action: { } slot }) return;
            busy = true;
            action.IsEnabled = false;
            try { await invokeAction(slot); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Taskbar component action failed: {0}", error);
                if (!disposed) AutomationProperties.SetHelpText(action, "组件动作执行失败");
            }
            finally
            {
                busy = false;
                if (!disposed && model is { } current) action.IsEnabled = current.CanInvokeAction && !current.ActionBusy;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Root.IsHitTestVisible = false;
            action.IsEnabled = false;
            action.Click -= Invoke;
            template?.Dispose();
            template = null;
            model = null;
            Root.Children.Clear();
        }
    }

    public void ApplyTimerReadings(IReadOnlyList<TimerDisplayReading> readings)
    {
        if (disposed) return;
        foreach (var reading in readings)
            if (items.TryGetValue(reading.Key, out var visual)) visual.UpdateTimer(reading);
    }

    public IReadOnlyList<PresetTextMeasurement> GetPresetMeasurements() => disposed || previousSnapshot is null ? [] :
        Array.AsReadOnly(previousSnapshot.ItemsByKey.Keys.Where(items.ContainsKey).Select(key => items[key].GetMeasurement()).ToArray());

    public void ApplyPresetReadings(IReadOnlyList<PresetMotionReading> readings, bool reducedMotion)
    {
        if (disposed) return;
        foreach (var reading in readings)
            if (items.TryGetValue(reading.Key, out var visual)) visual.UpdatePreset(reading, reducedMotion);
    }

    public ItemActivationTrigger? GetItemTrigger(ItemInteractionHandle handle, string? control)
    {
        if (disposed || previousSnapshot is null) return null;
        var pair = previousSnapshot.ItemsByKey.FirstOrDefault(pair => pair.Value.Handle == handle);
        return pair.Key is not null && items.TryGetValue(pair.Key, out var visual) ? visual.GetTrigger(handle, control) : null;
    }
}

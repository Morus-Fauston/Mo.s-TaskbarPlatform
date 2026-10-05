using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Windows.Foundation;

namespace Mtp.Host.Templates;

internal enum TemplateRenderSurface { Full, HintForeground, HintBackground }
internal sealed record TemplateInteractiveRegion(string NodeId, Control Control, Rect Bounds);

/// <summary>Renders only validated Host primitives. Field updates preserve native controls and their focus.</summary>
internal sealed class TemplateRenderer : UserControl, IDisposable
{
    private readonly TemplateInteractionController controller;
    private readonly RegisteredImageCache images;
    private readonly Dictionary<string, NodeView> views = new(StringComparer.Ordinal);
    private readonly List<Action> detach = [];
    private CancellationTokenSource generationLifetime = new();
    private long generation = -1;
    private long epoch;
    private bool refreshing;
    private bool disposed;
    private bool inputStopped;
    private string applicationId = "";
    private readonly TemplateRenderSurface surface;
    internal event Action? PresentationChanged;
    internal event Action? InteractiveGeometryChanged;
    internal TemplateSurfaceSnapshot? CapturedSnapshot { get; private set; }

    public TemplateRenderer(TemplateInteractionController controller, RegisteredImageCache images,
        TemplateRenderSurface surface = TemplateRenderSurface.Full)
    {
        this.controller = controller;
        this.images = images;
        this.surface = surface;
        VerticalContentAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(4, 0, 4, 0);
        Refresh();
    }

    public void Refresh()
    {
        if (disposed || inputStopped) return;
        // The passive layer never independently reads Broker state; its owner supplies
        // the exact snapshot already used by the foreground's unique controller.
        RefreshFromSnapshot(surface == TemplateRenderSurface.HintBackground ? CapturedSnapshot : controller.GetSnapshot());
    }

    internal void RefreshFromSnapshot(TemplateSurfaceSnapshot? snapshot)
    {
        if (disposed || inputStopped) return;
        if (snapshot is null) { ClearTree(); CapturedSnapshot = null; PresentationChanged?.Invoke(); return; }
        refreshing = true;
        try
        {
            var values = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
            if (snapshot.Generation != generation)
            {
                ClearTree();
                generation = snapshot.Generation;
                applicationId = snapshot.ApplicationId;
                Content = Build(snapshot.Template.Root, [], values);
            }
            foreach (var pair in views)
            {
                if (!values.TryGetValue(pair.Key, out var state)) continue;
                var view = pair.Value;
                bool visible = state.Visible && view.Ancestors.All(id => values[id].Visible);
                bool enabled = state.Enabled && view.Ancestors.All(id => values[id].Enabled);
                view.Element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                view.Element.IsHitTestVisible = enabled && surface != TemplateRenderSurface.HintBackground;
                if (view.Element is Control control) control.IsEnabled = enabled;
                var value = state.Preview ?? state.Confirmed;
                string? status = state.Error ?? (state.Busy ? "等待确认" : null);
                AutomationProperties.SetHelpText(view.Element, status ?? value?.Text ?? view.Node.AccessibleName ?? "");
                ToolTipService.SetToolTip(view.Element, status ?? view.Node.AccessibleName);
                switch (view.Element)
                {
                    case TextBlock text: text.Text = value?.Text ?? ""; AutomationProperties.SetName(text, view.Node.AccessibleName ?? text.Text); break;
                    case ToggleSwitch toggle: toggle.IsOn = value?.Boolean ?? false; break;
                    case Slider slider: slider.Value = value?.Number ?? slider.Minimum; break;
                    case Button button when view.Node.Kind == TemplateNodeKind.Button && value?.Kind != TemplateValueKind.Resource:
                        button.Content = value?.Text ?? view.Node.AccessibleName ?? ""; break;
                }
                if (view.Image is not null)
                {
                    string? resource = visible ? value?.ResourceId : null;
                    UpdateImage(view, resource);
                    if (view.ImageError is { } error)
                    {
                        ToolTipService.SetToolTip(view.Element, "图片不可用：" + error);
                        AutomationProperties.SetHelpText(view.Element, "图片不可用：" + error);
                    }
                }
            }
        }
        finally { refreshing = false; }
        CapturedSnapshot = snapshot;
        PresentationChanged?.Invoke();
    }

    internal IReadOnlyList<TemplateInteractiveRegion> GetInteractiveRegions()
    {
        if (disposed || inputStopped || surface == TemplateRenderSurface.HintBackground || !IsLoaded) return [];
        var result = new List<TemplateInteractiveRegion>();
        foreach (var view in views.Values)
        {
            if (view.Node.Action is null || view.Element is not Control { IsEnabled: true,
                IsHitTestVisible: true, Visibility: Visibility.Visible } control || !control.IsLoaded ||
                view.Ancestors.Any(id => views[id].Element.Visibility != Visibility.Visible ||
                    !views[id].Element.IsHitTestVisible)) continue;
            var bounds = control.TransformToVisual(this).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) ||
                !double.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0) continue;
            result.Add(new(view.Node.NodeId, control, bounds));
            if (result.Count >= TemplateLimits.NodesPerTemplate) break;
        }
        return result;
    }

    /// <summary>The sealed ToggleSwitch cannot suppress its peer. Its background counterpart is geometry only.</summary>
    internal void SynchronizeHintBackgroundLayout(TemplateRenderer input)
    {
        if (surface != TemplateRenderSurface.HintBackground || disposed) return;
        foreach (var pair in views)
        {
            if (pair.Value.Element is not TogglePlaceholder placeholder ||
                !input.views.TryGetValue(pair.Key, out var source) || source.Element is not ToggleSwitch toggle) continue;
            if (double.IsFinite(toggle.ActualWidth) && toggle.ActualWidth > 0 && placeholder.Width != toggle.ActualWidth)
                placeholder.Width = toggle.ActualWidth;
            if (double.IsFinite(toggle.ActualHeight) && toggle.ActualHeight > 0 && placeholder.Height != toggle.ActualHeight)
                placeholder.Height = toggle.ActualHeight;
        }
        UpdateLayout();
    }

    public bool IsCurrentNavigation(TemplateNavigationIntent intent) => !disposed && !inputStopped && controller.IsCurrentNavigation(intent);

    public FrameworkElement? GetNodeElement(string nodeId)
    {
        if (disposed || inputStopped) return null;
        Refresh();
        if (!views.TryGetValue(nodeId, out var view) || view.Element.Visibility != Visibility.Visible ||
            !view.Element.IsHitTestVisible || view.Element is Control { IsEnabled: false }) return null;
        return view.Element;
    }
    public bool FocusNode(string nodeId)
    {
        if (disposed || inputStopped || !views.TryGetValue(nodeId, out var view) || view.Element is not Control control ||
            !control.IsEnabled || control.Visibility != Visibility.Visible || !control.IsTabStop) return false;
        return control.Focus(FocusState.Keyboard);
    }
    public bool FocusFirst()
    {
        if (disposed || inputStopped) return false;
        foreach (var view in views.Values)
            if (view.Element is Control { IsEnabled: true, IsTabStop: true, Visibility: Visibility.Visible } control &&
                control.Focus(FocusState.Keyboard)) return true;
        return false;
    }

    private FrameworkElement Build(TemplateNode node, string[] ancestors, IReadOnlyDictionary<string, TemplateNodeSnapshot> values)
    {
        FrameworkElement element;
        Image? image = null;
        SymbolIcon? placeholder = null;
        long inputGeneration = generation;
        long inputEpoch = epoch;
        if (node.Kind == TemplateNodeKind.Text)
            element = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        else if (node.Kind == TemplateNodeKind.Image)
            element = ImageSurface(out image, out placeholder);
        else if (node.Kind is TemplateNodeKind.Button or TemplateNodeKind.IconButton)
        {
            Button button = surface == TemplateRenderSurface.HintBackground ? new PassiveButton() : new Button();
            button.MinWidth = 0; button.MinHeight = 24; button.Padding = new Thickness(4, 0, 4, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            if (node.Kind == TemplateNodeKind.IconButton && node.Icon is { } icon) button.Content = new SymbolIcon(SymbolFor(icon));
            else if (values[node.NodeId].Confirmed?.Kind == TemplateValueKind.Resource)
                button.Content = ImageSurface(out image, out placeholder);
            RoutedEventHandler click = (_, _) => Activate(node.NodeId, null, inputGeneration, inputEpoch);
            if (surface != TemplateRenderSurface.HintBackground)
            { button.Click += click; detach.Add(() => button.Click -= click); }
            element = button;
        }
        else if (node.Kind == TemplateNodeKind.Toggle && surface == TemplateRenderSurface.HintBackground)
        {
            // The only real ToggleSwitch is in the foreground. Its measured size is copied
            // after layout, including while disabled, so no duplicate control/peer is created.
            element = new TogglePlaceholder { MinHeight = 24, VerticalAlignment = VerticalAlignment.Center };
        }
        else if (node.Kind == TemplateNodeKind.Toggle)
        {
            var toggle = new ToggleSwitch();
            toggle.MinWidth = 0; toggle.MinHeight = 24; toggle.OffContent = ""; toggle.OnContent = "";
            toggle.VerticalAlignment = VerticalAlignment.Center;
            RoutedEventHandler changed = (_, _) =>
            {
                if (refreshing || disposed || inputEpoch != epoch) return;
                var value = new TemplateValue(TemplateValueKind.Boolean, Boolean: toggle.IsOn);
                controller.Preview(node.NodeId, value, inputGeneration);
                Activate(node.NodeId, value, inputGeneration, inputEpoch);
            };
            if (surface != TemplateRenderSurface.HintBackground)
            { toggle.Toggled += changed; detach.Add(() => toggle.Toggled -= changed); }
            element = toggle;
        }
        else if (node.Kind == TemplateNodeKind.Slider)
        {
            Slider slider = surface == TemplateRenderSurface.HintBackground ? new PassiveSlider() : new Slider();
            slider.Minimum = node.Minimum!.Value; slider.Maximum = node.Maximum!.Value;
            slider.StepFrequency = node.Step!.Value; slider.SmallChange = node.Step.Value; slider.LargeChange = node.Step.Value;
            slider.MinWidth = 100; slider.MinHeight = 24; slider.VerticalAlignment = VerticalAlignment.Center;
            slider.IsThumbToolTipEnabled = surface != TemplateRenderSurface.HintBackground;
            bool dragging = false;
            PointerEventHandler pressed = (_, _) => dragging = true;
            PointerEventHandler released = (_, _) =>
            {
                if (!dragging) return;
                dragging = false;
                Activate(node.NodeId, new(TemplateValueKind.Number, Number: slider.Value), inputGeneration, inputEpoch);
            };
            RangeBaseValueChangedEventHandler changed = (_, args) =>
            {
                if (refreshing || disposed || inputEpoch != epoch) return;
                var value = new TemplateValue(TemplateValueKind.Number, Number: args.NewValue);
                var preview = controller.Preview(node.NodeId, value, inputGeneration);
                if (preview.Accepted && !dragging) Activate(node.NodeId, value, inputGeneration, inputEpoch);
            };
            if (surface != TemplateRenderSurface.HintBackground)
            {
                slider.AddHandler(PointerPressedEvent, pressed, true);
                slider.AddHandler(PointerReleasedEvent, released, true);
                slider.AddHandler(PointerCaptureLostEvent, released, true);
                slider.ValueChanged += changed;
                detach.Add(() =>
                {
                    slider.RemoveHandler(PointerPressedEvent, pressed);
                    slider.RemoveHandler(PointerReleasedEvent, released);
                    slider.RemoveHandler(PointerCaptureLostEvent, released);
                    slider.ValueChanged -= changed;
                });
            }
            element = slider;
        }
        else if (node.Kind == TemplateNodeKind.Separator)
            element = new Border { Width = 1, Margin = new Thickness(4, 2, 4, 2), Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };
        else
        {
            var children = new StackPanel
            {
                Orientation = node.Kind is TemplateNodeKind.Horizontal or TemplateNodeKind.ListItem ? Orientation.Horizontal : Orientation.Vertical,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (node.Kind == TemplateNodeKind.Group && !string.IsNullOrWhiteSpace(node.AccessibleName))
                children.Children.Add(new TextBlock { Text = node.AccessibleName });
            foreach (var child in node.Children ?? []) children.Children.Add(Build(child, [.. ancestors, node.NodeId], values));
            if (node.Kind == TemplateNodeKind.ListItem && node.Action is not null)
            {
                Button button = surface == TemplateRenderSurface.HintBackground ? new PassiveButton() : new Button();
                button.Content = children; button.MinWidth = 0; button.Padding = new Thickness(4);
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                RoutedEventHandler click = (_, args) =>
                {
                    if (args.OriginalSource is DependencyObject source && IsNestedInteractive(source, button)) return;
                    Activate(node.NodeId, null, inputGeneration, inputEpoch);
                };
                if (surface != TemplateRenderSurface.HintBackground)
                { button.Click += click; detach.Add(() => button.Click -= click); }
                element = button;
            }
            else element = node.Kind == TemplateNodeKind.Group ? new Border { Child = children, Padding = new Thickness(4) } : children;
        }
        AutomationProperties.SetAutomationId(element, "mtp-template-" + node.NodeId);
        if (node.AccessibleName is { } name) AutomationProperties.SetName(element, name);
        if (node.Decorative) AutomationProperties.SetAccessibilityView(element, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        if (surface == TemplateRenderSurface.HintBackground && element is Control)
        {
            element.Opacity = 0;
            ((Control)element).IsTabStop = false;
            AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        }
        if (surface == TemplateRenderSurface.HintForeground && element is not Control)
            AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        if (surface == TemplateRenderSurface.HintForeground && node.Action is not null && element is Control)
        {
            // Loaded is delivered per control, after an earlier LayoutUpdated may already
            // have seen only part of the tree. Notify the owner when eligibility changes.
            RoutedEventHandler availability = (_, _) => InteractiveGeometryChanged?.Invoke();
            element.Loaded += availability; element.Unloaded += availability;
            detach.Add(() => { element.Loaded -= availability; element.Unloaded -= availability; });
        }
        views.Add(node.NodeId, new(node, element, ancestors, image, placeholder));
        return element;
    }

    private async void Activate(string nodeId, TemplateValue? value, long inputGeneration, long inputEpoch)
    {
        if (disposed || inputStopped || refreshing || epoch != inputEpoch || surface == TemplateRenderSurface.HintBackground) return;
        try
        {
            var operation = controller.ActivateAsync(nodeId, value, generationLifetime.Token, inputGeneration);
            Refresh();
            await operation;
            if (!disposed && epoch == inputEpoch) Refresh();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void UpdateImage(NodeView view, string? resource)
    {
        if (view.Resource == resource && (view.Loading || view.Lease is not null || !view.Retry)) return;
        view.CancelImage();
        view.Resource = resource;
        view.Retry = false;
        view.ImageError = null;
        if (resource is null) return;
        view.Loading = true;
        view.ImageCancellation = CancellationTokenSource.CreateLinkedTokenSource(generationLifetime.Token);
        _ = LoadImageAsync(view, resource, epoch, view.ImageCancellation.Token);
    }

    private async Task LoadImageAsync(NodeView view, string resource, long loadingEpoch, CancellationToken cancellationToken)
    {
        RegisteredImageResult result;
        try { result = await images.LoadAsync(applicationId, resource, cancellationToken); }
        catch (OperationCanceledException) { return; }
        if (disposed || epoch != loadingEpoch || cancellationToken.IsCancellationRequested || view.Resource != resource)
        { result.Lease?.Dispose(); return; }
        view.Loading = false;
        view.Lease = result.Lease;
        view.Retry = result.ErrorCode == "ImageLoaderBusy";
        view.ImageError = result.ErrorCode;
        view.Image!.Source = result.Lease?.Source;
        view.Placeholder!.Visibility = result.Lease is null ? Visibility.Visible : Visibility.Collapsed;
        if (result.ErrorCode is not null)
        {
            ToolTipService.SetToolTip(view.Element, "图片不可用：" + result.ErrorCode);
            AutomationProperties.SetHelpText(view.Element, "图片不可用：" + result.ErrorCode);
        }
    }

    private static Grid ImageSurface(out Image image, out SymbolIcon placeholder)
    {
        image = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform };
        placeholder = new SymbolIcon(Symbol.Pictures);
        var grid = new Grid { Width = 24, Height = 24 };
        grid.Children.Add(placeholder);
        grid.Children.Add(image);
        return grid;
    }

    private static Symbol SymbolFor(HostIcon icon) => icon switch
    {
        HostIcon.Check => Symbol.Accept,
        HostIcon.Warning or HostIcon.Error => Symbol.Important,
        HostIcon.Play => Symbol.Play,
        HostIcon.Pause => Symbol.Pause,
        HostIcon.Settings => Symbol.Setting,
        HostIcon.ChevronRight => Symbol.Forward,
        HostIcon.ChevronLeft => Symbol.Back,
        _ => Symbol.Message,
    };

    private static bool IsNestedInteractive(DependencyObject source, Button owner)
    {
        for (var current = source; current is not null && current != owner; current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase or ToggleSwitch or Slider) return true;
        return false;
    }

    private void ClearTree()
    {
        epoch++;
        generationLifetime.Cancel();
        generationLifetime.Dispose();
        generationLifetime = new();
        foreach (var unsubscribe in detach) unsubscribe();
        detach.Clear();
        foreach (var view in views.Values) view.CancelImage();
        views.Clear();
        Content = null;
        generation = -1;
    }

    /// <summary>Retire interaction immediately while keeping the last visual tree for its exit animation.</summary>
    internal void StopInteraction()
    {
        if (disposed || inputStopped) return;
        inputStopped = true;
        IsEnabled = false;
        IsHitTestVisible = false;
        generationLifetime.Cancel();
        controller.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearTree();
        generationLifetime.Dispose();
        controller.Dispose();
        PresentationChanged = null;
        CapturedSnapshot = null;
    }

    private sealed class PassiveButton : Button { protected override AutomationPeer OnCreateAutomationPeer() => null!; }
    private sealed class TogglePlaceholder : FrameworkElement
    {
        protected override AutomationPeer OnCreateAutomationPeer() => null!;
        protected override Size MeasureOverride(Size availableSize) => new(
            double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? MinHeight : Height);
    }
    private sealed class PassiveSlider : Slider { protected override AutomationPeer OnCreateAutomationPeer() => null!; }

    private sealed class NodeView(TemplateNode node, FrameworkElement element, string[] ancestors, Image? image, SymbolIcon? placeholder)
    {
        public TemplateNode Node { get; } = node;
        public FrameworkElement Element { get; } = element;
        public string[] Ancestors { get; } = ancestors;
        public Image? Image { get; } = image;
        public SymbolIcon? Placeholder { get; } = placeholder;
        public string? Resource { get; set; }
        public ImageLease? Lease { get; set; }
        public CancellationTokenSource? ImageCancellation { get; set; }
        public bool Loading { get; set; }
        public bool Retry { get; set; }
        public string? ImageError { get; set; }
        public void CancelImage()
        {
            ImageCancellation?.Cancel();
            ImageCancellation?.Dispose();
            ImageCancellation = null;
            Loading = false;
            if (Image is not null) Image.Source = null;
            Lease?.Dispose();
            Lease = null;
            if (Placeholder is not null) Placeholder.Visibility = Visibility.Visible;
        }
    }
}

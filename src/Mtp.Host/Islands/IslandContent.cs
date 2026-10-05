using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mtp.Host.Islands;

/// <summary>Host-owned content; test controls never enter the declaration/template contract.</summary>
internal sealed class IslandContent : UserControl
{
    private readonly Grid surface = new();
    private HostTestConfiguration appearance = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();
    private bool highContrast;
    internal bool HighContrast
    {
        get
        {
            try { highContrast = accessibility.HighContrast; }
            catch (System.Runtime.InteropServices.COMException) { }
            return highContrast;
        }
    }
    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Popup popup;
    private readonly Button button = new() { Content = "+1", Padding = new Thickness(8, 0, 8, 0), MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
    private readonly Action<string, object?> record;
    private readonly Func<HostComponentDisplayModel, Templates.TemplateRenderer?>? createTemplate;
    private Templates.TemplateRenderer? template;
    private Mtp.Platform.Core.StableIdentity? templateIdentity;
    private long clicks;
    private Mtp.Contracts.ActionSlotReference? action;
    private readonly Button actionButton = new() { Content = "执行", Padding = new Thickness(8, 0, 8, 0), MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
    public bool PopupOpen => popup.IsOpen;

    public IslandContent(HostComponentDisplayModel component, HostTestConfiguration config, Action<string, object?> record,
        Func<Mtp.Contracts.ActionSlotReference, Task>? invokeAction = null,
        Func<HostComponentDisplayModel, Templates.TemplateRenderer?>? createTemplate = null)
    {
        this.record = record;
        appearance = config;
        this.createTemplate = config.Controls ? null : createTemplate;
        RequestedTheme = config.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        AutomationProperties.SetAutomationId(this, "MtpHostIslandContent");
        label.Text = config.Controls ? "0" : component.Text + " · " + component.StatusLabel;
        AutomationProperties.SetName(label, config.Controls ? "本地测试计数" : "声明组件");
        surface.Padding = new Thickness(4, 0, 4, 0);
        surface.Children.Add(label);
        Content = surface;
        Loaded += (_, _) => record("content-loaded", new { ActualWidth, ActualHeight, config.Controls });
        ApplySurface(config);
        ActualThemeChanged += (_, _) => { ApplySurface(appearance); record("content-theme", ActualTheme.ToString()); };
        var close = new Button { Content = "关闭 Popup" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Host 本地 Popup 与焦点测试" });
        panel.Children.Add(close);
        popup = new Popup
        {
            IsLightDismissEnabled = true,
            ShouldConstrainToRootBounds = false,
            PlacementTarget = button,
            DesiredPlacement = PopupPlacementMode.Top,
            Child = new Border { Padding = new Thickness(12), Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"], Child = panel }
        };
        close.Click += (_, _) => SetPopup(false);
        popup.Opened += (_, _) => record("popup-opened", new { appearance = "pending-human" });
        popup.Closed += (_, _) => record("popup-closed", null);
        if (config.Controls)
        {
            surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            surface.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            surface.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            surface.ColumnSpacing = 4;
            var toggle = new ToggleSwitch { MinWidth = 0, OffContent = "", OnContent = "", VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = 25, MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button, "增加本地计数");
            AutomationProperties.SetName(toggle, "本地开关");
            AutomationProperties.SetName(slider, "本地滑块");
            button.Click += (_, _) => { clicks++; Update(0); record("button", clicks); };
            toggle.Toggled += (_, _) => record("toggle", toggle.IsOn);
            slider.ValueChanged += (_, e) => record("slider", e.NewValue);
            Grid.SetColumn(button, 1); Grid.SetColumn(toggle, 2); Grid.SetColumn(slider, 3);
            surface.Children.Add(button); surface.Children.Add(toggle); surface.Children.Add(slider);
            surface.Children.Add(popup);
        }
        else if (invokeAction is not null)
        {
            surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            surface.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            surface.ColumnSpacing = 8;
            Grid.SetColumn(actionButton, 1);
            AutomationProperties.SetAutomationId(actionButton, "MtpDeclaredAction");
            AutomationProperties.SetName(actionButton, "执行组件动作");
            actionButton.Click += async (_, _) =>
            {
                if (action is not { } slot || !actionButton.IsEnabled) return;
                actionButton.IsEnabled = false;
                await invokeAction(slot);
            };
            surface.Children.Add(actionButton);
            UpdateConfirmed(component);
        }
        GotFocus += (_, _) => record("content-focus", new { automatic = true, human = "pending" });
        if (!config.Controls) UpdateConfirmed(component);
    }

    private void ApplySurface(HostTestConfiguration config)
    {
        if (HighContrast)
        {
            try { surface.Background = new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)); }
            catch (System.Runtime.InteropServices.COMException) { record("surface-state-unavailable", "保留最后高对比背景"); }
            return;
        }
        var shade = ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
        surface.Background = new SolidColorBrush(Color.FromArgb(config.Material == "none" ? (byte)(255 * config.Alpha) : (byte)0, shade, shade, shade));
    }
    public void ApplyAppearance(HostTestConfiguration config)
    {
        appearance = config;
        RequestedTheme = config.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplySurface(config);
    }
    public void Update(long value) => label.Text = (value + clicks).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public void UpdateConfirmed(HostComponentDisplayModel component)
    {
        if (template is not null && (!component.HasTemplate || templateIdentity != component.Identity))
        {
            surface.Children.Remove(template);
            template.Dispose(); template = null; templateIdentity = null;
        }
        if (component.HasTemplate && template is null)
        {
            template = createTemplate?.Invoke(component);
            if (template is not null)
            {
                templateIdentity = component.Identity;
                Grid.SetColumnSpan(template, Math.Max(1, surface.ColumnDefinitions.Count));
                surface.Children.Add(template);
            }
        }
        label.Visibility = template is null ? Visibility.Visible : Visibility.Collapsed;
        template?.Refresh();
        label.Text = component.Text + " · " + component.StatusLabel;
        action = component.Action;
        actionButton.Visibility = template is not null || action is null ? Visibility.Collapsed : Visibility.Visible;
        actionButton.IsEnabled = component.CanInvokeAction;
        actionButton.Content = component.ActionBusy ? "等待确认" : "执行";
    }
    public void SetPopup(bool open) { popup.IsOpen = open; record("popup-request", open); }
    public void Release() { popup.IsOpen = false; template?.Dispose(); template = null; templateIdentity = null; }
}

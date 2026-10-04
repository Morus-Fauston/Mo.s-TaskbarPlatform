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
    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Popup popup;
    private readonly Button button = new() { Content = "+1", Padding = new Thickness(8, 0, 8, 0), MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
    private readonly Action<string, object?> record;
    private long clicks;
    public bool PopupOpen => popup.IsOpen;

    public IslandContent(HostComponentDisplayModel component, HostTestConfiguration config, Action<string, object?> record)
    {
        this.record = record;
        RequestedTheme = config.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        AutomationProperties.SetAutomationId(this, "MtpHostIslandContent");
        label.Text = config.Controls ? "0" : component.Text + " · " + component.StatusLabel;
        AutomationProperties.SetName(label, config.Controls ? "本地测试计数" : "声明组件");
        surface.Padding = new Thickness(4, 0, 4, 0);
        surface.Children.Add(label);
        Content = surface;
        Loaded += (_, _) => record("content-loaded", new { ActualWidth, ActualHeight, config.Controls });
        ApplySurface(config);
        ActualThemeChanged += (_, _) => { ApplySurface(config); record("content-theme", ActualTheme.ToString()); };
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
        GotFocus += (_, _) => record("content-focus", new { automatic = true, human = "pending" });
    }

    private void ApplySurface(HostTestConfiguration config)
    {
        var shade = ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
        surface.Background = new SolidColorBrush(Color.FromArgb(config.Material == "none" ? (byte)(255 * config.Alpha) : (byte)0, shade, shade, shade));
    }
    public void Update(long value) => label.Text = (value + clicks).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public void SetPopup(bool open) { popup.IsOpen = open; record("popup-request", open); }
    public void Release() => popup.IsOpen = false;
}

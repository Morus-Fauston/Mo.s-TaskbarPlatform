using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TaskbarIslandLab.Logic;
using Windows.UI;

namespace TaskbarIslandLab;

public sealed partial class LabContent : UserControl
{
    private long clicks;
    private Action<string, object?>? record;
    private Storyboard? animation;
    public event Action? StopRequested;

    public LabContent(LabOptions options, Action<string, object?> record)
    {
        InitializeComponent();
        this.record = record;
        Surface.Background = new SolidColorBrush(Color.FromArgb((byte)(options.Alpha * 255), 32, 128, 192));
        RequestedTheme = options.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ProbePopup.Opened += (_, _) => record("popup-opened", new { visualAndFocus = "pending-human" });
        ProbePopup.Closed += (_, _) => record("popup-closed", null);
        ActualThemeChanged += (_, _) => record("theme", ActualTheme.ToString());
    }

    public void Update(long value) => Counter.Text = (value + clicks).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private void Increment(object sender, RoutedEventArgs e)
    {
        clicks++;
        Update(0);
        animation?.Stop();
        animation = new Storyboard();
        var pulse = new DoubleAnimation { From = 0.35, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(450)) };
        Storyboard.SetTarget(pulse, Counter);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        animation.Children.Add(pulse);
        animation.Begin();
        record?.Invoke("button", clicks);
    }
    private void Toggled(object sender, RoutedEventArgs e) => record?.Invoke("toggle", Switch.IsOn);
    private void SliderChanged(object sender, RangeBaseValueChangedEventArgs e) => record?.Invoke("slider", e.NewValue);
    private void TogglePopup(object sender, RoutedEventArgs e) => ProbePopup.IsOpen = !ProbePopup.IsOpen;
    internal void TogglePopupFixture() => TogglePopup(this, new RoutedEventArgs());
    internal bool PopupIsOpen => ProbePopup.IsOpen;
    internal bool PopupConstrainedToRoot => ProbePopup.IsConstrainedToRootBounds;
    internal void InvokePopupCloseFixture() => new ButtonAutomationPeer(PopupCloseButton).Invoke();
    internal (global::Windows.Foundation.Rect Panel, global::Windows.Foundation.Rect Label, global::Windows.Foundation.Rect CloseButton, bool CloseOffscreen) ReadPopupFixture()
    {
        var panel = new FrameworkElementAutomationPeer((FrameworkElement)ProbePopup.Child);
        var label = new TextBlockAutomationPeer(PopupLabel);
        var button = new ButtonAutomationPeer(PopupCloseButton);
        return (panel.GetBoundingRectangle(), label.GetBoundingRectangle(), button.GetBoundingRectangle(), button.IsOffscreen());
    }
    private void RequestStop(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    public void CloseTransient()
    {
        ProbePopup.IsOpen = false;
        ActionButton.ContextFlyout?.Hide();
        animation?.Stop();
    }
    public void Release()
    {
        CloseTransient();
        StopRequested = null;
        record = null;
    }
    public bool HasExpectedTree => ActionButton is Button && Switch is ToggleSwitch && ValueSlider is Slider && Counter.Text.Length > 0;
}

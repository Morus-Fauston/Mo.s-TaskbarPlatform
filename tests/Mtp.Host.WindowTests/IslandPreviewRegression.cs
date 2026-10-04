using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Host.Islands;

namespace Mtp.Host.WindowTests;

internal static class IslandPreviewRegression
{
    public static async Task RunAsync(MainWindow window, HostConsoleController controller, string root, Action<string> log)
    {
        var view = (FrameworkElement)window.Content;
        T Find<T>(string name) => (T)view.FindName(name);
        async Task Click(string name)
        {
            var button = Find<Button>(name);
            Check(button.IsEnabled, name + " is disabled");
            new ButtonAutomationPeer(button).Invoke();
            await Task.Delay(120);
        }
        var preview = controller.Preview;
        try
        {
            controller.SetVisibility(false);
            Find<NavigationView>("Navigation").SelectedItem = Find<NavigationView>("Navigation").MenuItems[1];
            Find<ComboBox>("AlphaCombo").SelectedIndex = 1;
            await Click("OpenPreviewButton");
            Check(preview.IsAlive && preview.Configuration!.Controls, "Preview did not start without taskbar display");
            Check(controller.Session.State == IslandDisplayState.Hidden && !controller.Tests.IsRunning, "Preview changed taskbar intent or created a report");
            Check(!Find<Button>("OpenControlsButton").IsEnabled, "Taskbar case allowed during preview");
            var rejected = false;
            try { controller.Tests.Start(new()); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !controller.Tests.IsRunning, "Controller failed to enforce preview/case exclusion");

            var nodes = Descendants(preview.ContentRoot!).ToArray();
            Check(nodes.OfType<ToggleSwitch>().Count() == 1 && nodes.OfType<Slider>().Count() == 1, "Real island controls missing");
            var increment = nodes.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "增加本地计数");
            new ButtonAutomationPeer(increment).Invoke();
            await Task.Delay(50);
            Check(nodes.OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "本地测试计数").Text == "1", "Preview button does not update native XAML content");
            await ConsoleScreenshot.SaveAsync(preview.ContentRoot!, Path.Combine(root, "preview-controls.png"));
            await Click("PreviewPopupButton");
            Check(preview.PopupOpen, "Preview Popup not open");
            preview.SetPopup(false);

            var handle = preview.Handle;
            var bridge = preview.Bridge;
            await Click("OpenPreviewButton");
            Check(handle == preview.Handle && bridge == preview.Bridge, "Repeated open recreated matching preview");
            var rect = NativeWindows.WindowBounds(handle);
            NativeWindows.Place(handle, rect.Left + 60, rect.Top + 40, rect.Right - rect.Left, rect.Bottom - rect.Top);
            await Task.Delay(120);
            Check(preview.Bridge == bridge && preview.IsAlive, "Move recreated or invalidated the content island");
            var clientOrigin = NativeWindows.ClientOrigin(handle);
            var childRect = NativeWindows.WindowBounds(preview.IslandHandle);
            Check(childRect.Left == clientOrigin.X && childRect.Top == clientOrigin.Y, "Island overlaps non-client caption instead of filling client area");
            NativeWindows.Place(handle, rect.Left, rect.Top, 700, 300);
            await Task.Delay(120);
            var client = NativeWindows.Client(handle);
            var child = NativeWindows.Client(preview.IslandHandle);
            Check(child.Right == client.Right && child.Bottom == client.Bottom, "Resized preview did not resize island");

            foreach (var variant in new[] { (0, 0), (0, 1), (0, 2), (1, 2), (2, 2) })
            {
                Find<ComboBox>("MaterialCombo").SelectedIndex = variant.Item1;
                Find<ComboBox>("AlphaCombo").SelectedIndex = variant.Item2;
                Find<ComboBox>("ThemeCombo").SelectedIndex = 2;
                await Click("OpenPreviewButton");
                Check(preview.IsAlive && preview.Configuration!.Material == new[] { "none", "acrylic", "mica" }[variant.Item1] &&
                    preview.Configuration.Theme == "dark", "Preview material/theme selection did not reach actual host");
            }
            Find<CheckBox>("PreviewControlsToggle").IsChecked = false;
            await Click("OpenPreviewButton");
            Check(!Descendants(preview.ContentRoot!).OfType<Slider>().Any(), "Controls were not removed for plain preview");
            Check(!Find<Button>("PreviewPopupButton").IsEnabled, "Popup enabled for plain preview");
            SendMessageW(handle, 0x10, 0, 0); // Native caption Close follows the owner cleanup path.
            await Task.Delay(150);
            Check(!preview.IsOpen && !NativeWindows.IsWindow(handle) && !NativeWindows.IsWindow(bridge), "Native Close left preview or original bridge behind");
            Check(!Find<Button>("ClosePreviewButton").IsEnabled, "Native Close did not update console");
            await Click("OpenPreviewButton");
            Check(preview.IsAlive, "Preview failed to reopen after native Close");
            SendMessageW(preview.Handle, 0x10, 0, 0);
            preview.Close();
            controller.OpenPreview(WinRT.Interop.WindowNative.GetWindowHandle(window), new(Controls: true));
            await Task.Delay(150);
            Check(preview.IsAlive, "Late close from prior generation closed a new preview");
            await Click("ClosePreviewButton");
            controller.SetVisibility(true);
            controller.Tests.Start(new());
            Check(!Find<Button>("OpenPreviewButton").IsEnabled, "Preview enabled during case");
            rejected = false;
            try { controller.OpenPreview(WinRT.Interop.WindowNative.GetWindowHandle(window), new()); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !preview.IsOpen, "Preview/case exclusion only enforced in UI");
            controller.Tests.Stop();
            log("PASS: independent preview real controls, Popup, config, native move without rebuild, client resize, native Close/reopen, and case exclusion. Appearance/drag feel pending human.");
        }
        finally
        {
            preview.Close();
            controller.SetVisibility(true);
            Find<CheckBox>("PreviewControlsToggle").IsChecked = true;
            Find<ComboBox>("MaterialCombo").SelectedIndex = 0;
            Find<ComboBox>("AlphaCombo").SelectedIndex = 0;
            Find<ComboBox>("ThemeCombo").SelectedIndex = 0;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nuint wparam, nint lparam);
}

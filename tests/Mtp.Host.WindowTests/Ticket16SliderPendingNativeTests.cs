using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mtp.Contracts;
using Mtp.Host.Templates;
using Mtp.Host.Flyouts;

namespace Mtp.Host.WindowTests;

internal static class Ticket16SliderPendingNativeTests
{
    internal static async Task RunAsync(Action<string> log)
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "template-slider-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var states = new BrokerStateStore(["interactive-test"]);
        InteractiveHintManagerNativeTests.Connect(states, "pending-visual");
        var response = new TaskCompletionSource<ProtocolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int sent = 0;
        using var images = new RegisteredImageCache();
        using var controller = new TemplateInteractionController(states, "interactive-test",
            new("group", TemplateEntryKind.Hint, "adjust"), (_, _, _, _) => { sent++; return response.Task; });
        using var renderer = new TemplateRenderer(controller, images);
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        root.Children.Add(renderer);
        var window = new Window { Content = root };
        FlyoutNative.GetCursorPos(out var savedCursor);
        bool pointerDown = false;
        try
        {
            var work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            window.AppWindow.MoveAndResize(new(work.X + 600, work.Y + 200, 540, 200));
            window.Activate();
            await Task.Delay(120);
            var slider = renderer.GetNodeElement("value") as Slider ?? throw new InvalidOperationException("Pending slider did not load");
            var before = Observe(slider);
            var accentBefore = Accent(slider);
            await ConsoleScreenshot.SaveAsync(window, System.IO.Path.Combine(directory, "before.png"));
            ((IRangeValueProvider)new SliderAutomationPeer(slider).GetPattern(PatternInterface.RangeValue)).SetValue(70);
            await Task.Delay(120);
            var waiting = Observe(slider);
            var busy = controller.GetSnapshot()!.Nodes.Single(x => x.NodeId == "value");
            bool remainedEnabled = slider.IsEnabled;
            var accentWaiting = Accent(slider);
            await ConsoleScreenshot.SaveAsync(window, System.IO.Path.Combine(directory, "waiting.png"));
            response.SetResult(ProtocolResult.Reject("FixtureDenied", "test rollback"));
            await Task.Delay(120);
            var restored = Observe(slider);
            await ConsoleScreenshot.SaveAsync(window, System.IO.Path.Combine(directory, "restored.png"));
            var observation = new { before, waiting, restored, busy.Busy, busy.Enabled, sent, confirmed = busy.Confirmed?.Number, preview = busy.Preview?.Number };
            File.WriteAllText(System.IO.Path.Combine(directory, "observations.json"), JsonSerializer.Serialize(observation, new JsonSerializerOptions { WriteIndented = true }));
            log("slider-pending-visual: " + JsonSerializer.Serialize(observation));
            Check(busy.Busy && sent == 1 && busy.Confirmed?.Number == 40 && busy.Preview?.Number == 70, "Probe did not hold a real pending action with separate confirmed and preview values");
            Check(remainedEnabled, "Waiting for slider confirmation switched the native control to Disabled and its non-accent palette");
            Check(accentBefore == accentWaiting, "Pending confirmation changed the native slider accent palette");
            Check(slider.IsEnabled && slider.Value == 40, "Refused slider value did not return to the confirmed value");

            response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ((IRangeValueProvider)new SliderAutomationPeer(slider).GetPattern(PatternInterface.RangeValue)).SetValue(55);
            await Task.Delay(60);
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            Check(FlyoutNative.SetWindowPos(handle, new(-1), 0, 0, 0, 0, 0x13), "Cannot expose owned pending slider");
            var thumb = Descendants(slider).OfType<Thumb>().First(x => x.ActualWidth > 0 && x.ActualHeight > 0);
            var bounds = TaskbarFlyoutNativeRegression.ElementScreenBounds(thumb, window);
            int x = bounds.X + bounds.Width / 2, y = bounds.Y + bounds.Height / 2;
            Check(SetCursorPos(x, y), "Cannot position pointer over owned slider");
            SendMouse(1); await Task.Delay(30);
            Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(new() { X = x, Y = y })) == handle, "Pending slider input target is occluded");
            SendMouse(2); pointerDown = true; await Task.Delay(30);
            SendMouse(1, 24); await Task.Delay(40);
            double draft = slider.Value;
            Check(Captured(slider) && draft > 55, "Real subsequent drag was not captured");
            response.SetResult(ProtocolResult.Reject("EarlierRejected", "older request"));
            await Task.Delay(80);
            Check(Captured(slider) && slider.Value == draft, "Earlier confirmation/rejection overwrote the captured subsequent drag");
            response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SendMouse(4); pointerDown = false; await Task.Delay(60);
            Check(sent == 3 && slider.Value == draft && !Captured(slider), "Final release did not submit the latest preview exactly once");
            response.SetResult(ProtocolResult.Reject("LatestRejected", "latest request"));
            await Task.Delay(80);
            Check(slider.Value == 40 && slider.IsEnabled, "Latest rejected target lost confirmation rollback");
            log("slider-pending-drag-pass: " + JsonSerializer.Serialize(new { draft, sent, restored = slider.Value, preservedWhileCaptured = true }));
            Check(states.Handle(new() { Kind = MessageKind.Disconnected, ApplicationId = "interactive-test", SessionId = "pending-visual" }).Result!.Accepted,
                "Disconnected positive control setup failed");
            renderer.Refresh();
            Check(renderer.GetNodeElement("value") is null && renderer.GetInteractiveRegions().Count == 0,
                "Actually disconnected slider must not expose live input");
            Check(!(await controller.ActivateAsync("value", new(TemplateValueKind.Number, Number: 10))).Accepted && sent == 3,
                "Disconnected input still dispatched an action");
            log("PASS: slider remains visually available while confirmation is pending, rejects to confirmed value, and disables on real disconnect; screenshots=" + directory);
        }
        finally { if (pointerDown) SendMouse(4); SetCursorPos(savedCursor.X, savedCursor.Y); response.TrySetResult(ProtocolResult.Reject("FixtureEnded", "cleanup")); window.Close(); }
    }

    private static object Observe(Slider slider) => new
    {
        slider.IsEnabled, slider.Value, theme = slider.ActualTheme.ToString(),
        fills = Descendants(slider).OfType<Shape>().Where(x => x.Fill is SolidColorBrush).Select(x => new { x.Name, color = ((SolidColorBrush)x.Fill).Color.ToString() }).ToArray(),
        states = Descendants(slider).OfType<FrameworkElement>().SelectMany(x => VisualStateManager.GetVisualStateGroups(x)).Select(x => new { x.Name, current = x.CurrentState?.Name }).ToArray(),
    };
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Windows.UI.Color Accent(Slider slider) => ((SolidColorBrush)Descendants(slider).OfType<Shape>().Single(x => x.Name == "HorizontalDecreaseRect").Fill).Color;
    private static bool Captured(Slider slider) => Descendants(slider).OfType<UIElement>().Any(x => x.PointerCaptures?.Count > 0);
    private static void SendMouse(uint flags, int dx = 0) => Check(SendInput(1, [new Input { Flags = flags, X = dx }], Marshal.SizeOf<Input>()) == 1, "Cannot send owned slider input");
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public int X; [FieldOffset(20)] public uint Flags; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
}

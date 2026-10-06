using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Contracts;
using Mtp.Host.Islands;
using Mtp.Platform.Core;
using Windows.Foundation;

namespace Mtp.Host.WindowTests;

/// <summary>Native production surface with injected presentation environments; does not change OS settings.</summary>
internal static class VisualEnvironmentNativeRegression
{
    private static readonly TaskbarComponentKey Dynamic = new("visual-environment", "main", "items");
    private static readonly TaskbarComponentKey Ordinary = new("visual-environment", "main", "ordinary");
    private const string FullStatus = "完整状态文字：放大字体后仍须能够读取所有内容，槽位保持固定高度。";

    public static async Task RunAsync(Action<string> log)
    {
        var clock = Stopwatch.StartNew();
        using var surface = new TaskbarGroupSurface(_ => null, _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        var window = new Window { Content = surface, Title = "MTP visual environment regression" };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 160));
        var item = Item("composite", true);
        var status = Item("status", false);
        var animation = new TaskbarGroupAnimation();
        var presets = new PresetMotionSampler();
        Button? original = null;
        TextBlock? originalText = null;
        double originalMeasured = 0;
        int samples = 0;
        string evidence = Path.Combine(AppContext.BaseDirectory, "visual-environment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        try
        {
            window.Activate();
            foreach (var environment in new[]
            {
                new HostPresentationEnvironment(),
                new HostPresentationEnvironment(HostPresentationDensity.Normal, 1.25),
                new HostPresentationEnvironment(HostPresentationDensity.Compact, 2.25),
                new HostPresentationEnvironment()
            })
            {
                var snapshot = Snapshot(item, status, environment);
                Check(animation.Retarget(snapshot.Layout, TimeSpan.FromSeconds(samples), true, animation.Generation), "Surface target rejected.");
                surface.Apply(snapshot, animation.Sample(TimeSpan.FromSeconds(samples)));
                await Until(() => surface.IsLoaded && Math.Abs(surface.ActualWidth - snapshot.Layout.WidthDip) < 0.01);
                surface.UpdateLayout();
                Check(presets.Synchronize(snapshot.Items, surface.GetPresetMeasurements(), TimeSpan.FromSeconds(samples), true).Accepted,
                    "Preset sampler rejected visual environment fields or measurements.");
                surface.ApplyPresetReadings(presets.Sample(TimeSpan.FromSeconds(samples), true), true);
                var button = Find<Button>(surface, Id("composite"));
                var text = Find<TextBlock>(surface, Id("composite") + "/Text");
                var counter = Find<TextBlock>(surface, Id("composite") + "/Counter");
                var timer = Find<TextBlock>(surface, Id("composite") + "/Timer");
                var progress = Find<TextBlock>(surface, Id("composite") + "/ProgressText");
                var marker = Find<FontIcon>(surface, Id("composite") + "/StatusMarker");
                foreach (var label in new[] { text, counter, timer, progress, Find<TextBlock>(surface, Id("status") + "/Text") })
                    Check(Math.Abs(label.FontSize - 11 * environment.TextScale) < 0.001 && !label.IsTextScaleFactorEnabled,
                        "Custom text did not use exactly one explicit text scale.");
                Check(marker.FontSize == 11 && !marker.IsTextScaleFactorEnabled, "Semantic icon scaled with text.");
                Check(surface.ActualHeight == 32 && button.ActualHeight == 32, "Text scale enlarged the fixed 32 DIP slot.");
                Check(AutomationProperties.GetName(button).Contains(FullStatus, StringComparison.Ordinal) &&
                    (ToolTipService.GetToolTip(button) as string)?.Contains(FullStatus, StringComparison.Ordinal) == true,
                    "Clipped large text lost its full tooltip or accessibility name.");
                var ordinary = Find<Grid>(surface, "mtp-component/visual-environment/main/ordinary");
                var ordinaryText = ordinary.Children.OfType<TextBlock>().Single();
                Check(ordinaryText.IsTextScaleFactorEnabled, "Ordinary WinUI text lost system automatic text scaling.");
                if (original is not null)
                    Check(ReferenceEquals(original, button) && ReferenceEquals(originalText, text) && snapshot.Items[0].Handle == item.Handle,
                        "Environment change replaced stable controls or interaction identity.");
                else { original = button; originalText = text; }

                double CenterY(FrameworkElement element) => element.TransformToVisual(surface).TransformPoint(new Point(0, element.ActualHeight / 2)).Y;
                double y = CenterY(counter);
                double statusY = CenterY(text);
                if (environment.TextScale > 1)
                {
                    Check(Math.Abs(y - statusY) < 1 && Math.Abs(y - CenterY(timer)) < 1 && Math.Abs(y - CenterY(progress)) < 1,
                        "Large composite retained vertically stacked rows.");
                    Check(counter.ActualWidth > 0 && timer.ActualWidth > 0 && progress.ActualWidth > 0,
                        "Large composite discarded independent metric columns.");
                }
                else Check(statusY - y >= 15, "Default scale failed to restore the compact two-row presentation.");

                var measurement = surface.GetPresetMeasurements().Single(value => value.Key.ItemId == "status");
                var reference = new TextBlock { Text = FullStatus, FontSize = text.FontSize, FontFamily = text.FontFamily,
                    IsTextScaleFactorEnabled = false, TextWrapping = TextWrapping.NoWrap };
                reference.Measure(new Size(double.PositiveInfinity, 32));
                Check(Math.Abs(measurement.TextWidthDip - reference.DesiredSize.Width) < 0.5,
                    "Detached measurement disagrees with explicit-scale text.");
                if (samples == 0) originalMeasured = measurement.TextWidthDip;
                else if (environment.TextScale > 1) Check(measurement.TextWidthDip > originalMeasured,
                    "Text scale did not invalidate measured content width.");
                else Check(Math.Abs(measurement.TextWidthDip - originalMeasured) < 0.5,
                    "Returning to default scale retained stale measurement.");
                samples++;
                log($"visual-environment-frame: sample={samples}; density={environment.Density}; textScale={environment.TextScale}; height={surface.ActualHeight}; textWidth={measurement.TextWidthDip}; viewport={measurement.ViewportWidthDip}; sameControl=true; fullDescription=true");
                if (environment.TextScale == 2.25)
                    await ConsoleScreenshot.SaveAsync(surface, Path.Combine(evidence, "compact-text-225.png")).WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally { window.Content = null; window.Close(); }
        Check(!NativeWindows.IsWindow(hwnd), "Visual environment test retained its native window.");
        log($"visual-environment-pass: samples={samples}; elapsedMs={clock.ElapsedMilliseconds}; simulatedTextAndDensity=true; nativeWinUI=true; explicitScaleOnce=true; stableControls=true; ordinaryAutoScale=true; cleanup=true; evidence={evidence}");

        async Task Until(Func<bool> condition)
        {
            while (!condition())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new InvalidOperationException("Visual environment surface failed to arrange within 12 seconds.");
                await Task.Delay(20);
            }
        }
    }

    private static HostGroupPresentationSnapshot Snapshot(HostItemPresentation composite, HostItemPresentation status,
        HostPresentationEnvironment environment)
    {
        var items = new[] { composite, status };
        var byKey = items.ToDictionary(value => new TaskbarItemKey(Dynamic, value.Item.ItemId, value.PresenceGeneration));
        var ordinary = new HostComponentDisplayModel(new StableIdentity(new StableId("visual-environment")).CreateChild(new StableId("main")).CreateChild(new StableId("ordinary")),
            "普通 WinUI 文本", CapabilityStatus.Available, "available", true);
        double unit = environment.Density == HostPresentationDensity.Compact ? 28 : 32;
        var layout = TaskbarGroupLayout.Calculate([
            new(Dynamic, Items: byKey.Keys.Select(key => new TaskbarMeasuredItem(key, 3 * unit * environment.TextScale)).ToArray()),
            new(Ordinary, 240)], 32);
        return new(layout, [ordinary], items, new Dictionary<TaskbarComponentKey, HostComponentDisplayModel> { [Ordinary] = ordinary }, byKey)
        { Environment = environment };
    }

    private static HostItemPresentation Item(string id, bool composite)
    {
        var fields = composite ? ContentFields.Counter | ContentFields.Timer | ContentFields.Progress | ContentFields.Status : ContentFields.Status;
        var presentation = new ItemPresentation(composite ? PresetTemplate.Composite : PresetTemplate.Status, fields, new(WidthTier.Large), PresetVariant.Text);
        return new(new(Guid.NewGuid(), "screen", new(Dynamic.ApplicationId, Dynamic.FeatureGroupId, Dynamic.ComponentId, id), 1), "session",
            DynamicContentKind.OrdinaryItems, new("preset", true, presentation),
            new(id, "preset", [], new(Timer: composite ? new(TimerDirection.CountUp, DateTimeOffset.UnixEpoch, 0, IsPaused: true) : null,
                Counter: composite ? new(CounterSemantics.CompletedCount, 4, 10) : null,
                Progress: composite ? new(ProgressMode.Determinate, 25, 100) : null, Status: new(FullStatus))), false, presentation, true, 1);
    }

    private static string Id(string item) => "mtp-item/visual-environment/main/items/" + item + "/1";
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Children(child)) yield return descendant;
        }
    }
    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Children(root).OfType<T>().Single(value => AutomationProperties.GetAutomationId(value) == id);
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
}

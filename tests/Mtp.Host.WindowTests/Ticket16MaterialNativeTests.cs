using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mtp.Host.Flyouts;
using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class Ticket16MaterialNativeTests
{
    internal static async Task RunAsync(Action<string> log)
    {
        var lowerRoot = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var lower = new Window { Content = lowerRoot };
        var upper = new Window { Content = root };
        var work = DisplayArea.Primary.WorkArea;
        var rect = new Windows.Graphics.RectInt32(work.X + 400, work.Y + 160, 360, 220);
        nint lowerHandle = WinRT.Interop.WindowNative.GetWindowHandle(lower), upperHandle = WinRT.Interop.WindowNative.GetWindowHandle(upper);
        bool upperClosed = false;
        try
        {
            foreach (var window in new[] { lower, upper })
            {
                if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(false, false);
                window.AppWindow.MoveAndResize(rect);
            }
            lower.AppWindow.Show(false);
            upper.AppWindow.Show(false);
            FlyoutNative.SetWindowPos(lowerHandle, new(-1), 0, 0, 0, 0, 0x13);
            FlyoutNative.SetWindowPos(upperHandle, new(-1), 0, 0, 0, 0, 0x13);
            var callbackFailures = new List<string>();
            var callbackCases = new List<(string Name, SystemBackdrop Backdrop)>
            {
                ("solid", new SolidTintBackdrop(upper, Windows.UI.Color.FromArgb(153, 32, 32, 32), (kind, data) => log(kind + ": " + JsonSerializer.Serialize(data))))
            };
            if (DesktopAcrylicController.IsSupported())
            {
                callbackCases.Add(("configured-acrylic", new ConfiguredMaterialBackdrop(MaterialKind.Acrylic, .6, (kind, data) => log(kind + ": " + JsonSerializer.Serialize(data)))));
                callbackCases.Add(("nonactivating-acrylic", new NonActivatingAcrylicBackdrop((kind, data) => log(kind + ": " + JsonSerializer.Serialize(data)))));
            }
            if (MicaController.IsSupported()) callbackCases.Add(("configured-mica", new ConfiguredMaterialBackdrop(MaterialKind.Mica, .6, (kind, data) => log(kind + ": " + JsonSerializer.Serialize(data)))));
            foreach (var item in callbackCases)
            {
                upper.SystemBackdrop = item.Backdrop;
                await Task.Delay(60);
                var callbackRoot = root.XamlRoot;
                DeliverNullConfiguration(item.Backdrop, callbackRoot, item.Name + "-connected");
                upper.SystemBackdrop = null;
                DeliverNullConfiguration(item.Backdrop, callbackRoot, item.Name + "-retired");
            }
            Check(callbackFailures.Count == 0, "Framework null/retired configuration notification failed: " + string.Join("; ", callbackFailures));
            log("followup-callback-pass: connected and retired null-target notifications for " + callbackCases.Count + " backdrop paths");

            void DeliverNullConfiguration(SystemBackdrop backdrop, XamlRoot callbackRoot, string phase)
            {
                // This protected WinUI override is an inbound framework boundary; WinUI #11535
                // documents null-target notifications. Keep a real connected/retired native owner.
                var callback = backdrop.GetType().GetMethod("OnDefaultSystemBackdropConfigurationChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                try { callback.Invoke(backdrop, [null, callbackRoot]); log("configuration-callback-pass: " + phase); }
                catch (System.Reflection.TargetInvocationException error)
                { callbackFailures.Add(phase + ": " + error.InnerException); log("configuration-callback-failed: " + phase + " " + error.InnerException); }
            }
            var style = FlyoutNative.GetWindowLongPtrW(upperHandle, -16).ToInt64();
            log("ticket16-material-style-before: " + style.ToString("X"));
            upper.SystemBackdrop = new SolidTintBackdrop(upper, Windows.UI.Color.FromArgb(153, 32, 32, 32), (kind, data) => log(kind + ": " + JsonSerializer.Serialize(data)));
            await Task.Delay(250);
            int black = Read();
            lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
            await Task.Delay(180);
            int white = Read();
            log("ticket16-solid-native-pixels: " + JsonSerializer.Serialize(new { black, white, expectedBlack = 19, expectedWhite = 121 }));
            Check(black < 60 && white > black + 50 && white < 190, "Solid tint did not composite against the owned black/white lower surfaces");
            // A paired counterexample proves the pixel assertion detects the old XAML-over-white failure.
            upper.SystemBackdrop = null;
            root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(153, 32, 32, 32));
            lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
            await Task.Delay(180);
            int oldBlack = Read();
            lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
            await Task.Delay(180);
            int oldWhite = Read();
            log("ticket16-solid-counterexample: " + JsonSerializer.Serialize(new { oldBlack, oldWhite }));
            Check(!(oldBlack < 60 && oldWhite > oldBlack + 50 && oldWhite < 190), "Paired old XAML path did not distinguish the regression");
            root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (DesktopAcrylicController.IsSupported())
            {
                var observations = new List<(double Requested, float Actual, string State)>();
                foreach (double opacity in new[] { 0.2, 0.8 })
                {
                    upper.SystemBackdrop = new ConfiguredMaterialBackdrop(MaterialKind.Acrylic, opacity, (kind, value) => log(kind + ": " + JsonSerializer.Serialize(value)));
                    await Task.Delay(180);
                    var acrylicField = typeof(ConfiguredMaterialBackdrop).GetField("acrylic", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    var acrylic = (DesktopAcrylicController)acrylicField.GetValue(upper.SystemBackdrop)!;
                    observations.Add((opacity, acrylic.TintOpacity, acrylic.State.ToString()));
                    Check(Math.Abs(acrylic.TintOpacity - opacity) < 0.01, "Acrylic configuration overwrote the requested tint strength");
                }
                log("ticket16-acrylic-native: " + JsonSerializer.Serialize(observations.Select(x => new { x.Requested, x.Actual, x.State, visual = "pending-human" })));
                var active = (ConfiguredMaterialBackdrop)upper.SystemBackdrop!;
                var field = typeof(ConfiguredMaterialBackdrop).GetField("acrylic", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var controller = (DesktopAcrylicController)field.GetValue(active)!;
                var darkTint = controller.TintColor;
                root.RequestedTheme = ElementTheme.Light;
                await Task.Delay(120);
                controller = (DesktopAcrylicController)field.GetValue(active)!;
                var lightTint = controller.TintColor;
                log("followup-acrylic-theme-colors: " + JsonSerializer.Serialize(new { darkTint = darkTint.ToString(), lightTint = lightTint.ToString(), controller.TintOpacity }));
                Check(lightTint.R > darkTint.R + 60 && Math.Abs(controller.TintOpacity - .8) < .01, "Customized acrylic did not update native light theme colors while preserving strength");
                root.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(80);
                controller = (DesktopAcrylicController)field.GetValue(active)!;
                Check(controller.TintColor.R == darkTint.R, "Customized acrylic did not restore native dark colors");

            }
            if (MicaController.IsSupported())
            {
                var backdrop = new ConfiguredMaterialBackdrop(MaterialKind.Mica, .6, (kind, value) => log(kind + ": " + JsonSerializer.Serialize(value)));
                upper.SystemBackdrop = backdrop;
                await Task.Delay(120);
                var micaField = typeof(ConfiguredMaterialBackdrop).GetField("mica", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var darkMica = ((MicaController)micaField.GetValue(backdrop)!).TintColor;
                root.RequestedTheme = ElementTheme.Light;
                await Task.Delay(120);
                var lightMica = (MicaController)micaField.GetValue(backdrop)!;
                Check(lightMica.TintColor.R > darkMica.R + 60 && Math.Abs(lightMica.LuminosityOpacity - .6) < .01, "Customized mica lost native light palette or requested strength");
                log("followup-mica-theme-colors: " + JsonSerializer.Serialize(new { dark = darkMica.ToString(), light = lightMica.TintColor.ToString(), lightMica.LuminosityOpacity }));
                root.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(80);
                Check(((MicaController)micaField.GetValue(backdrop)!).TintColor.R == darkMica.R, "Mica did not restore native dark colors");
            }
            // Ordinary framed windows must preserve tint without stripping native chrome.
            ((OverlappedPresenter)upper.AppWindow.Presenter).SetBorderAndTitleBar(true, true);
            lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
            upper.SystemBackdrop = new SolidTintBackdrop(upper, Windows.UI.Color.FromArgb(153, 32, 32, 32), (kind, data) => log(kind + ": " + JsonSerializer.Serialize(data)));
            await Task.Delay(200);
            int framedBlack = Read();
            lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
            await Task.Delay(150);
            int framedWhite = Read();
            Check(framedBlack < 60 && framedWhite > framedBlack + 50, "Framed settings surface did not preserve background alpha");
            rect.Width = 500; rect.Height = 280;
            lower.AppWindow.MoveAndResize(rect); upper.AppWindow.MoveAndResize(rect);
            lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
            await Task.Delay(200);
            int resizedBlack = Read();
            lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
            await Task.Delay(150);
            int resizedWhite = Read();
            Check(resizedBlack < 60 && resizedWhite > resizedBlack + 50, "Resizing lost alpha in newly exposed surface area");
            lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
            root.RequestedTheme = ElementTheme.Light;
            await Task.Delay(200);
            int light = Read();
            log("ticket16-native-theme-resize: " + JsonSerializer.Serialize(new { resizedBlack, resizedWhite, light, theme = root.ActualTheme.ToString() }));
            Check(light > 100 && light < 190, "Existing solid backdrop did not follow a live light theme change");
            upper.Close(); upperClosed = true;
            var component = HostComponentDisplayModel.From(new Component(new StableIdentity(new StableId("material-fixture")), CapabilityState.Available));
            var island = new ContentIslandHost((kind, data) => log(kind + ": " + JsonSerializer.Serialize(data)));
            var client = new PixelRect(rect.X + 24, rect.Y + 64, 300, 110);
            try
            {
                island.Start(lowerHandle, client, component, new("none", 1, "dark"));
                island.ApplyAppearance(new(HostTheme.Dark, MaterialKind.Solid, .6));
                lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
                await Task.Delay(200);
                int islandBlack = Sample(client.X + client.Width - 20, client.Y + client.Height - 20);
                lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
                await Task.Delay(150);
                int islandWhite = Sample(client.X + client.Width - 20, client.Y + client.Height - 20);
                log("ticket16-island-native-pixels: " + JsonSerializer.Serialize(new { islandBlack, islandWhite, framedBlack, framedWhite }));
                Check(islandBlack < 60 && islandWhite > islandBlack + 50, "Content island tint did not preserve alpha against its owned parent");
                lowerRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
                island.ApplyAppearance(new(HostTheme.Light, MaterialKind.Solid, .6));
                await Task.Delay(180);
                int islandLight = Sample(client.X + client.Width - 20, client.Y + client.Height - 20);
                Check(islandLight > 100 && islandLight < 190 && island.IsAlive, "Existing island did not follow light theme without replacement");
                log("ticket16-island-live-theme: " + islandLight);
            }
            finally { island.Close(); }
            log("PASS: ticket16 native solid pixel pair/counterexample and acrylic request/actual strength; visual acceptance remains human.");
        }
        finally { if (!upperClosed) upper.Close(); lower.Close(); }

        int Read()
        {
            var point = new FlyoutNative.Point { X = rect.X + rect.Width - 30, Y = rect.Y + rect.Height - 30 };
            Check(FlyoutNative.Root(FlyoutNative.WindowFromPoint(point)) == upperHandle, "Owned material sample is occluded");
            return Sample(point.X, point.Y);
        }
        int Sample(int x, int y)
        {
            nint dc = GetDC(0);
            try { uint pixel = GetPixel(dc, x, y); Check(pixel != uint.MaxValue, "Cannot read native material pixel"); return (int)(pixel & 255); }
            finally { ReleaseDC(0, dc); }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
}

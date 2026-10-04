using System.Text.Json;
using Microsoft.UI.Xaml;
using Mtp.Host.Islands;
using Mtp.Platform.Core;

namespace Mtp.Host.WindowTests;

internal static class PreviewStartupRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        var component = HostComponentDisplayModel.From(new Component(new StableIdentity(new StableId("preview-first-show")), CapabilityState.Available));
        var owner = new Window();
        var preview = new IslandPreviewWindow();
        var lines = new List<string>();
        var failures = new List<string>();
        preview.DiagnosticObserver = (kind, value) => lines.Add(JsonSerializer.Serialize(new { kind, value }));
        try
        {
            var displays = new Win32TaskbarDockEnvironment().GetDisplays();
            // Deliberately do not render-to-bitmap or force layout before checking cold presentation.
            foreach (var display in displays)
            {
                owner.AppWindow.Move(new(display.WorkArea.X + 100, display.WorkArea.Y + 100));
                await Task.Delay(150);
                foreach (var material in new[] { "none", "acrylic" })
                {
                    var startEvent = lines.Count;
                    preview.Open(WinRT.Interop.WindowNative.GetWindowHandle(owner), component, new(material, 1, Controls: true));
                    await Task.Delay(800);
                    lines.Add(JsonSerializer.Serialize(new { kind = "cold-settled-" + material, value = preview.PresentationSnapshot() }));
                    var root = preview.ContentRoot!;
                    log($"STARTUP {display.Id} {material}: loaded={root.IsLoaded} xamlVisible={root.XamlRoot?.IsHostVisible} layout={root.ActualWidth}x{root.ActualHeight} dpi={NativeWindows.GetDpiForWindow(preview.Handle)}");
                    if (!root.IsLoaded || root.XamlRoot?.IsHostVisible != true || root.ActualWidth <= 0 || root.ActualHeight <= 0)
                        failures.Add(material + ": Cold preview has no visible loaded layout before any move/resize.");
                    if (material == "acrylic")
                    {
                        var initialized = lines.Skip(startEvent).Select(line => JsonDocument.Parse(line)).ToArray();
                        try
                        {
                            var applied = initialized.Any(item => item.RootElement.GetProperty("kind").GetString() == "preview-backdrop-initialized" &&
                                item.RootElement.GetProperty("value").GetProperty("hresult").GetInt32() == 0);
                            log("STARTUP acrylic: owned root initialization succeeded=" + applied + "; visual result pending human");
                            if (!applied) failures.Add("Preview omitted host backdrop initialization on its own top-level HWND.");
                        }
                        finally { foreach (var item in initialized) item.Dispose(); }
                        foreach (var destination in displays.Where(d => d.Id != display.Id))
                        {
                            var rect = NativeWindows.WindowBounds(preview.Handle);
                            NativeWindows.Place(preview.Handle, destination.WorkArea.X + 140, destination.WorkArea.Y + 160, rect.Right - rect.Left, rect.Bottom - rect.Top);
                            await Task.Delay(800);
                            lines.Add(JsonSerializer.Serialize(new { kind = "cross-monitor-settled", value = preview.PresentationSnapshot() }));
                            log($"CROSS {display.Id}->{destination.Id}: alive={preview.IsAlive} visible={preview.ContentRoot?.XamlRoot?.IsHostVisible} dpi={NativeWindows.GetDpiForWindow(preview.Handle)}");
                            if (!preview.IsAlive || preview.ContentRoot?.XamlRoot?.IsHostVisible != true || NativeWindows.GetDpiForWindow(preview.Handle) != destination.Dpi)
                                failures.Add("Cross-monitor preview did not settle at destination DPI with a visible island.");
                        }
                    }
                    preview.Close();
                }
            }
            if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
            log("PASS: cold preview startup on connected displays and native cross-monitor visibility; no forced layout/bitmap before assertions. Appearance remains pending human.");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "preview-startup.jsonl"), lines);
            preview.Close();
            owner.Close();
        }
    }
}

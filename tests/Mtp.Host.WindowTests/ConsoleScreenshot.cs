using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace Mtp.Host.WindowTests;

internal static class ConsoleScreenshot
{
    internal static async Task SaveAsync(Window window, string path)
        => await SaveAsync((FrameworkElement)window.Content, path);

    internal static async Task SaveAsync(FrameworkElement content, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(content);
        var buffer = await bitmap.GetPixelsAsync();
        using var stream = new FileStream(path, FileMode.Create);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, buffer.ToArray());
        await encoder.FlushAsync();
    }
}

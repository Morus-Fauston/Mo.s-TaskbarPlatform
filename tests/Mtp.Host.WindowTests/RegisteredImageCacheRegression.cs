using Mtp.Contracts;
using Mtp.Host.Templates;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mtp.Host.WindowTests;

internal static class RegisteredImageCacheRegression
{
    public static async Task RunAsync(Action<string> log)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mtp-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var leases = new List<ImageLease>();
        using var cache = new RegisteredImageCache();
        try
        {
            string png = Path.Combine(directory, "registered.png");
            await WritePngAsync(png, 1024, 1024);
            Check(cache.Register("images", "image-0", ImageResourceFormat.Png, png).Accepted, "Host local image registration failed");
            var first = await cache.LoadAsync("images", "image-0");
            Check(first.Lease is not null && first.ErrorCode is null, "Valid bounded image failed to decode: " + first.ErrorCode);
            leases.Add(first.Lease!);
            Check(first.Lease!.Source.PixelWidth == 1024 && first.Lease.Source.PixelHeight == 1024, "Decoded image dimensions differ");
            var same = await cache.LoadAsync("images", "image-0");
            Check(ReferenceEquals(first.Lease.Source, same.Lease!.Source), "Same registered image was decoded twice");
            same.Lease.Dispose();
            same.Lease.Dispose();
            for (int i = 1; i < 4; i++)
            {
                Check(cache.Register("images", "image-" + i, ImageResourceFormat.Png, png).Accepted, "Image registration failed");
                var loaded = await cache.LoadAsync("images", "image-" + i);
                Check(loaded.Lease is not null, "Four 4 MiB images must fit the 16 MiB budget: " + loaded.ErrorCode);
                leases.Add(loaded.Lease!);
            }
            Check(cache.Register("images", "image-4", ImageResourceFormat.Png, png).Accepted, "Fifth image registration failed");
            Check((await cache.LoadAsync("images", "image-4")).ErrorCode == "ImageCacheBudgetExceeded", "Live image leases were evicted to bypass decoded budget");
            leases[0].Dispose();
            var afterRelease = await cache.LoadAsync("images", "image-4");
            Check(afterRelease.Lease is not null, "Unleased image was not eligible for bounded eviction");
            leases.Add(afterRelease.Lease!);

            Check((await cache.LoadAsync("other", "image-0")).ErrorCode == "ImageNotRegistered", "Resource crossed application ownership");
            Check(!cache.Register("images", "remote", ImageResourceFormat.Png, "https://example.test/image.png").Accepted, "URL registration accepted");
            Check(!cache.Register("images", "relative", ImageResourceFormat.Png, "relative.png").Accepted, "Relative registration accepted");
            Check(!cache.Register("images", "image-0", ImageResourceFormat.Jpeg, png).Accepted, "Registered identity was replaced");
            for (int i = 5; i < 64; i++) Check(cache.Register("images", "image-" + i, ImageResourceFormat.Png, png).Accepted, "64 registration budget rejected too soon");
            Check(cache.Register("images", "image-64", ImageResourceFormat.Png, png).Code == "ImageResourceBudgetExceeded", "Image count budget exceeded");
            for (int i = 1; i < 16; i++) Check(cache.Register("app-" + i, "image", ImageResourceFormat.Png, png).Accepted, "16 application budget rejected too soon");
            Check(cache.Register("app-16", "image", ImageResourceFormat.Png, png).Code == "ImageApplicationBudgetExceeded", "Application count budget exceeded");

            using var invalid = new RegisteredImageCache();
            string oversized = Path.Combine(directory, "oversized.png");
            await File.WriteAllBytesAsync(oversized, new byte[TemplateLimits.EncodedImageBytes + 1]);
            Check(invalid.Register("invalid", "large", ImageResourceFormat.Png, oversized).Accepted, "Trusted path could not be registered");
            Check((await invalid.LoadAsync("invalid", "large")).ErrorCode == "ImageEncodingBudgetExceeded", "Encoded image budget was bypassed");
            Check(invalid.Register("invalid", "mismatch", ImageResourceFormat.Jpeg, png).Accepted, "Mismatch registration failed");
            Check((await invalid.LoadAsync("invalid", "mismatch")).ErrorCode == "InvalidImageHeader", "Format mismatch reached native decoding");
            string wide = Path.Combine(directory, "wide.png");
            await WritePngAsync(wide, 1025, 1);
            Check(invalid.Register("invalid", "wide", ImageResourceFormat.Png, wide).Accepted, "Wide image registration failed");
            Check((await invalid.LoadAsync("invalid", "wide")).ErrorCode == "ImageDimensionsExceeded", "Image edge budget was bypassed");
            string truncated = Path.Combine(directory, "truncated.png");
            var pngBytes = await File.ReadAllBytesAsync(png);
            await File.WriteAllBytesAsync(truncated, pngBytes[..33]);
            Check(invalid.Register("invalid", "truncated", ImageResourceFormat.Png, truncated).Accepted, "Truncated image registration failed");
            Check((await invalid.LoadAsync("invalid", "truncated")).Lease is null, "Header-only PNG was mistaken for a decoded image");
            string jpeg = Path.Combine(directory, "registered.jpg");
            await WriteImageAsync(jpeg, 16, 8, BitmapEncoder.JpegEncoderId);
            Check(invalid.Register("invalid", "jpeg", ImageResourceFormat.Jpeg, jpeg).Accepted, "JPEG image registration failed");
            var jpegResult = await invalid.LoadAsync("invalid", "jpeg");
            Check(jpegResult.Lease is not null && jpegResult.Lease.Source.PixelWidth == 16 && jpegResult.Lease.Source.PixelHeight == 8,
                "Valid JPEG did not decode to expected dimensions: " + jpegResult.ErrorCode);
            jpegResult.Lease!.Dispose();
            string missing = Path.Combine(directory, "missing.png");
            Check(invalid.Register("invalid", "missing", ImageResourceFormat.Png, missing).Accepted, "Missing registered resource failed registration");
            Check((await invalid.LoadAsync("invalid", "missing")).ErrorCode == "ImageLoadFailed", "Missing image did not return a bounded placeholder reason");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await invalid.LoadAsync("invalid", "mismatch", cancelled.Token); throw new InvalidOperationException("Cancelled image loaded"); }
            catch (OperationCanceledException) { }
            invalid.Dispose();
            invalid.Dispose();
            Check((await invalid.LoadAsync("invalid", "missing")).ErrorCode == "ImageCacheDisposed", "Closed cache accepted work");
            log("PASS: registered images decode to exact dimensions; lease-aware 16MiB budget; 64 resources/16 apps; 4MiB encoded bound; format/path/ownership rejection; cancellation and idempotent disposal.");
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
            cache.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private static Task WritePngAsync(string path, uint width, uint height) => WriteImageAsync(path, width, height, BitmapEncoder.PngEncoderId);

    private static async Task WriteImageAsync(string path, uint width, uint height, Guid encoderId)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        byte[] pixels = new byte[checked((int)(width * height * 4))];
        Array.Fill(pixels, (byte)255);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        byte[] bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

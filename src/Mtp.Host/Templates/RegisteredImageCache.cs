using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Mtp.Contracts;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mtp.Host.Templates;

internal sealed record RegisteredImageResult(ImageLease? Lease, string? ErrorCode);

internal sealed class ImageLease(BitmapSource source, Action release) : IDisposable
{
    private Action? release = release;
    private BitmapSource? source = source;
    public BitmapSource Source => source ?? throw new ObjectDisposedException(nameof(ImageLease));
    public void Dispose()
    {
        source = null;
        Interlocked.Exchange(ref release, null)?.Invoke();
    }
}

/// <summary>UI-thread-owned local registrations and leased bitmaps. No paths arrive from template DTOs.</summary>
internal sealed class RegisteredImageCache : IDisposable
{
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("ImageCacheRequiresUiThread");
    private readonly Dictionary<string, ApplicationImages> applications = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private int activeLoads;
    private long lastUse;
    private bool disposed;

    public ProtocolResult Register(string applicationId, string resourceId, ImageResourceFormat format, string trustedPath)
    {
        VerifyAccess();
        if (disposed) return ProtocolResult.Reject("ImageCacheDisposed", "图像资源已关闭");
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 256 || string.IsNullOrWhiteSpace(resourceId) || resourceId.Length > 256 ||
            !Enum.IsDefined(format) || string.IsNullOrWhiteSpace(trustedPath) || !Path.IsPathFullyQualified(trustedPath) ||
            trustedPath.StartsWith("\\\\", StringComparison.Ordinal) || trustedPath.Contains("://", StringComparison.Ordinal) ||
            trustedPath.AsSpan(2).Contains(':'))
            return ProtocolResult.Reject("InvalidImageRegistration", "图像必须来自Host登记的本地只读资源");
        string path;
        try { path = Path.GetFullPath(trustedPath); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return ProtocolResult.Reject("InvalidImageRegistration", "图像路径无效"); }
        if (!applications.TryGetValue(applicationId, out var app))
        {
            if (applications.Count == 16) return ProtocolResult.Reject("ImageApplicationBudgetExceeded", "图像应用数量超限");
            applications.Add(applicationId, app = new());
        }
        if (app.Registrations.TryGetValue(resourceId, out var previous))
            return previous.Format == format && StringComparer.OrdinalIgnoreCase.Equals(previous.Path, path)
                ? ProtocolResult.Success() : ProtocolResult.Reject("ImageAlreadyRegistered", "资源身份已登记");
        if (app.Registrations.Count == TemplateLimits.ImagesPerApplication)
            return ProtocolResult.Reject("ImageResourceBudgetExceeded", "图像数量超限");
        app.Registrations.Add(resourceId, new(format, path));
        return ProtocolResult.Success();
    }

    public async Task<RegisteredImageResult> LoadAsync(string applicationId, string resourceId, CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        if (disposed) return new(null, "ImageCacheDisposed");
        cancellationToken.ThrowIfCancellationRequested();
        if (!applications.TryGetValue(applicationId, out var app) || !app.Registrations.TryGetValue(resourceId, out var registration))
            return new(null, "ImageNotRegistered");
        if (app.Cache.TryGetValue(resourceId, out var cached)) return Lease(cached);
        if (activeLoads >= 2 || !app.Loading.Add(resourceId)) return new(null, "ImageLoaderBusy");
        activeLoads++;
        int reserved = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            byte[] encoded;
            using (var file = new FileStream(registration.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (file.Length is <= 0 or > TemplateLimits.EncodedImageBytes) return new(null, "ImageEncodingBudgetExceeded");
                encoded = new byte[(int)file.Length];
                await file.ReadExactlyAsync(encoded, linked.Token);
                var extra = new byte[1];
                if (await file.ReadAsync(extra, linked.Token) != 0) return new(null, "ImageEncodingBudgetExceeded");
            }
            linked.Token.ThrowIfCancellationRequested();
            var header = ImageResourceValidation.Validate(encoded, registration.Format);
            if (!header.Accepted) return new(null, header.Code);
            int decodedBytes = checked(header.Width * header.Height * 4);
            while (app.DecodedBytes + decodedBytes > TemplateLimits.DecodedImageBytesPerApplication)
            {
                var unused = app.Cache.Where(pair => pair.Value.References == 0).OrderBy(pair => pair.Value.LastUse).FirstOrDefault();
                if (unused.Value is null) return new(null, "ImageCacheBudgetExceeded");
                app.Cache.Remove(unused.Key);
                app.DecodedBytes -= unused.Value.Bytes;
            }
            app.DecodedBytes += reserved = decodedBytes;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(encoded);
                await writer.StoreAsync().AsTask(linked.Token);
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(linked.Token);
            if (decoder.PixelWidth != header.Width || decoder.PixelHeight != header.Height || decoder.FrameCount != 1)
                return new(null, "ImageDecodedDimensionsMismatch");
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream).AsTask(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (bitmap.PixelWidth != header.Width || bitmap.PixelHeight != header.Height)
                return new(null, "ImageDecodedDimensionsMismatch");
            var entry = new CachedImage(bitmap, decodedBytes) { LastUse = ++lastUse };
            app.Cache.Add(resourceId, entry);
            reserved = 0;
            return Lease(entry);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return new(null, "ImageLoadCancelled"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException)
        { return new(null, "ImageLoadFailed"); }
        finally
        {
            app.DecodedBytes -= reserved;
            app.Loading.Remove(resourceId);
            activeLoads--;
        }
    }

    private RegisteredImageResult Lease(CachedImage image)
    {
        image.References++;
        image.LastUse = ++lastUse;
        return new(new ImageLease(image.Source, () => { VerifyAccess(); image.References--; }), null);
    }

    public void Dispose()
    {
        VerifyAccess();
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        applications.Clear();
        lifetime.Dispose();
    }

    private void VerifyAccess()
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("ImageCacheRequiresUiThread");
    }

    private sealed record Registration(ImageResourceFormat Format, string Path);
    private sealed class CachedImage(BitmapSource source, int bytes)
    {
        public BitmapSource Source { get; } = source;
        public int Bytes { get; } = bytes;
        public int References { get; set; }
        public long LastUse { get; set; }
    }
    private sealed class ApplicationImages
    {
        public Dictionary<string, Registration> Registrations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CachedImage> Cache { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Loading { get; } = new(StringComparer.Ordinal);
        public int DecodedBytes { get; set; }
    }
}

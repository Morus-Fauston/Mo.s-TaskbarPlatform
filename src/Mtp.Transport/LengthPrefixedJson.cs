using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mtp.Transport;

/// <summary>Frames one JSON value per call. The caller owns and serializes access to the stream.</summary>
public static class LengthPrefixedJson
{
    public const int MaximumPayloadBytes = 1_048_576;
    public static readonly TimeSpan DefaultFrameTimeout = TimeSpan.FromSeconds(5);
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        TimeSpan duration = ValidateTimeout(timeout, allowInfiniteIdle: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (duration != Timeout.InfiniteTimeSpan) deadline.CancelAfter(duration);
        try
        {
            byte[] header = new byte[4];
            int first = await stream.ReadAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false);
            if (first == 0) throw new ProtocolException("end_of_stream");
            if (duration == Timeout.InfiniteTimeSpan) deadline.CancelAfter(DefaultFrameTimeout);
            await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is <= 0 or > MaximumPayloadBytes) throw new ProtocolException("invalid_length");
            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
            _ = new UTF8Encoding(false, true).GetCharCount(payload);
            ValidateJson(payload);
            T result = JsonSerializer.Deserialize<T>(payload, Options) ?? throw new ProtocolException("invalid_json");
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new ProtocolException("frame_timeout", error);
        }
        catch (EndOfStreamException error)
        {
            throw new ProtocolException("truncated_frame", error);
        }
        catch (DecoderFallbackException error)
        {
            throw new ProtocolException("invalid_utf8", error);
        }
        catch (JsonException error)
        {
            throw new ProtocolException("invalid_json", error);
        }
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        TimeSpan duration = ValidateTimeout(timeout, allowInfiniteIdle: false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        try
        {
            if (value is null) throw new ProtocolException("invalid_json");
            using var payload = new BoundedPayloadStream();
            await JsonSerializer.SerializeAsync(payload, value, Options, deadline.Token).ConfigureAwait(false);
            ValidateJson(payload.GetBuffer().AsSpan(0, (int)payload.Length));
            deadline.Token.ThrowIfCancellationRequested();
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, (int)payload.Length);
            await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload.GetBuffer().AsMemory(0, (int)payload.Length), deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new ProtocolException("frame_timeout", error);
        }
        catch (JsonException error)
        {
            throw new ProtocolException("invalid_json", error);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            MaxDepth = 32,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void ValidateJson(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 32 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!objects.Peek().Add(reader.GetString()!))
                    {
                        throw new JsonException("Duplicate property.");
                    }
                    break;
                case JsonTokenType.Number:
                    if (!reader.TryGetDouble(out double number) || !double.IsFinite(number))
                    {
                        throw new JsonException("Non-finite number.");
                    }
                    break;
            }
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout, bool allowInfiniteIdle)
    {
        TimeSpan duration = timeout ?? DefaultFrameTimeout;
        if (allowInfiniteIdle && duration == Timeout.InfiniteTimeSpan) return duration;
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        return duration;
    }

    private sealed class BoundedPayloadStream() : MemoryStream(4096)
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void CheckCapacity(int count)
        {
            if (count > MaximumPayloadBytes - Position) throw new ProtocolException("invalid_length");
        }
    }
}

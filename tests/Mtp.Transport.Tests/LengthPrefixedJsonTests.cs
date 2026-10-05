using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace Mtp.Transport.Tests;

public sealed class LengthPrefixedJsonTests
{
    [Fact]
    public async Task WritesLittleEndianLengthAndCamelCaseJsonAndReadsSuccessiveFrames()
    {
        using var stream = new MemoryStream();
        await LengthPrefixedJson.WriteAsync(stream, new Message("ok", State.Ready));
        var payload = Encoding.UTF8.GetBytes("{\"text\":\"ok\",\"state\":\"ready\"}");
        Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()));
        Assert.Equal(payload, stream.ToArray()[4..]);
        await LengthPrefixedJson.WriteAsync(stream, new Message("next", State.Ready));
        stream.Position = 0;
        Assert.Equal(new Message("ok", State.Ready), await LengthPrefixedJson.ReadAsync<Message>(stream));
        Assert.Equal(new Message("next", State.Ready), await LengthPrefixedJson.ReadAsync<Message>(stream));
        Assert.True(stream.CanRead);
    }

    public sealed record Message(string Text, State State);
    public enum State { Ready }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_048_577)]
    [InlineData(int.MaxValue)]
    public async Task RejectsInvalidLengthBeforeReadingPayload(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => LengthPrefixedJson.ReadAsync<Message>(stream));
        Assert.Equal("invalid_length", error.Code);
    }

    [Theory]
    [InlineData("{\"text\":\"first\",\"text\":\"second\",\"state\":\"ready\"}")]
    [InlineData("{\"text\":\"first\",\"\\u0074ext\":\"second\",\"state\":\"ready\"}")]
    [InlineData("{\"text\":\"ok\",\"state\":\"ready\",\"extra\":true}")]
    [InlineData("{\"text\":\"ok\",\"state\":0}")]
    [InlineData("{\"text\":\"ok\",\"state\":\"unknown\"}")]
    [InlineData("null")]
    [InlineData("{\"text\":\"ok\",\"state\":\"ready\",}")]
    public async Task RejectsAmbiguousOrUnsupportedJson(string json)
    {
        using var stream = Frame(Encoding.UTF8.GetBytes(json));
        var error = await Assert.ThrowsAsync<ProtocolException>(() => LengthPrefixedJson.ReadAsync<Message>(stream));
        Assert.Equal("invalid_json", error.Code);
    }

    [Fact]
    public async Task RejectsInvalidUtf8RatherThanReplacingIt()
    {
        using var stream = Frame([.. Encoding.UTF8.GetBytes("{\"text\":\""), 0xff, .. Encoding.UTF8.GetBytes("\",\"state\":\"ready\"}")]);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => LengthPrefixedJson.ReadAsync<Message>(stream));
        Assert.Equal("invalid_utf8", error.Code);
    }

    private static MemoryStream Frame(byte[] payload)
    {
        var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task RejectsExcessDepthAndNonFiniteNumbersEvenInUntypedPayloads()
    {
        foreach (string json in new[] { new string('[', 33) + "0" + new string(']', 33), "{\"value\":1e999}", "{\"nested\":{\"x\":1,\"x\":2}}" })
        {
            using var stream = Frame(Encoding.UTF8.GetBytes(json));
            Assert.Equal("invalid_json", (await Assert.ThrowsAsync<ProtocolException>(() => LengthPrefixedJson.ReadAsync<System.Text.Json.JsonElement>(stream))).Code);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task ClosedOrTruncatedFrameHasExplicitErrorAndLeavesStreamOwnedByCaller(int count)
    {
        using var whole = Frame(Encoding.UTF8.GetBytes("{\"text\":\"ok\",\"state\":\"ready\"}"));
        using var partial = new MemoryStream(whole.ToArray()[..count]);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => LengthPrefixedJson.ReadAsync<Message>(partial));
        Assert.Equal(count == 0 ? "end_of_stream" : "truncated_frame", error.Code);
        Assert.True(partial.CanRead);
    }

    [Fact]
    public async Task ReadDeadlineCoversTheWholeFrameInsteadOfResettingForEachByte()
    {
        using var frame = Frame(Encoding.UTF8.GetBytes("{\"text\":\"ok\",\"state\":\"ready\"}"));
        using var slow = new DelayedStream(frame.ToArray(), TimeSpan.FromMilliseconds(25));
        var error = await Assert.ThrowsAsync<ProtocolException>(() => LengthPrefixedJson.ReadAsync<Message>(slow, timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Equal("frame_timeout", error.Code);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        using var stream = new DelayedStream([], Timeout.InfiniteTimeSpan);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LengthPrefixedJson.ReadAsync<Message>(stream, cancellation.Token));
    }

    [Fact]
    public async Task InfiniteIdleWaitStillBoundsAnIncomingPartialFrame()
    {
        using var stream = new DelayedStream([12], TimeSpan.Zero, stallWhenEmpty: true);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => LengthPrefixedJson.ReadAsync<Message>(stream, timeout: Timeout.InfiniteTimeSpan));
        Assert.Equal("frame_timeout", error.Code);
    }

    [Fact]
    public async Task OversizedOutputIsRejectedBeforeAnyBytesAreWritten()
    {
        using var stream = new MemoryStream();
        var error = await Assert.ThrowsAsync<ProtocolException>(() => LengthPrefixedJson.WriteAsync(stream, new Message(new string('a', 1_048_576), State.Ready)));
        Assert.Equal("invalid_length", error.Code);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task SlowWriteTimesOutAndUnboundedWriteIsRejected()
    {
        using var stream = new DelayedStream([], Timeout.InfiniteTimeSpan);
        Assert.Equal("frame_timeout", (await Assert.ThrowsAsync<ProtocolException>(() => LengthPrefixedJson.WriteAsync(stream, new Message("ok", State.Ready), timeout: TimeSpan.FromMilliseconds(50)))).Code);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LengthPrefixedJson.WriteAsync(stream, "ok", timeout: Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void SharedSerializerOptionsCannotBeMutated()
    {
        Assert.Throws<InvalidOperationException>(() => LengthPrefixedJson.Options.MaxDepth = 100);
    }

    [Fact]
    public async Task AcceptsTheExactOneMiBPayloadBoundary()
    {
        string value = new('a', 1_048_574);
        using var stream = new MemoryStream();
        await LengthPrefixedJson.WriteAsync(stream, value);
        Assert.Equal(1_048_580, stream.Length);
        stream.Position = 0;
        Assert.Equal(value, await LengthPrefixedJson.ReadAsync<string>(stream));
    }

    [Fact]
    public async Task ActualCurrentUserNamedPipeTransfersFragmentedFramesAndCancelsIdleRead()
    {
        string name = "mtp-transport-tests-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await accept;
        using var frame = Frame(Encoding.UTF8.GetBytes("{\"text\":\"named-pipe\",\"state\":\"ready\"}"));
        var read = LengthPrefixedJson.ReadAsync<Message>(server, deadline.Token);
        await client.WriteAsync(frame.ToArray().AsMemory(0, 2), deadline.Token);
        await client.WriteAsync(frame.ToArray().AsMemory(2), deadline.Token);
        Assert.Equal(new Message("named-pipe", State.Ready), await read);
        var acknowledgment = LengthPrefixedJson.ReadAsync<Message>(client, deadline.Token);
        await LengthPrefixedJson.WriteAsync(server, new Message("ack", State.Ready), deadline.Token);
        Assert.Equal(new Message("ack", State.Ready), await acknowledgment);
        using var idleCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LengthPrefixedJson.ReadAsync<Message>(server, idleCancellation.Token, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task LongLivedPipeCanRemainIdleBeyondTheDefaultFrameDeadline()
    {
        string name = "mtp-transport-idle-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var accept = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await accept;
        var read = LengthPrefixedJson.ReadAsync<Message>(server, deadline.Token, Timeout.InfiniteTimeSpan);
        await Task.Delay(TimeSpan.FromMilliseconds(5200), deadline.Token);
        await LengthPrefixedJson.WriteAsync(client, new Message("after-idle", State.Ready), deadline.Token);
        Assert.Equal(new Message("after-idle", State.Ready), await read);
    }

    private sealed class DelayedStream(byte[] data, TimeSpan delay, bool stallWhenEmpty = false) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(position == data.Length && stallWhenEmpty ? Timeout.InfiniteTimeSpan : delay, cancellationToken);
            if (position == data.Length) return 0;
            buffer.Span[0] = data[position++];
            return 1;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => await Task.Delay(delay, cancellationToken);
    }
}

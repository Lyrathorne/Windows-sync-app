using System.Buffers.Binary;
using System.Text;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Protocol.Tests;

public sealed class ProtocolFrameTests
{
    [Fact]
    public async Task Writer_WritesBigEndianByteLength()
    {
        await using var stream = new MemoryStream();
        var writer = new ProtocolFrameWriter(stream);

        await writer.WriteAsync(TestMessages.AndroidHello());

        var bytes = stream.ToArray();
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, 4));
        Assert.Equal(bytes.Length - 4, length);
    }

    [Fact]
    public async Task Reader_ReadsPartialChunks()
    {
        var frame = await FrameBytesAsync(TestMessages.AndroidHello());
        await using var stream = new ChunkedReadStream(frame, 2);
        var reader = new ProtocolFrameReader(stream);

        var message = await reader.ReadAsync();

        Assert.Equal(ProtocolMessageTypes.ConnectionHello, message.Type);
    }

    [Fact]
    public async Task UnicodeLength_IsMeasuredInBytes()
    {
        var message = TestMessages.AndroidHello("android-unicode", "Pixel");
        var json = ProtocolSerializer.Serialize(message);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        var frame = await FrameBytesAsync(message);

        Assert.Equal(byteCount, BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(0, 4)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ProtocolConstants.MaxJsonMessageSize + 1)]
    public async Task Reader_RejectsInvalidLength(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        await using var stream = new MemoryStream(header);
        var reader = new ProtocolFrameReader(stream);

        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Reader_DetectsEofInsideHeader()
    {
        await using var stream = new MemoryStream([0, 0]);
        var reader = new ProtocolFrameReader(stream);

        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Reader_DetectsEofInsideJson()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, 12);
        await using var stream = new MemoryStream(header.Concat(Encoding.UTF8.GetBytes("{}")).ToArray());
        var reader = new ProtocolFrameReader(stream);

        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Reader_ReadsTwoConsecutiveFrames()
    {
        var first = await FrameBytesAsync(TestMessages.AndroidHello("android-1"));
        var second = await FrameBytesAsync(TestMessages.AndroidPing());
        await using var stream = new MemoryStream(first.Concat(second).ToArray());
        var reader = new ProtocolFrameReader(stream);

        Assert.Equal(ProtocolMessageTypes.ConnectionHello, (await reader.ReadAsync()).Type);
        Assert.Equal(ProtocolMessageTypes.ConnectionPing, (await reader.ReadAsync()).Type);
    }

    private static async Task<byte[]> FrameBytesAsync(ProtocolMessage message)
    {
        await using var stream = new MemoryStream();
        await new ProtocolFrameWriter(stream).WriteAsync(message);
        return stream.ToArray();
    }
}

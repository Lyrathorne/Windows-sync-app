namespace DeviceSync.Protocol.Tests;

internal sealed class ChunkedReadStream : MemoryStream
{
    private readonly int _chunkSize;

    public ChunkedReadStream(byte[] buffer, int chunkSize)
        : base(buffer)
    {
        _chunkSize = chunkSize;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkSize)], cancellationToken);
    }
}

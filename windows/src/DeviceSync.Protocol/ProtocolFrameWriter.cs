using System.Buffers.Binary;
using System.Text;

namespace DeviceSync.Protocol;

public sealed class ProtocolFrameWriter
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ProtocolFrameWriter(Stream stream)
    {
        _stream = stream;
    }

    public async Task WriteAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        var payload = Encoding.UTF8.GetBytes(ProtocolSerializer.Serialize(message));
        if (payload.Length <= 0 || payload.Length > ProtocolConstants.MaxJsonMessageSize)
        {
            throw new ProtocolException("Frame payload exceeds the maximum JSON message size.");
        }

        var header = new byte[ProtocolConstants.FrameHeaderSize];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

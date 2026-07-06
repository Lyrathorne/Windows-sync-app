using System.Buffers.Binary;
using System.Text;

namespace DeviceSync.Protocol;

public sealed class ProtocolFrameReader
{
    private readonly Stream _stream;

    public ProtocolFrameReader(Stream stream)
    {
        _stream = stream;
    }

    public async Task<ProtocolMessage> ReadAsync(CancellationToken cancellationToken = default)
    {
        var header = new byte[ProtocolConstants.FrameHeaderSize];
        await ReadExactlyAsync(_stream, header, cancellationToken).ConfigureAwait(false);

        var payloadSize = BinaryPrimitives.ReadInt32BigEndian(header);
        if (payloadSize <= 0 || payloadSize > ProtocolConstants.MaxJsonMessageSize)
        {
            throw new ProtocolException("Invalid frame length.");
        }

        var payload = new byte[payloadSize];
        await ReadExactlyAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        return ProtocolSerializer.Deserialize(Encoding.UTF8.GetString(payload));
    }

    public static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Connection closed before the frame was complete.");
            }

            offset += read;
        }
    }
}

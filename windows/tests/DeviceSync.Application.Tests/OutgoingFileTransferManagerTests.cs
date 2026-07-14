using System.Text.Json;
using DeviceSync.Application;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class OutgoingFileTransferManagerTests
{
    [Fact]
    public async Task SendAsync_StreamsChunksInOrder_AndWaitsForReceipt()
    {
        var path = Path.GetTempFileName();
        var bytes = Enumerable.Range(0, OutgoingFileTransferManager.ChunkSizeBytes + 7).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var transport = new FakeOutgoingTransport();
            var manager = new OutgoingFileTransferManager(transport);

            var transfer = await manager.SendAsync(path, "application/octet-stream");

            Assert.Equal(OutgoingFileTransferState.Completed, transfer.State);
            var chunks = transport.Sent.Where(item => item.Type == ProtocolMessageTypes.FileChunk)
                .Select(item => ProtocolSerializer.DecodePayload<FileChunkPayload>(item.Payload)).ToArray();
            Assert.Equal(2, chunks.Length);
            Assert.Equal(0, chunks[0].Index);
            Assert.Equal(0, chunks[0].Offset);
            Assert.Equal(1, chunks[1].Index);
            Assert.Equal(OutgoingFileTransferManager.ChunkSizeBytes, chunks[1].Offset);
            Assert.Equal(bytes, chunks.SelectMany(chunk => Convert.FromBase64String(chunk.Data)).ToArray());
        }
        finally { File.Delete(path); }
    }

    private sealed class FakeOutgoingTransport : IOutgoingFileTransferTransport
    {
        public bool IsConnected => true;
        public string? RemoteDeviceId => "android-test";
        public bool SupportsResumableTransfers => false;
        public List<(string Type, JsonElement Payload)> Sent { get; } = [];
        public event EventHandler<OutgoingFileTransferMessageEventArgs>? MessageReceived;
        public event EventHandler? Disconnected { add { } remove { } }

        public Task SendAsync(string type, JsonElement payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload));
            if (type == ProtocolMessageTypes.FileOffer)
            {
                var offer = ProtocolSerializer.DecodePayload<FileOfferPayload>(payload);
                MessageReceived?.Invoke(this, new(ProtocolMessageTypes.FileAccept,
                    ProtocolSerializer.PayloadToJson(new FileAcceptPayload { TransferId = offer.TransferId })));
            }
            else if (type == ProtocolMessageTypes.FileComplete)
            {
                var complete = ProtocolSerializer.DecodePayload<FileCompletePayload>(payload);
                var offer = ProtocolSerializer.DecodePayload<FileOfferPayload>(Sent.Single(item => item.Type == ProtocolMessageTypes.FileOffer).Payload);
                MessageReceived?.Invoke(this, new(ProtocolMessageTypes.FileReceived,
                    ProtocolSerializer.PayloadToJson(new FileReceivedPayload
                    {
                        TransferId = complete.TransferId,
                        SizeBytes = complete.SizeBytes,
                        Sha256 = offer.Sha256,
                        SavedFileName = offer.FileName,
                    })));
            }
            return Task.CompletedTask;
        }
    }
}

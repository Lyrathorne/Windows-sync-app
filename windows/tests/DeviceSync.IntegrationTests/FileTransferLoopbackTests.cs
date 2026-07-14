using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DeviceSync.Application;
using DeviceSync.Infrastructure;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.IntegrationTests;

public sealed class FileTransferLoopbackTests
{
    [Fact]
    public async Task AuthenticatedStyleSession_TransfersMultipleChunksToDisk()
    {
        using var workspace = TempWorkspace.Create();
        var port = GetAvailablePort();
        var identity = new LoopbackIdentityProvider(port);
        var manager = new IncomingFileTransferManager(
            new WindowsIncomingFileStorage(),
            new AcceptIntoDirectoryDecisionService(workspace.Path));
        await using var server = new TcpDeviceServer(
            identity,
            new DeviceSessionRegistry(),
            incomingFileTransferManager: manager);
        await server.StartAsync();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        var reader = new ProtocolFrameReader(stream);
        var writer = new ProtocolFrameWriter(stream);
        var hello = Message(ProtocolMessageTypes.ConnectionHello, new ConnectionHelloPayload
        {
            DeviceName = "Android loopback",
            DeviceType = "android",
            AppVersion = "test",
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            Capabilities = SupportedCapabilities.Values,
        });
        await writer.WriteAsync(hello);
        var helloAck = await reader.ReadAsync();
        Assert.Equal(ProtocolMessageTypes.ConnectionHelloAck, helloAck.Type);

        var bytes = RandomNumberGenerator.GetBytes(IncomingFileTransferManager.RequiredChunkSize + 777);
        const string transferId = "550e8400-e29b-41d4-a716-446655440000";
        var offer = Message(ProtocolMessageTypes.FileOffer, new FileOfferPayload
        {
            TransferId = transferId,
            FileName = "loopback.bin",
            SizeBytes = bytes.Length,
            MimeType = "application/octet-stream",
            Sha256 = Base64Url(SHA256.HashData(bytes)),
            ChunkSize = IncomingFileTransferManager.RequiredChunkSize,
        });
        await writer.WriteAsync(offer);
        var accept = await reader.ReadAsync();
        Assert.Equal(ProtocolMessageTypes.FileAccept, accept.Type);
        Assert.Equal(offer.MessageId, accept.CorrelationId);

        await writer.WriteAsync(Message(ProtocolMessageTypes.FileChunk, new FileChunkPayload
        {
            TransferId = transferId,
            Index = 0,
            Offset = 0,
            Data = Convert.ToBase64String(bytes, 0, IncomingFileTransferManager.RequiredChunkSize),
        }));
        await writer.WriteAsync(Message(ProtocolMessageTypes.FileChunk, new FileChunkPayload
        {
            TransferId = transferId,
            Index = 1,
            Offset = IncomingFileTransferManager.RequiredChunkSize,
            Data = Convert.ToBase64String(
                bytes,
                IncomingFileTransferManager.RequiredChunkSize,
                bytes.Length - IncomingFileTransferManager.RequiredChunkSize),
        }));
        var complete = Message(ProtocolMessageTypes.FileComplete, new FileCompletePayload
        {
            TransferId = transferId,
            TotalChunks = 2,
            SizeBytes = bytes.Length,
        });
        await writer.WriteAsync(complete);

        var received = await reader.ReadAsync();
        Assert.Equal(ProtocolMessageTypes.FileReceived, received.Type);
        Assert.Equal(complete.MessageId, received.CorrelationId);
        var receivedPayload = ProtocolSerializer.DecodePayload<FileReceivedPayload>(received.Payload);
        var destination = Path.Combine(workspace.Path, receivedPayload.SavedFileName);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(workspace.Path, "*.part"));

        await server.StopAsync();
    }

    private static ProtocolMessage Message<T>(string type, T payload) => new()
    {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageId = Guid.NewGuid().ToString(),
        Type = type,
        SenderDeviceId = "android-loopback",
        RecipientDeviceId = "windows-loopback",
        TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        RequiresAcknowledgement = false,
        Payload = ProtocolSerializer.PayloadToJson(payload),
    };

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class AcceptIntoDirectoryDecisionService(string directory)
        : IIncomingFileTransferDecisionService
    {
        public Task<IncomingFileTransferDecision> DecideAsync(
            IncomingFileTransfer transfer,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new IncomingFileTransferDecision(true, directory));
    }

    private sealed class LoopbackIdentityProvider(int port) : IWindowsDeviceIdentityProvider
    {
        private AppSettings _settings = new()
        {
            WindowsDeviceId = "windows-loopback",
            DeviceName = "Windows loopback",
            Port = port,
        };

        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_settings.WindowsDeviceId!);

        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_settings);

        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        private TempWorkspace(string path) => Path = path;

        public string Path { get; }

        public static TempWorkspace Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"devicesync-file-transfer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempWorkspace(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

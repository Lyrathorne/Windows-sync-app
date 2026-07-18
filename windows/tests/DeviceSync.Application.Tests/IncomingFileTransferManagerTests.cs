using System.Security.Cryptography;
using DeviceSync.Infrastructure;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class IncomingFileTransferManagerTests
{
    private const string SenderDeviceId = "android-test-device";
    private const string TransferId = "550e8400-e29b-41d4-a716-446655440000";

    [Fact]
    public async Task MultipleChunks_AreStreamedVerifiedAndCommitted()
    {
        var bytes = RandomNumberGenerator.GetBytes(IncomingFileTransferManager.RequiredChunkSize + 137);
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage);

        var accepted = await manager.HandleOfferAsync(SenderDeviceId, Offer(bytes));
        Assert.Equal(ProtocolMessageTypes.FileAccept, accepted?.Type);

        Assert.Null(await manager.HandleChunkAsync(SenderDeviceId, Chunk(bytes, 0, 0, 65_536)));
        Assert.Null(await manager.HandleChunkAsync(SenderDeviceId, Chunk(bytes, 1, 65_536, 137)));
        var received = await manager.HandleCompleteAsync(SenderDeviceId, new FileCompletePayload
        {
            TransferId = TransferId,
            TotalChunks = 2,
            SizeBytes = bytes.Length,
        });

        Assert.Equal(ProtocolMessageTypes.FileReceived, received.Type);
        Assert.Equal(bytes, storage.CommittedBytes);
        Assert.Equal(IncomingFileTransferState.Completed, manager.ActiveTransfer?.State);
        Assert.Equal(bytes.Length, manager.ActiveTransfer?.ReceivedBytes);
        Assert.False(storage.TemporaryExists);
    }

    [Fact]
    public async Task EmptyFile_IsAllowedAndCommittedWithoutChunks()
    {
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage);
        await manager.HandleOfferAsync(SenderDeviceId, Offer([]));

        var received = await manager.HandleCompleteAsync(SenderDeviceId, new FileCompletePayload
        {
            TransferId = TransferId,
            TotalChunks = 0,
            SizeBytes = 0,
        });

        Assert.Equal(ProtocolMessageTypes.FileReceived, received.Type);
        Assert.Empty(storage.CommittedBytes!);
    }

    [Theory]
    [InlineData(1, 0, "invalid_chunk_index")]
    [InlineData(0, 1, "invalid_chunk_offset")]
    public async Task InvalidChunkPosition_FailsAndDeletesPart(int index, long offset, string expectedCode)
    {
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage);
        await manager.HandleOfferAsync(SenderDeviceId, Offer([1, 2, 3]));

        var response = await manager.HandleChunkAsync(SenderDeviceId, new FileChunkPayload
        {
            TransferId = TransferId,
            Index = index,
            Offset = offset,
            Data = Convert.ToBase64String([1, 2, 3]),
        });

        AssertError(response, expectedCode);
        Assert.Equal(IncomingFileTransferState.Failed, manager.ActiveTransfer?.State);
        Assert.False(storage.TemporaryExists);
        Assert.True(storage.DeleteCount > 0);
    }

    [Fact]
    public async Task ChunkExceedingOfferedSize_FailsAndDeletesPart()
    {
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage);
        await manager.HandleOfferAsync(SenderDeviceId, Offer([1, 2]));

        var response = await manager.HandleChunkAsync(SenderDeviceId, new FileChunkPayload
        {
            TransferId = TransferId,
            Index = 0,
            Offset = 0,
            Data = Convert.ToBase64String([1, 2, 3]),
        });

        AssertError(response, "size_exceeded");
        Assert.False(storage.TemporaryExists);
    }

    [Fact]
    public async Task CompleteBeforeAllBytes_FailsAndDeletesPart()
    {
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage);
        await manager.HandleOfferAsync(SenderDeviceId, Offer([1, 2, 3]));

        var response = await manager.HandleCompleteAsync(SenderDeviceId, new FileCompletePayload
        {
            TransferId = TransferId,
            TotalChunks = 0,
            SizeBytes = 3,
        });

        AssertError(response, "complete_before_all_bytes");
        Assert.False(storage.TemporaryExists);
    }

    [Fact]
    public async Task ChecksumMismatch_FailsWithoutDestinationFile()
    {
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage);
        var actual = new byte[] { 1, 2, 3 };
        var offered = new byte[] { 4, 5, 6 };
        await manager.HandleOfferAsync(SenderDeviceId, Offer(offered, sizeOverride: actual.Length));
        await manager.HandleChunkAsync(SenderDeviceId, Chunk(actual, 0, 0, actual.Length));

        var response = await manager.HandleCompleteAsync(SenderDeviceId, new FileCompletePayload
        {
            TransferId = TransferId,
            TotalChunks = 1,
            SizeBytes = actual.Length,
        });

        AssertError(response, "checksum_mismatch");
        Assert.Null(storage.CommittedBytes);
        Assert.False(storage.TemporaryExists);
    }

    [Fact]
    public async Task RejectedOffer_DoesNotCreatePart()
    {
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage, accepted: false);

        var response = await manager.HandleOfferAsync(SenderDeviceId, Offer([1]));

        Assert.Equal(ProtocolMessageTypes.FileReject, response?.Type);
        Assert.Equal(IncomingFileTransferState.Rejected, manager.ActiveTransfer?.State);
        Assert.False(storage.TemporaryExists);
    }

    [Fact]
    public async Task CancelAndDisconnect_DeletePart()
    {
        var cancelStorage = new FakeIncomingFileStorage();
        var cancelManager = CreateManager(cancelStorage);
        await cancelManager.HandleOfferAsync(SenderDeviceId, Offer([1]));
        await cancelManager.HandleCancelAsync(SenderDeviceId, new FileCancelPayload
        {
            TransferId = TransferId,
            Reason = "user_cancelled",
        });
        Assert.Equal(IncomingFileTransferState.Cancelled, cancelManager.ActiveTransfer?.State);
        Assert.False(cancelStorage.TemporaryExists);

        var disconnectStorage = new FakeIncomingFileStorage();
        var disconnectManager = CreateManager(disconnectStorage);
        await disconnectManager.HandleOfferAsync(SenderDeviceId, Offer([1]));
        await disconnectManager.HandleDisconnectAsync(SenderDeviceId);
        Assert.Equal(IncomingFileTransferState.Failed, disconnectManager.ActiveTransfer?.State);
        Assert.Equal("disconnected", disconnectManager.ActiveTransfer?.Error);
        Assert.False(disconnectStorage.TemporaryExists);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/secret.txt")]
    [InlineData("folder\\secret.txt")]
    public async Task TraversalName_IsRejected(string fileName)
    {
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage);

        var response = await manager.HandleOfferAsync(SenderDeviceId, Offer([1], fileName));

        Assert.Equal(ProtocolMessageTypes.FileReject, response?.Type);
        Assert.Equal("invalid_file_name", DecodeErrorCode<FileRejectPayload>(response!));
        Assert.False(storage.TemporaryExists);
    }

    [Fact]
    public async Task ExistingDestination_GetsUniqueName()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var storage = new FakeIncomingFileStorage(existingDestination: true);
        var manager = CreateManager(storage);
        await manager.HandleOfferAsync(SenderDeviceId, Offer(bytes));
        await manager.HandleChunkAsync(SenderDeviceId, Chunk(bytes, 0, 0, bytes.Length));

        var response = await manager.HandleCompleteAsync(SenderDeviceId, new FileCompletePayload
        {
            TransferId = TransferId,
            TotalChunks = 1,
            SizeBytes = bytes.Length,
        });

        var payload = ProtocolSerializer.DecodePayload<FileReceivedPayload>(response.Payload);
        Assert.Equal("hello (1).txt", payload.SavedFileName);
    }

    [Fact]
    public async Task ReusedTransferId_IsRejectedWithoutOverwriting()
    {
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage, accepted: false);
        await manager.HandleOfferAsync(SenderDeviceId, Offer([1]));

        var repeated = await manager.HandleOfferAsync(SenderDeviceId, Offer([1]));

        Assert.Equal(ProtocolMessageTypes.FileReject, repeated?.Type);
        Assert.Equal("duplicate_transfer_id", DecodeErrorCode<FileRejectPayload>(repeated!));
    }

    [Fact]
    public async Task OversizedAndDangerousOffers_AreRejectedBeforePartCreation()
    {
        var storage = new FakeIncomingFileStorage();
        var manager = CreateManager(storage);

        var oversized = await manager.HandleOfferAsync(SenderDeviceId,
            Offer([1]) with { SizeBytes = IncomingFileTransferManager.MaximumFileSize + 1 });
        Assert.Equal("file_too_large", DecodeErrorCode<FileRejectPayload>(oversized!));
        Assert.False(storage.TemporaryExists);

        var dangerous = await manager.HandleOfferAsync(SenderDeviceId,
            Offer([1], "payload.exe") with { TransferId = Guid.NewGuid().ToString() });
        Assert.Equal("CONTENT_TYPE_BLOCKED", DecodeErrorCode<FileRejectPayload>(dangerous!));
        Assert.False(storage.TemporaryExists);
    }

    [Fact]
    public async Task TrustRevokedDuringTransfer_FailsAndDeletesPart()
    {
        var storage = new FakeIncomingFileStorage();
        var guard = new MutableTransferGuard();
        var manager = new IncomingFileTransferManager(
            storage,
            new FakeDecisionService(true),
            transferGuard: guard);
        var bytes = new byte[] { 1, 2, 3 };
        await manager.HandleOfferAsync(SenderDeviceId, Offer(bytes));
        Assert.True(storage.TemporaryExists);

        guard.Allowed = false;
        var response = await manager.HandleChunkAsync(SenderDeviceId, Chunk(bytes, 0, 0, bytes.Length));

        AssertError(response, "TRUST_REVOKED");
        Assert.False(storage.TemporaryExists);
    }

    private static IncomingFileTransferManager CreateManager(FakeIncomingFileStorage storage, bool accepted = true)
        => new(storage, new FakeDecisionService(accepted));

    private static FileOfferPayload Offer(byte[] bytes, string fileName = "hello.txt", int? sizeOverride = null) => new()
    {
        TransferId = TransferId,
        FileName = fileName,
        SizeBytes = sizeOverride ?? bytes.Length,
        MimeType = "application/octet-stream",
        Sha256 = Base64Url(SHA256.HashData(bytes)),
        ChunkSize = IncomingFileTransferManager.RequiredChunkSize,
    };

    private static FileChunkPayload Chunk(byte[] source, int index, int offset, int count) => new()
    {
        TransferId = TransferId,
        Index = index,
        Offset = offset,
        Data = Convert.ToBase64String(source, offset, count),
    };

    private static void AssertError(FileTransferResponse? response, string expectedCode)
    {
        Assert.NotNull(response);
        Assert.Equal(ProtocolMessageTypes.FileError, response.Type);
        Assert.Equal(expectedCode, DecodeErrorCode<FileErrorPayload>(response));
    }

    private static string DecodeErrorCode<T>(FileTransferResponse response)
        => ProtocolSerializer.DecodePayload<T>(response.Payload) switch
        {
            FileErrorPayload error => error.Code,
            FileRejectPayload reject => reject.Code,
            _ => throw new InvalidOperationException(),
        };

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task V2_DisconnectPreservesPartial_AndResumeCompletesFromAcknowledgedOffset()
    {
        var root = Path.Combine(Path.GetTempPath(), "devicesync-resume-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Enumerable.Range(0, IncomingFileTransferManager.RequiredChunkSize + 17)
                .Select(index => (byte)(index % 251)).ToArray();
            var offer = Offer(bytes);
            var checkpoints = new JsonIncomingTransferCheckpointStore(Path.Combine(root, "metadata"));
            var storage = new WindowsIncomingFileStorage();
            var decision = new DirectoryDecisionService(root);
            var first = new IncomingFileTransferManager(storage, decision, checkpointStore: checkpoints);
            await first.HandleOfferAsync(SenderDeviceId, offer, resumable: true);
            var firstChunk = bytes[..IncomingFileTransferManager.RequiredChunkSize];

            var acknowledgement = await first.HandleChunkAsync(SenderDeviceId, new FileChunkPayload
            {
                TransferId = offer.TransferId,
                Index = 0,
                Offset = 0,
                Data = Convert.ToBase64String(firstChunk),
                ChunkSha256 = Base64Url(SHA256.HashData(firstChunk)),
            });
            Assert.Equal(ProtocolMessageTypes.FileChunkReceived, acknowledgement!.Type);
            await first.HandleDisconnectAsync(SenderDeviceId);

            var resumed = new IncomingFileTransferManager(storage, decision, checkpointStore: checkpoints);
            var resumeResponse = await resumed.HandleResumeRequestAsync(SenderDeviceId, new FileResumeRequestPayload
            {
                TransferId = offer.TransferId,
                FileName = offer.FileName,
                SizeBytes = offer.SizeBytes,
                Sha256 = offer.Sha256,
                ChunkSize = offer.ChunkSize,
            });
            var resumePoint = ProtocolSerializer.DecodePayload<FileResumeAcceptedPayload>(resumeResponse.Payload);
            Assert.Equal(firstChunk.Length, resumePoint.Offset);
            var finalChunk = bytes[firstChunk.Length..];
            await resumed.HandleChunkAsync(SenderDeviceId, new FileChunkPayload
            {
                TransferId = offer.TransferId,
                Index = 1,
                Offset = firstChunk.Length,
                Data = Convert.ToBase64String(finalChunk),
                ChunkSha256 = Base64Url(SHA256.HashData(finalChunk)),
            });
            var completed = await resumed.HandleCompleteAsync(SenderDeviceId, new FileCompletePayload
            {
                TransferId = offer.TransferId,
                TotalChunks = 2,
                SizeBytes = bytes.Length,
            });

            Assert.Equal(ProtocolMessageTypes.FileReceived, completed.Type);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, offer.FileName)));
            Assert.Null(await checkpoints.LoadAsync(offer.TransferId));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeDecisionService(bool accepted) : IIncomingFileTransferDecisionService
    {
        public Task<IncomingFileTransferDecision> DecideAsync(
            IncomingFileTransfer transfer,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new IncomingFileTransferDecision(accepted));
    }

    private sealed class MutableTransferGuard : IIncomingFileTransferGuard
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> IsTransferAllowedAsync(string senderDeviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Allowed);
    }

    private sealed class DirectoryDecisionService(string directory) : IIncomingFileTransferDecisionService
    {
        public Task<IncomingFileTransferDecision> DecideAsync(IncomingFileTransfer transfer, CancellationToken cancellationToken = default)
            => Task.FromResult(new IncomingFileTransferDecision(true, directory));
    }

    private sealed class FakeIncomingFileStorage : IIncomingFileStorage
    {
        private readonly bool _existingDestination;
        private MemoryStream? _stream;

        public FakeIncomingFileStorage(bool existingDestination = false)
        {
            _existingDestination = existingDestination;
        }

        public string DefaultReceiveDirectory => "C:\\receive";
        public byte[]? CommittedBytes { get; private set; }
        public bool TemporaryExists { get; private set; }
        public int DeleteCount { get; private set; }

        public long GetAvailableBytes(string directoryPath) => long.MaxValue;

        public FileTransferReservation Reserve(string directoryPath, string safeFileName, string transferId)
        {
            TemporaryExists = true;
            var destinationName = _existingDestination ? "hello (1).txt" : safeFileName;
            return new FileTransferReservation(
                Path.Combine(directoryPath, $".devicesync-{transferId}.part"),
                Path.Combine(directoryPath, destinationName));
        }

        public Stream OpenWrite(string temporaryPath)
        {
            _stream = new MemoryStream();
            return _stream;
        }

        public Task<string> CommitAsync(FileTransferReservation reservation, CancellationToken cancellationToken = default)
        {
            CommittedBytes = _stream!.ToArray();
            TemporaryExists = false;
            return Task.FromResult(reservation.DestinationPath);
        }

        public Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            TemporaryExists = false;
            return Task.CompletedTask;
        }
    }
}

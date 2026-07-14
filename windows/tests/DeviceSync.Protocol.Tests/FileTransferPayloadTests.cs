using System.Text.Json;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Protocol.Tests;

public sealed class FileTransferPayloadTests
{
    private const string TransferId = "550e8400-e29b-41d4-a716-446655440000";
    private const string Sha256 = "ZOyIygCyaOW6GjVnihtTFtIS9PNmskdyMlNKiuyjfzw";

    [Fact]
    public void MessageTypes_MatchSharedContract()
    {
        Assert.Equal("file.offer", ProtocolMessageTypes.FileOffer);
        Assert.Equal("file.accept", ProtocolMessageTypes.FileAccept);
        Assert.Equal("file.reject", ProtocolMessageTypes.FileReject);
        Assert.Equal("file.chunk", ProtocolMessageTypes.FileChunk);
        Assert.Equal("file.complete", ProtocolMessageTypes.FileComplete);
        Assert.Equal("file.received", ProtocolMessageTypes.FileReceived);
        Assert.Equal("file.cancel", ProtocolMessageTypes.FileCancel);
        Assert.Equal("file.error", ProtocolMessageTypes.FileError);
        Assert.Equal("file.chunk.received", ProtocolMessageTypes.FileChunkReceived);
        Assert.Equal("file.resume.request", ProtocolMessageTypes.FileResumeRequest);
        Assert.Equal("file.resume.accepted", ProtocolMessageTypes.FileResumeAccepted);
    }

    [Fact]
    public void V2PayloadModels_RoundTrip()
    {
        Assert.Equal(new FileChunkReceivedPayload { TransferId = TransferId, NextChunkIndex = 4, Offset = 262144 },
            RoundTrip(new FileChunkReceivedPayload { TransferId = TransferId, NextChunkIndex = 4, Offset = 262144 }));
        Assert.Equal(new FileResumeRequestPayload { TransferId = TransferId, FileName = "photo.jpg", SizeBytes = 1258291, Sha256 = Sha256, ChunkSize = 65536 },
            RoundTrip(new FileResumeRequestPayload { TransferId = TransferId, FileName = "photo.jpg", SizeBytes = 1258291, Sha256 = Sha256, ChunkSize = 65536 }));
        Assert.Equal(new FileResumeAcceptedPayload { TransferId = TransferId, NextChunkIndex = 4, Offset = 262144 },
            RoundTrip(new FileResumeAcceptedPayload { TransferId = TransferId, NextChunkIndex = 4, Offset = 262144 }));
    }

    [Fact]
    public void SupportedCapabilities_ContainsFileTransferV1()
    {
        Assert.Equal("file-transfer-v1", SupportedCapabilities.FileTransferV1);
        Assert.Contains(SupportedCapabilities.FileTransferV1, SupportedCapabilities.Values);
    }

    [Fact]
    public void FileOffer_UsesCamelCaseAndPreservesInt64()
    {
        const long sizeBytes = 3_000_000_000L;
        var payload = Offer(sizeBytes);

        var json = ProtocolSerializer.PayloadToJson(payload);
        var decoded = ProtocolSerializer.DecodePayload<FileOfferPayload>(json);

        Assert.True(json.TryGetProperty("transferId", out _));
        Assert.True(json.TryGetProperty("sizeBytes", out var size));
        Assert.False(json.TryGetProperty("TransferId", out _));
        Assert.Equal(sizeBytes, size.GetInt64());
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void FileChunk_PreservesInt64Offset()
    {
        const long offset = 3_000_000_000L;
        var payload = new FileChunkPayload
        {
            TransferId = TransferId,
            Index = 45_776,
            Offset = offset,
            Data = "SGVsbG8gd29ybGQ=",
        };

        Assert.Equal(payload, RoundTrip(payload));
    }

    [Fact]
    public void AllPayloadModels_RoundTrip()
    {
        Assert.Equal(new FileAcceptPayload { TransferId = TransferId },
            RoundTrip(new FileAcceptPayload { TransferId = TransferId }));
        Assert.Equal(new FileRejectPayload { TransferId = TransferId, Code = "user_rejected", Message = "Declined." },
            RoundTrip(new FileRejectPayload { TransferId = TransferId, Code = "user_rejected", Message = "Declined." }));
        Assert.Equal(new FileCompletePayload { TransferId = TransferId, TotalChunks = 1, SizeBytes = 11 },
            RoundTrip(new FileCompletePayload { TransferId = TransferId, TotalChunks = 1, SizeBytes = 11 }));
        Assert.Equal(new FileReceivedPayload { TransferId = TransferId, SizeBytes = 11, Sha256 = Sha256, SavedFileName = "hello.txt" },
            RoundTrip(new FileReceivedPayload { TransferId = TransferId, SizeBytes = 11, Sha256 = Sha256, SavedFileName = "hello.txt" }));
        Assert.Equal(new FileCancelPayload { TransferId = TransferId, Reason = "user_cancelled" },
            RoundTrip(new FileCancelPayload { TransferId = TransferId, Reason = "user_cancelled" }));
        Assert.Equal(new FileErrorPayload { TransferId = TransferId, Code = "checksum_mismatch", Message = "Hash mismatch." },
            RoundTrip(new FileErrorPayload { TransferId = TransferId, Code = "checksum_mismatch", Message = "Hash mismatch." }));
    }

    [Fact]
    public void FileOffer_IgnoresUnknownFields()
    {
        using var document = JsonDocument.Parse("""
            {
              "transferId": "550e8400-e29b-41d4-a716-446655440000",
              "fileName": "hello.txt",
              "sizeBytes": 11,
              "mimeType": "text/plain",
              "sha256": "ZOyIygCyaOW6GjVnihtTFtIS9PNmskdyMlNKiuyjfzw",
              "chunkSize": 65536,
              "futureField": true
            }
            """);

        var decoded = ProtocolSerializer.DecodePayload<FileOfferPayload>(document.RootElement);

        Assert.Equal("hello.txt", decoded.FileName);
    }

    [Fact]
    public void FileOffer_RejectsMissingRequiredField()
    {
        using var document = JsonDocument.Parse("""
            {
              "transferId": "550e8400-e29b-41d4-a716-446655440000",
              "fileName": "hello.txt",
              "sizeBytes": 11,
              "mimeType": "text/plain",
              "chunkSize": 65536
            }
            """);

        Assert.Throws<ProtocolException>(() =>
            ProtocolSerializer.DecodePayload<FileOfferPayload>(document.RootElement));
    }

    [Theory]
    [InlineData("01-file-offer.json", ProtocolMessageTypes.FileOffer)]
    [InlineData("02-file-accept.json", ProtocolMessageTypes.FileAccept)]
    [InlineData("03-file-chunk.json", ProtocolMessageTypes.FileChunk)]
    [InlineData("04-file-complete.json", ProtocolMessageTypes.FileComplete)]
    [InlineData("05-file-received.json", ProtocolMessageTypes.FileReceived)]
    [InlineData("file-reject.json", ProtocolMessageTypes.FileReject)]
    [InlineData("file-cancel.json", ProtocolMessageTypes.FileCancel)]
    [InlineData("file-error.json", ProtocolMessageTypes.FileError)]
    [InlineData("v2-file-resume-request.json", ProtocolMessageTypes.FileResumeRequest)]
    [InlineData("v2-file-resume-accepted.json", ProtocolMessageTypes.FileResumeAccepted)]
    [InlineData("v2-file-chunk-received.json", ProtocolMessageTypes.FileChunkReceived)]
    public void SharedVector_DeserializesWithExpectedPayload(string fileName, string expectedType)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "file-transfer", fileName);
        var message = ProtocolSerializer.Deserialize(File.ReadAllText(path));

        Assert.Equal(expectedType, message.Type);

        _ = message.Type switch
        {
            ProtocolMessageTypes.FileOffer => (object)ProtocolSerializer.DecodePayload<FileOfferPayload>(message.Payload),
            ProtocolMessageTypes.FileAccept => ProtocolSerializer.DecodePayload<FileAcceptPayload>(message.Payload),
            ProtocolMessageTypes.FileReject => ProtocolSerializer.DecodePayload<FileRejectPayload>(message.Payload),
            ProtocolMessageTypes.FileChunk => ProtocolSerializer.DecodePayload<FileChunkPayload>(message.Payload),
            ProtocolMessageTypes.FileComplete => ProtocolSerializer.DecodePayload<FileCompletePayload>(message.Payload),
            ProtocolMessageTypes.FileReceived => ProtocolSerializer.DecodePayload<FileReceivedPayload>(message.Payload),
            ProtocolMessageTypes.FileCancel => ProtocolSerializer.DecodePayload<FileCancelPayload>(message.Payload),
            ProtocolMessageTypes.FileError => ProtocolSerializer.DecodePayload<FileErrorPayload>(message.Payload),
            ProtocolMessageTypes.FileResumeRequest => ProtocolSerializer.DecodePayload<FileResumeRequestPayload>(message.Payload),
            ProtocolMessageTypes.FileResumeAccepted => ProtocolSerializer.DecodePayload<FileResumeAcceptedPayload>(message.Payload),
            ProtocolMessageTypes.FileChunkReceived => ProtocolSerializer.DecodePayload<FileChunkReceivedPayload>(message.Payload),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected vector type: {message.Type}"),
        };
    }

    private static FileOfferPayload Offer(long sizeBytes) => new()
    {
        TransferId = TransferId,
        FileName = "hello.txt",
        SizeBytes = sizeBytes,
        MimeType = "text/plain",
        Sha256 = Sha256,
        ChunkSize = 65_536,
    };

    private static T RoundTrip<T>(T payload)
    {
        var json = ProtocolSerializer.PayloadToJson(payload);
        return ProtocolSerializer.DecodePayload<T>(json);
    }
}

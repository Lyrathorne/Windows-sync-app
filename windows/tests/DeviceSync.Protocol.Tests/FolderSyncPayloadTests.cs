using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Protocol.Tests;

public sealed class FolderSyncPayloadTests
{
    [Fact]
    public void Approval_round_trips_with_camel_case_conflict_choices()
    {
        var payload = new FolderPlanApprovedPayload
        {
            SyncId = "sync-1",
            ConflictResolutions =
            [
                new FolderConflictResolutionPayload { RelativePath = "docs/report.txt", Resolution = "keep_both" },
            ],
        };

        var json = ProtocolSerializer.PayloadToJson(payload);
        Assert.True(json.TryGetProperty("syncId", out _));
        Assert.True(json.TryGetProperty("conflictResolutions", out _));
        var decoded = ProtocolSerializer.DecodePayload<FolderPlanApprovedPayload>(json);
        Assert.Equal(payload.SyncId, decoded.SyncId);
        Assert.Equal(payload.ConflictResolutions, decoded.ConflictResolutions);
    }

    [Fact]
    public void Folder_metadata_on_file_offer_round_trips()
    {
        var payload = new FileOfferPayload
        {
            TransferId = "transfer-1", FileName = "report.txt", SizeBytes = 12,
            MimeType = "text/plain", Sha256 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", ChunkSize = 65_536,
            FolderSyncId = "sync-1", RelativePath = "docs/report.txt", ConflictCopy = true,
        };
        Assert.Equal(payload, ProtocolSerializer.DecodePayload<FileOfferPayload>(ProtocolSerializer.PayloadToJson(payload)));
    }
}

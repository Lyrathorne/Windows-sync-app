using System.Text.Json;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Protocol.Tests;

public sealed class MediaCatalogPayloadTests
{
    [Fact]
    public void Contract_UsesExactMessageTypesAndImplementedCapabilities()
    {
        Assert.Equal("catalog.query", ProtocolMessageTypes.CatalogQuery);
        Assert.Equal("catalog.page", ProtocolMessageTypes.CatalogPage);
        Assert.Equal("catalog.changed", ProtocolMessageTypes.CatalogChanged);
        Assert.Equal("catalog.thumbnail.request", ProtocolMessageTypes.CatalogThumbnailRequest);
        Assert.Equal("catalog.thumbnail.response", ProtocolMessageTypes.CatalogThumbnailResponse);
        Assert.Equal("catalog.file.download.request", ProtocolMessageTypes.CatalogFileDownloadRequest);
        Assert.Equal("catalog.permission", ProtocolMessageTypes.CatalogPermission);
        Assert.Equal("catalog.error", ProtocolMessageTypes.CatalogError);
        Assert.Equal("catalog.cancel", ProtocolMessageTypes.CatalogCancel);
        Assert.Equal("media-catalog-v1", SupportedCapabilities.MediaCatalogV1);
        Assert.Equal("thumbnails-v1", SupportedCapabilities.ThumbnailsV1);
        Assert.Contains(SupportedCapabilities.MediaCatalogV1, SupportedCapabilities.Values);
        Assert.Contains(SupportedCapabilities.ThumbnailsV1, SupportedCapabilities.Values);
    }

    [Fact]
    public void SharedVectors_DecodeEveryPayloadAndPreserveInt64()
    {
        var query = Decode<CatalogQueryPayload>("01-query.json", ProtocolMessageTypes.CatalogQuery);
        Assert.Equal(3_000_000_000L, query.GenerationAfter);

        var page = Decode<CatalogPagePayload>("02-page.json", ProtocolMessageTypes.CatalogPage);
        Assert.Equal(5_000_000_000L, page.Items.Single().SizeBytes);
        Assert.Equal("Лето.jpg", page.Items.Single().DisplayName);

        Assert.Equal(2, Decode<CatalogChangedPayload>("03-changed.json", ProtocolMessageTypes.CatalogChanged).Changes.Count);
        Assert.Equal(256, Decode<CatalogThumbnailRequestPayload>("04-thumbnail-request.json", ProtocolMessageTypes.CatalogThumbnailRequest).MaxWidth);
        Assert.Equal("aGVsbG8gd29ybGQ=", Decode<CatalogThumbnailResponsePayload>("05-thumbnail-response.json", ProtocolMessageTypes.CatalogThumbnailResponse).Data);
        Assert.NotEmpty(Decode<CatalogFileDownloadRequestPayload>("06-download-request.json", ProtocolMessageTypes.CatalogFileDownloadRequest).TransferId);
        Assert.Equal("revoked", Decode<CatalogPermissionPayload>("07-permission-revoked.json", ProtocolMessageTypes.CatalogPermission).State);
        Assert.Equal("ITEM_CHANGED", Decode<CatalogErrorPayload>("08-error.json", ProtocolMessageTypes.CatalogError).Code);
        Assert.Equal("view_closed", Decode<CatalogCancelPayload>("09-cancel.json", ProtocolMessageTypes.CatalogCancel).Reason);
    }

    [Fact]
    public void Query_IgnoresUnknownFieldsAndUsesCamelCase()
    {
        var message = ReadMessage("01-query.json");
        var payload = ProtocolSerializer.DecodePayload<CatalogQueryPayload>(message.Payload);
        var encoded = ProtocolSerializer.PayloadToJson(payload);

        Assert.Equal("summer", payload.Search);
        Assert.True(encoded.TryGetProperty("queryId", out _));
        Assert.False(encoded.TryGetProperty("QueryId", out _));
    }

    [Fact]
    public void Item_RejectsMissingRequiredField()
    {
        var json = JsonDocument.Parse("""
            {"itemId":"item:1","displayName":"photo.jpg","mimeType":"image/jpeg","modifiedAtUtc":"2026-07-15T10:00:00Z","category":"image","generation":1}
            """).RootElement.Clone();

        Assert.Throws<ProtocolException>(() => ProtocolSerializer.DecodePayload<CatalogItemPayload>(json));
    }

    [Fact]
    public void SharedVectors_NeverExposeAndroidPathsOrUris()
    {
        foreach (var path in Directory.GetFiles(VectorDirectory, "*.json"))
        {
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("content://", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("filesystemPath", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("relativePath", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static T Decode<T>(string fileName, string expectedType)
    {
        var message = ReadMessage(fileName);
        Assert.Equal(expectedType, message.Type);
        return ProtocolSerializer.DecodePayload<T>(message.Payload);
    }

    private static ProtocolMessage ReadMessage(string fileName) =>
        ProtocolSerializer.Deserialize(File.ReadAllText(Path.Combine(VectorDirectory, fileName)));

    private static string VectorDirectory => Path.Combine(AppContext.BaseDirectory, "TestVectors", "media-catalog");
}

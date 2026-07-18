namespace DeviceSync.Protocol;

public static class SupportedCapabilities
{
    public const string FileTransferV1 = "file-transfer-v1";
    public const string FileTransferV2 = "file-transfer-v2";
    public const string ClipboardV1 = "clipboard-v1";
    public const string TextShareV1 = "text-share-v1";
    public const string NotificationsV1 = "notifications-v1";
    public const string FolderSyncV1 = "folder-sync-v1";
    public const string ClipboardAutoV1 = "clipboard-auto-v1";
    public const string FileAutoReceiveV1 = "file-auto-receive-v1";
    public const string MediaCatalogV1 = "media-catalog-v1";
    public const string ThumbnailsV1 = "thumbnails-v1";
    public const string TransportLanTlsV1 = "transport-lan-tls-v1";
    public const string TransportHotspotV1 = "transport-hotspot-v1";
    public const string TransportUsbV1 = "transport-usb-v1";
    public const string TransportBluetoothV1 = "transport-bluetooth-v1";

    public static readonly IReadOnlyList<string> Values =
    [
        "heartbeat-v1",
        "ack-v1",
        "reconnect-v1",
        FileTransferV1,
        FileTransferV2,
        ClipboardV1,
        TextShareV1,
        NotificationsV1,
        FolderSyncV1,
        MediaCatalogV1,
        ThumbnailsV1,
        TransportLanTlsV1,
        TransportHotspotV1,
        TransportUsbV1,
        TransportBluetoothV1,
    ];
}

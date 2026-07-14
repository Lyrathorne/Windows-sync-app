namespace DeviceSync.Protocol;

public static class SupportedCapabilities
{
    public const string FileTransferV1 = "file-transfer-v1";
    public const string FileTransferV2 = "file-transfer-v2";
    public const string ClipboardV1 = "clipboard-v1";
    public const string TextShareV1 = "text-share-v1";
    public const string NotificationsV1 = "notifications-v1";
    public const string FolderSyncV1 = "folder-sync-v1";

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
    ];
}

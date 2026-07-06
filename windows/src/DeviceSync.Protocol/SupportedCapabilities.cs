namespace DeviceSync.Protocol;

public static class SupportedCapabilities
{
    public static readonly IReadOnlyList<string> Values =
    [
        "heartbeat-v1",
        "ack-v1",
        "reconnect-v1",
    ];
}

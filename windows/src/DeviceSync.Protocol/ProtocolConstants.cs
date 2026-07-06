namespace DeviceSync.Protocol;

public static class ProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const int MaxJsonMessageSize = 1_048_576;
    public const int FrameHeaderSize = 4;
    public const string TimestampFormat = "ISO-8601 UTC string, for example 2026-07-05T18:45:00Z";
}

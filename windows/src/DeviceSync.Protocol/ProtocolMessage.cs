using System.Text.Json;

namespace DeviceSync.Protocol;

public sealed record ProtocolMessage
{
    public required int ProtocolVersion { get; init; }
    public required string MessageId { get; init; }
    public required string Type { get; init; }
    public required string SenderDeviceId { get; init; }
    public string? RecipientDeviceId { get; init; }
    public required string TimestampUtc { get; init; }
    public string? CorrelationId { get; init; }
    public bool RequiresAcknowledgement { get; init; }
    public required JsonElement Payload { get; init; }
}

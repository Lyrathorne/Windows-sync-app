namespace DeviceSync.Application;

public sealed record DeviceSessionInfo
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string DeviceType { get; init; }
    public required int ProtocolVersion { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public required DateTimeOffset ConnectedAtUtc { get; init; }
    public DateTimeOffset LastSeenAtUtc { get; init; }
    public DeviceTransportKind TransportKind { get; init; } = DeviceTransportKind.Lan;
    public bool IsSlowTransport { get; init; }
    public string? TransportAddress { get; init; }
}

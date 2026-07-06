namespace DeviceSync.Application;

public enum PublisherState
{
    Stopped,
    Starting,
    Published,
    Stopping,
    Failed,
}

public sealed record PublishedService
{
    public required string InstanceName { get; init; }
    public required string ServiceType { get; init; }
    public required int Port { get; init; }
    public string? AdvertisedAddress { get; init; }
    public IReadOnlyDictionary<string, string> TxtRecords { get; init; } = new Dictionary<string, string>();
}

public sealed class PublisherStateChangedEventArgs : EventArgs
{
    public PublisherStateChangedEventArgs(
        PublisherState state,
        PublishedService? service,
        string? lastError,
        DateTimeOffset? lastPublishedAtUtc)
    {
        State = state;
        Service = service;
        LastError = lastError;
        LastPublishedAtUtc = lastPublishedAtUtc;
    }

    public PublisherState State { get; }
    public PublishedService? Service { get; }
    public string? LastError { get; }
    public DateTimeOffset? LastPublishedAtUtc { get; }
}

namespace DeviceSync.Application;

public sealed record AppSettings
{
    public string? WindowsDeviceId { get; init; }
    public string? DeviceName { get; init; }
    public int Port { get; init; } = 54321;
}

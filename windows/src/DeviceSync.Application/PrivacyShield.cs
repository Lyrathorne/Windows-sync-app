namespace DeviceSync.Application;

public interface IPrivacyShieldMonitor
{
    bool IsSensitiveSession { get; }
    event EventHandler? Changed;
}

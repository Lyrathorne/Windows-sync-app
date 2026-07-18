using DeviceSync.Application;
using Microsoft.Win32;

namespace DeviceSync.App;

public sealed class WindowsPrivacyShieldMonitor : IPrivacyShieldMonitor, IDisposable
{
    private bool _locked;

    public WindowsPrivacyShieldMonitor()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public bool IsSensitiveSession =>
        _locked || System.Windows.Forms.SystemInformation.TerminalServerSession;

    public event EventHandler? Changed;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        var value = args.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,
            _ => _locked,
        };
        if (value == _locked) return;
        _locked = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;
}

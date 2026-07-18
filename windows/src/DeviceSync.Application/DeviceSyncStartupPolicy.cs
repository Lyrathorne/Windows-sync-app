namespace DeviceSync.Application;

public sealed record DeviceSyncStartupPlan(
    bool ShowDiagnostics,
    EdgePanelState InitialPanelState);

public static class DeviceSyncStartupPolicy
{
    public static DeviceSyncStartupPlan Create(bool edgePanelEnabled) =>
        new(
            ShowDiagnostics: false,
            InitialPanelState: EdgePanelState.Hidden);
}

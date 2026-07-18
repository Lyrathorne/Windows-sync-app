namespace DeviceSync.Application;

public enum EdgePanelTrigger
{
    Enable,
    Disable,
    RevealHandle,
    Expand,
    Collapse,
    Hide,
}

public static class EdgePanelStateMachine
{
    public static EdgePanelState Transition(EdgePanelState current, EdgePanelTrigger trigger) =>
        trigger switch
        {
            EdgePanelTrigger.Disable or EdgePanelTrigger.Hide => EdgePanelState.Hidden,
            EdgePanelTrigger.Enable or EdgePanelTrigger.Collapse => EdgePanelState.Hidden,
            EdgePanelTrigger.RevealHandle or EdgePanelTrigger.Expand => EdgePanelState.Expanded,
            _ => current,
        };
}

public sealed class EdgePanelTransitionScheduler
{
    private int _version;

    public EdgePanelState? PendingState { get; private set; }
    public int Version => _version;

    public int Schedule(EdgePanelState state)
    {
        PendingState = state;
        return ++_version;
    }

    public void Cancel()
    {
        PendingState = null;
        _version++;
    }

    public bool TryTake(int version, out EdgePanelState state)
    {
        if (version != _version || PendingState is null)
        {
            state = default;
            return false;
        }

        state = PendingState.Value;
        PendingState = null;
        return true;
    }
}

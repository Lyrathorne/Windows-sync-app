using DeviceSync.Application;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class EdgePanelStateMachineTests
{
    [Fact]
    public void StateMachine_FollowsHiddenExpandedHiddenFlow()
    {
        var state = EdgePanelState.Hidden;

        state = EdgePanelStateMachine.Transition(state, EdgePanelTrigger.Enable);
        Assert.Equal(EdgePanelState.Hidden, state);

        state = EdgePanelStateMachine.Transition(state, EdgePanelTrigger.RevealHandle);
        Assert.Equal(EdgePanelState.Expanded, state);

        state = EdgePanelStateMachine.Transition(state, EdgePanelTrigger.Hide);
        Assert.Equal(EdgePanelState.Hidden, state);
    }

    [Fact]
    public void Cancel_InvalidatesDelayedTransition()
    {
        var scheduler = new EdgePanelTransitionScheduler();
        var version = scheduler.Schedule(EdgePanelState.Hidden);

        scheduler.Cancel();

        Assert.False(scheduler.TryTake(version, out _));
        Assert.Null(scheduler.PendingState);
    }

    [Fact]
    public void Reschedule_InvalidatesPreviousTransitionAndKeepsLatest()
    {
        var scheduler = new EdgePanelTransitionScheduler();
        var oldVersion = scheduler.Schedule(EdgePanelState.Hidden);
        var currentVersion = scheduler.Schedule(EdgePanelState.Expanded);

        Assert.False(scheduler.TryTake(oldVersion, out _));
        Assert.True(scheduler.TryTake(currentVersion, out var state));
        Assert.Equal(EdgePanelState.Expanded, state);
    }

    [Theory]
    [InlineData(true, EdgePanelState.Hidden)]
    [InlineData(false, EdgePanelState.Hidden)]
    public void StartupPolicy_NeverShowsDiagnosticsAndChoosesUnobtrusiveState(
        bool enabled,
        EdgePanelState expectedState)
    {
        var plan = DeviceSyncStartupPolicy.Create(enabled);

        Assert.False(plan.ShowDiagnostics);
        Assert.Equal(expectedState, plan.InitialPanelState);
    }
}

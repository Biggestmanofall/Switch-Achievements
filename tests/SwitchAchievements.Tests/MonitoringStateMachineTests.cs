using SwitchAchievements.App.Services;
using SwitchAchievements.Core;

namespace SwitchAchievements.Tests;

public class MonitoringStateMachineTests
{
    private static MonitoringStateMachine New(int required = MonitoringStateMachine.DefaultRequiredStableObservations)
        => new(required);

    private static void Start(MonitoringStateMachine m)
        => Assert.True(m.TryTransition(MonitoringState.Searching));

    private static void ToConnecting(MonitoringStateMachine m)
    {
        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.True(m.Observe(ProcessSelectionStatus.Unique));
    }

    private static void ToMonitoring(MonitoringStateMachine m)
    {
        ToConnecting(m);
        Assert.True(m.TryTransition(MonitoringState.Monitoring));
    }

    private static void ToReconnecting(MonitoringStateMachine m)
    {
        ToMonitoring(m);
        Assert.True(m.TryTransition(MonitoringState.Reconnecting));
    }

    private static void ToAmbiguous(MonitoringStateMachine m)
    {
        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Ambiguous));
        Assert.True(m.Observe(ProcessSelectionStatus.Ambiguous));
    }

    private static void ToError(MonitoringStateMachine m)
    {
        Start(m);
        Assert.True(m.TryTransition(MonitoringState.Error));
    }

    private static void AssertCanStop(Action<MonitoringStateMachine> reach)
    {
        var m = New();
        reach(m);
        Assert.True(m.TryTransition(MonitoringState.Stopped));
        Assert.Equal(MonitoringState.Stopped, m.State);
    }

    [Fact]
    public void Initial_state_is_stopped()
        => Assert.Equal(MonitoringState.Stopped, New().State);

    [Fact]
    public void Default_threshold_requires_two_observations()
    {
        var m = New();
        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Searching, m.State);
        Assert.True(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Connecting, m.State);
    }

    [Fact]
    public void Custom_threshold_is_respected()
    {
        var m = New(3);
        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Searching, m.State);
        Assert.True(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Connecting, m.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Threshold_below_one_throws(int required)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MonitoringStateMachine(required));

    [Fact]
    public void Start_transitions_stopped_to_searching()
    {
        var m = New();
        Assert.True(m.TryTransition(MonitoringState.Searching));
        Assert.Equal(MonitoringState.Searching, m.State);
    }

    [Fact]
    public void Start_when_already_searching_returns_false()
    {
        var m = New();
        Start(m);
        Assert.False(m.TryTransition(MonitoringState.Searching));
        Assert.Equal(MonitoringState.Searching, m.State);
    }

    [Fact] public void Stop_works_from_Searching() => AssertCanStop(Start);
    [Fact] public void Stop_works_from_Connecting() => AssertCanStop(ToConnecting);
    [Fact] public void Stop_works_from_Monitoring() => AssertCanStop(ToMonitoring);
    [Fact] public void Stop_works_from_Reconnecting() => AssertCanStop(ToReconnecting);
    [Fact] public void Stop_works_from_Ambiguous() => AssertCanStop(ToAmbiguous);
    [Fact] public void Stop_works_from_Error() => AssertCanStop(ToError);

    [Fact]
    public void Stop_when_stopped_returns_false()
    {
        var m = New();
        Assert.False(m.TryTransition(MonitoringState.Stopped));
        Assert.Equal(MonitoringState.Stopped, m.State);
    }

    [Fact]
    public void Connection_success_transitions_connecting_to_monitoring()
    {
        var m = New();
        ToConnecting(m);
        Assert.True(m.TryTransition(MonitoringState.Monitoring));
        Assert.Equal(MonitoringState.Monitoring, m.State);
    }

    [Fact]
    public void Connection_success_is_rejected_outside_connecting()
    {
        var stopped = New();
        Assert.False(stopped.TryTransition(MonitoringState.Monitoring));
        Assert.Equal(MonitoringState.Stopped, stopped.State);

        var searching = New();
        Start(searching);
        Assert.False(searching.TryTransition(MonitoringState.Monitoring));
        Assert.Equal(MonitoringState.Searching, searching.State);

        var monitoring = New();
        ToMonitoring(monitoring);
        Assert.False(monitoring.TryTransition(MonitoringState.Monitoring)); // same-state
        Assert.Equal(MonitoringState.Monitoring, monitoring.State);
    }

    [Fact]
    public void Disconnect_transitions_monitoring_to_reconnecting()
    {
        var m = New();
        ToMonitoring(m);
        Assert.True(m.TryTransition(MonitoringState.Reconnecting));
        Assert.Equal(MonitoringState.Reconnecting, m.State);
    }

    [Fact]
    public void Disconnect_transitions_connecting_to_reconnecting()
    {
        var m = New();
        ToConnecting(m);
        Assert.True(m.TryTransition(MonitoringState.Reconnecting));
        Assert.Equal(MonitoringState.Reconnecting, m.State);
    }

    [Fact]
    public void Fatal_error_transitions_from_each_non_error_state()
    {
        Assert.True(New().TryTransition(MonitoringState.Error)); // Stopped

        var s = New(); Start(s); Assert.True(s.TryTransition(MonitoringState.Error));
        var c = New(); ToConnecting(c); Assert.True(c.TryTransition(MonitoringState.Error));
        var m = New(); ToMonitoring(m); Assert.True(m.TryTransition(MonitoringState.Error));
        var r = New(); ToReconnecting(r); Assert.True(r.TryTransition(MonitoringState.Error));
        var a = New(); ToAmbiguous(a); Assert.True(a.TryTransition(MonitoringState.Error));
    }

    [Fact]
    public void Fatal_error_from_error_is_rejected()
    {
        var m = New();
        ToError(m);
        Assert.False(m.TryTransition(MonitoringState.Error));
        Assert.Equal(MonitoringState.Error, m.State);
    }

    [Fact]
    public void Invalid_lifecycle_transitions_fail_and_preserve_state()
    {
        var m = New();
        Assert.False(m.TryTransition(MonitoringState.Monitoring));   // Stopped -> Monitoring illegal
        Assert.False(m.TryTransition(MonitoringState.Reconnecting)); // Stopped -> Reconnecting illegal
        Assert.False(m.TryTransition(MonitoringState.Stopped));      // same-state
        Assert.Equal(MonitoringState.Stopped, m.State);

        Start(m);
        Assert.False(m.TryTransition(MonitoringState.Monitoring));   // Searching -> Monitoring illegal
        Assert.False(m.TryTransition(MonitoringState.Reconnecting)); // Searching -> Reconnecting illegal
        Assert.False(m.TryTransition(MonitoringState.Searching));    // same-state
        Assert.Equal(MonitoringState.Searching, m.State);
    }

    [Fact]
    public void Stop_works_regardless_of_debounce_counter()
    {
        var m = New();
        Start(m);
        m.Observe(ProcessSelectionStatus.Unique); // partial (below threshold)
        Assert.True(m.TryTransition(MonitoringState.Stopped));
        Assert.Equal(MonitoringState.Stopped, m.State);
    }

    [Fact]
    public void Error_works_regardless_of_debounce_counter()
    {
        var m = New();
        Start(m);
        m.Observe(ProcessSelectionStatus.Unique); // partial (below threshold)
        Assert.True(m.TryTransition(MonitoringState.Error));
        Assert.Equal(MonitoringState.Error, m.State);
    }

    [Fact]
    public void Successful_lifecycle_transition_resets_debounce()
    {
        var m = New();
        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique)); // partial

        Assert.True(m.TryTransition(MonitoringState.Stopped));
        Assert.Equal(MonitoringState.Stopped, m.State);

        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique)); // fresh counter
        Assert.Equal(MonitoringState.Searching, m.State);
    }

    [Fact]
    public void Unique_transitions_searching_to_connecting()
    {
        var m = New();
        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.True(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Connecting, m.State);
    }

    [Fact]
    public void Ambiguous_transitions_searching_to_ambiguous()
    {
        var m = New();
        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Ambiguous));
        Assert.True(m.Observe(ProcessSelectionStatus.Ambiguous));
        Assert.Equal(MonitoringState.Ambiguous, m.State);
    }

    [Fact]
    public void Unique_transitions_reconnecting_to_connecting()
    {
        var m = New();
        ToReconnecting(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.True(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Connecting, m.State);
    }

    [Fact]
    public void Ambiguous_transitions_reconnecting_to_ambiguous()
    {
        var m = New();
        ToReconnecting(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Ambiguous));
        Assert.True(m.Observe(ProcessSelectionStatus.Ambiguous));
        Assert.Equal(MonitoringState.Ambiguous, m.State);
    }

    [Fact]
    public void None_transitions_reconnecting_to_searching()
    {
        var m = New();
        ToReconnecting(m);
        Assert.False(m.Observe(ProcessSelectionStatus.None));
        Assert.True(m.Observe(ProcessSelectionStatus.None));
        Assert.Equal(MonitoringState.Searching, m.State);
    }

    [Fact]
    public void Unique_transitions_ambiguous_to_connecting()
    {
        var m = New();
        ToAmbiguous(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.True(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Connecting, m.State);
    }

    [Fact]
    public void None_transitions_ambiguous_to_searching()
    {
        var m = New();
        ToAmbiguous(m);
        Assert.False(m.Observe(ProcessSelectionStatus.None));
        Assert.True(m.Observe(ProcessSelectionStatus.None));
        Assert.Equal(MonitoringState.Searching, m.State);
    }

    [Fact]
    public void Alternating_observations_reset_counter()
    {
        var m = New();
        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.False(m.Observe(ProcessSelectionStatus.None));
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Searching, m.State);
    }

    [Fact]
    public void Stay_observations_saturate_and_reset_on_change()
    {
        var m = New();
        Start(m);
        Assert.False(m.Observe(ProcessSelectionStatus.None));
        Assert.False(m.Observe(ProcessSelectionStatus.None));
        Assert.False(m.Observe(ProcessSelectionStatus.None)); // saturated no-op
        Assert.Equal(MonitoringState.Searching, m.State);

        Assert.False(m.Observe(ProcessSelectionStatus.Unique)); // change resets
        Assert.True(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Connecting, m.State);
    }

    [Fact]
    public void Observe_in_disallowed_states_does_nothing()
    {
        var s = New();
        Assert.False(s.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Stopped, s.State);

        var c = New(); ToConnecting(c);
        Assert.False(c.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Connecting, c.State);

        var m = New(); ToMonitoring(m);
        Assert.False(m.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Monitoring, m.State);

        var e = New(); ToError(e);
        Assert.False(e.Observe(ProcessSelectionStatus.Unique));
        Assert.Equal(MonitoringState.Error, e.State);
    }

    [Fact]
    public void TryTransition_connecting_from_searching_is_rejected()
    {
        var m = New();
        Start(m);
        Assert.False(m.TryTransition(MonitoringState.Connecting));
        Assert.Equal(MonitoringState.Searching, m.State);
    }

    [Fact]
    public void TryTransition_ambiguous_from_searching_is_rejected()
    {
        var m = New();
        Start(m);
        Assert.False(m.TryTransition(MonitoringState.Ambiguous));
        Assert.Equal(MonitoringState.Searching, m.State);
    }

    [Fact]
    public void TryTransition_connecting_from_reconnecting_is_rejected()
    {
        var m = New();
        ToReconnecting(m);
        Assert.False(m.TryTransition(MonitoringState.Connecting));
        Assert.Equal(MonitoringState.Reconnecting, m.State);
    }

    [Fact]
    public void TryTransition_connecting_from_ambiguous_is_rejected()
    {
        var m = New();
        ToAmbiguous(m);
        Assert.False(m.TryTransition(MonitoringState.Connecting));
        Assert.Equal(MonitoringState.Ambiguous, m.State);
    }

    [Fact]
    public void Discovery_transitions_require_observe()
    {
        var m = New();
        Start(m);
        Assert.False(m.TryTransition(MonitoringState.Connecting));
        Assert.False(m.TryTransition(MonitoringState.Ambiguous));
        Assert.Equal(MonitoringState.Searching, m.State);

        Assert.False(m.Observe(ProcessSelectionStatus.Unique)); // below threshold
        Assert.True(m.Observe(ProcessSelectionStatus.Unique));  // stable -> Connecting
        Assert.Equal(MonitoringState.Connecting, m.State);
    }
}
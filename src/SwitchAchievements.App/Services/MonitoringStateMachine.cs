using SwitchAchievements.Core;

namespace SwitchAchievements.App.Services;

/// <summary>
/// Pure, single-consumer state machine for the monitoring lifecycle. It owns the
/// legal state transitions and the stable-observation debouncing used to accept
/// discovery results, but performs no process discovery, connection, session,
/// timing, or UI work of its own.
///
/// Transitions are split into two disjoint public surfaces:
/// <list type="bullet">
/// <item><see cref="TryTransition"/> handles explicit lifecycle operations
/// (start, stop, connection succeeded, disconnected, fatal error). It can never
/// enter <see cref="MonitoringState.Connecting"/> or
/// <see cref="MonitoringState.Ambiguous"/>.</item>
/// <item><see cref="Observe"/> is the only route for discovery-driven moves.
/// Those require <c>requiredStableObservations</c> consecutive identical
/// observations before they are applied.</item>
/// </list>
///
/// The machine is not thread-safe; the future monitoring service is its sole,
/// serialized driver.
/// </summary>
public sealed class MonitoringStateMachine
{
    public const int DefaultRequiredStableObservations = 2;

    private readonly int _requiredStableObservations;
    private MonitoringState _state = MonitoringState.Stopped;
    private ProcessSelectionStatus? _tracked;
    private int _count;

    public MonitoringStateMachine(int requiredStableObservations = DefaultRequiredStableObservations)
    {
        if (requiredStableObservations < 1)
            throw new ArgumentOutOfRangeException(
                nameof(requiredStableObservations),
                requiredStableObservations,
                "The required number of stable observations must be at least 1.");

        _requiredStableObservations = requiredStableObservations;
    }

    public MonitoringState State => _state;

    /// <summary>
    /// Applies an explicit lifecycle transition, returning false when it is not
    /// permitted from the current state. <see cref="MonitoringState.Connecting"/>
    /// and <see cref="MonitoringState.Ambiguous"/> are never valid targets here.
    /// </summary>
    public bool TryTransition(MonitoringState target)
    {
        if (target is MonitoringState.Connecting or MonitoringState.Ambiguous)
            return false;

        return IsLifecycleAllowed(_state, target) && TrySetState(target);
    }

    /// <summary>
    /// Feeds one discovery observation. Discovery-driven transitions are applied
    /// only after the configured number of consecutive identical observations.
    /// In states that do not perform discovery this is a no-op returning false.
    /// </summary>
    public bool Observe(ProcessSelectionStatus selection)
    {
        if (!IsDiscoveryState(_state))
            return false;

        if (_tracked == selection)
        {
            if (_count < _requiredStableObservations)
                _count++;
        }
        else
        {
            _tracked = selection;
            _count = 1;
        }

        if (_count < _requiredStableObservations)
            return false;

        var target = MapDiscovery(_state, selection);
        return target != _state && TrySetState(target);
    }

    private bool TrySetState(MonitoringState target)
    {
        _state = target;
        _tracked = null;
        _count = 0;
        return true;
    }

    private static bool IsDiscoveryState(MonitoringState state) =>
        state is MonitoringState.Searching or MonitoringState.Reconnecting or MonitoringState.Ambiguous;

    private static bool IsLifecycleAllowed(MonitoringState from, MonitoringState target) => target switch
    {
        MonitoringState.Searching => from == MonitoringState.Stopped,
        MonitoringState.Stopped => from is MonitoringState.Searching or MonitoringState.Connecting
            or MonitoringState.Monitoring or MonitoringState.Reconnecting or MonitoringState.Ambiguous or MonitoringState.Error,
        MonitoringState.Monitoring => from == MonitoringState.Connecting,
        MonitoringState.Reconnecting => from is MonitoringState.Connecting or MonitoringState.Monitoring,
        MonitoringState.Error => from is MonitoringState.Stopped or MonitoringState.Searching or MonitoringState.Connecting
            or MonitoringState.Monitoring or MonitoringState.Reconnecting or MonitoringState.Ambiguous,
        _ => false,
    };

    private static MonitoringState MapDiscovery(MonitoringState from, ProcessSelectionStatus selection) => (from, selection) switch
    {
        (MonitoringState.Searching, ProcessSelectionStatus.Unique) => MonitoringState.Connecting,
        (MonitoringState.Searching, ProcessSelectionStatus.Ambiguous) => MonitoringState.Ambiguous,
        (MonitoringState.Searching, ProcessSelectionStatus.None) => MonitoringState.Searching,

        (MonitoringState.Reconnecting, ProcessSelectionStatus.Unique) => MonitoringState.Connecting,
        (MonitoringState.Reconnecting, ProcessSelectionStatus.Ambiguous) => MonitoringState.Ambiguous,
        (MonitoringState.Reconnecting, ProcessSelectionStatus.None) => MonitoringState.Searching,

        (MonitoringState.Ambiguous, ProcessSelectionStatus.Unique) => MonitoringState.Connecting,
        (MonitoringState.Ambiguous, ProcessSelectionStatus.Ambiguous) => MonitoringState.Ambiguous,
        (MonitoringState.Ambiguous, ProcessSelectionStatus.None) => MonitoringState.Searching,

        _ => from,
    };
}
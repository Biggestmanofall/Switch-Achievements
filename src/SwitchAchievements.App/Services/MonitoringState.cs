namespace SwitchAchievements.App.Services;

/// <summary>
/// The lifecycle states of game monitoring. The future monitoring service owns
/// transitions between these states; <see cref="MonitoringStateMachine"/> is the
/// authoritative validator. Presentational layers consume this value and never
/// mutate it independently.
/// </summary>
public enum MonitoringState
{
    Stopped,
    Searching,
    Connecting,
    Monitoring,
    Reconnecting,
    Ambiguous,
    Error,
}
using SwitchAchievements.Core;

namespace SwitchAchievements.App.Services;

/// <summary>
/// The application-level monitoring pipeline: discovery, safe selection,
/// connection, subscription, snapshot fan-in, session processing, reconnection,
/// and shutdown. The implementation is the sole owner of the monitoring state
/// machine and the session manager, and is single-use (start once, then a
/// terminal stop/dispose).
/// </summary>
public interface IMonitoringService : IAsyncDisposable
{
    /// <summary>Current monitoring state (safe to read cross-thread).</summary>
    MonitoringState State { get; }

    /// <summary>
    /// Immutable snapshot of the current session (id + latest normalized
    /// snapshot), or <see cref="SessionSnapshot.None"/> when no session exists.
    /// </summary>
    SessionSnapshot Session { get; }

    /// <summary>Raised on the monitoring loop after every actual state change.</summary>
    event Action<MonitoringState>? StateChanged;

    Task StartAsync();

    Task StopAsync();
}
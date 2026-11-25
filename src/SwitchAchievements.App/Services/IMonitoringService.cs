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

    /// <summary>
    /// The game identification of the process the service is connecting to or
    /// monitoring (safe to read cross-thread; immutable). It is set as soon as that
    /// process has been identified — before <see cref="MonitoringState.Monitoring"/>
    /// is announced, and before any gameplay snapshot arrives — kept across
    /// reconnects to the same process, replaced when another process is
    /// identified, and cleared when the service enters
    /// <see cref="MonitoringState.Searching"/>, <see cref="MonitoringState.Error"/>,
    /// or <see cref="MonitoringState.Stopped"/>. Null when no process is being
    /// tracked or identification is unavailable (no identifier or catalog). Only
    /// <see cref="GameIdentification.Game"/> authorizes a supported game; Unknown,
    /// Unsupported, and Conflict results carry no game.
    /// </summary>
    GameIdentification? CurrentIdentification { get; }

    /// <summary>Raised on the monitoring loop after every actual state change.</summary>
    event Action<MonitoringState>? StateChanged;

    /// <summary>
    /// An explanation for the current state (M5.8.1), safe to read cross-thread. It is
    /// set only while <see cref="MonitoringState.Connecting"/> and belongs to the process
    /// being connected to: it is cleared when that process changes or disappears, when
    /// the state leaves Connecting (including Error and Stopped), and when the endpoint
    /// becomes available. The default (for implementations without diagnostics) is None.
    /// </summary>
    MonitoringDiagnostic Diagnostic => MonitoringDiagnostic.None;

    /// <summary>
    /// Raised on the monitoring loop after <see cref="Diagnostic"/> changes. Separate
    /// from <see cref="StateChanged"/>, which reports only actual state transitions.
    /// </summary>
    event Action? DiagnosticChanged
    {
        add { }
        remove { }
    }

    Task StartAsync();

    Task StopAsync();
}
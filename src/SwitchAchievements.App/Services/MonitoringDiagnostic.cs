namespace SwitchAchievements.App.Services;

/// <summary>
/// An explanation shown alongside the monitoring state (M5.8.1). It never replaces
/// or changes the state, and it belongs to one specific game process.
/// </summary>
public enum MonitoringDiagnostic
{
    None,

    /// <summary>
    /// The selected game process has been running past the startup grace period and
    /// its live-data endpoint (Longshot's control bridge) does not exist, so no
    /// connection is attempted. The state stays <see cref="MonitoringState.Connecting"/>.
    /// </summary>
    LiveTrackingDisabled,
}

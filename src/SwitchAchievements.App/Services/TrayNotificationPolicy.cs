using System.Runtime.CompilerServices;
using SwitchAchievements.Core;

namespace SwitchAchievements.App.Services;

public enum TrayNotificationKind
{
    SupportedGameDetected,
    UnsupportedGameDetected,
    MonitoringError,
    RestartFailed,
}

/// <summary>A native tray notification to show.</summary>
public sealed record TrayNotification(TrayNotificationKind Kind, string Title, string Text)
{
    public bool IsError => Kind is TrayNotificationKind.MonitoringError or TrayNotificationKind.RestartFailed;
}

/// <summary>
/// Decides which monitoring observations become tray notifications (M5.7 Task 4).
/// WPF-free and deterministic; called in event order on one thread (the UI thread).
///
/// <para>Rules:</para>
/// <list type="bullet">
/// <item>Supported game: <see cref="MonitoringState.Monitoring"/> with an
/// <see cref="IdentificationStatus.Identified"/> result for a process fingerprint
/// (PID + start time) not seen before.</item>
/// <item>Unsupported game: the same, with <see cref="IdentificationStatus.Unsupported"/>.</item>
/// <item>Error: the first <see cref="MonitoringState.Error"/> observed for a given service instance.</item>
/// <item>Unknown and Conflict results, and every other state, never notify.</item>
/// </list>
///
/// <para>An eligible event is <em>consumed</em> when it is first observed, whether or
/// not a notification is shown: an event seen while the window is visible is never
/// replayed later. Repeated observations (reconnects to the same process, repeated
/// evaluations after a service change, a restart that re-identifies the same running
/// process) are therefore harmless.</para>
/// </summary>
public sealed class TrayNotificationPolicy
{
    private readonly HashSet<ProcessFingerprint> _announcedProcesses = new();
    private readonly ConditionalWeakTable<object, object> _errorServices = new();

    /// <summary>
    /// The approved visibility rule: notifications are shown only while the main window
    /// is hidden or minimized.
    /// </summary>
    public static bool IsWindowHidden(bool isVisible, bool isMinimized) => !isVisible || isMinimized;

    /// <summary>
    /// Observes one monitoring event and returns the notification to show, or null.
    /// </summary>
    /// <param name="service">The monitoring service instance the event came from.</param>
    /// <param name="state">The state the service reported.</param>
    /// <param name="identification">The service's identification captured with the event.</param>
    /// <param name="windowHidden">Whether the main window is hidden or minimized now.</param>
    public TrayNotification? Observe(object service, MonitoringState state, GameIdentification? identification, bool windowHidden)
    {
        ArgumentNullException.ThrowIfNull(service);

        var notification = state switch
        {
            MonitoringState.Monitoring => ObserveGame(identification),
            MonitoringState.Error => ObserveError(service),
            _ => null,
        };
        return windowHidden ? notification : null;
    }

    /// <summary>The notification for a failed restart attempt, or null (only while the window is hidden).</summary>
    public static TrayNotification? ForRestartOutcome(RestartOutcome outcome, bool windowHidden)
    {
        if (!windowHidden || outcome is not (RestartOutcome.Failed or RestartOutcome.Refused))
            return null;
        return new TrayNotification(
            TrayNotificationKind.RestartFailed,
            "Restart failed",
            "Monitoring could not be restarted. You can try again from the tray menu.");
    }

    private TrayNotification? ObserveGame(GameIdentification? identification)
    {
        if (identification is null)
            return null;

        TrayNotification notification;
        if (identification.IsIdentified)
        {
            notification = new TrayNotification(
                TrayNotificationKind.SupportedGameDetected,
                "Game detected",
                identification.Game!.DisplayName + " is being monitored.");
        }
        else if (identification.Status == IdentificationStatus.Unsupported)
        {
            notification = new TrayNotification(
                TrayNotificationKind.UnsupportedGameDetected,
                "Unsupported game",
                "The running game or version is not supported by Switch Achievements.");
        }
        else
        {
            return null; // Unknown and Conflict are diagnostics, not notifications
        }

        // Consumed on first sight; the same process instance never announces twice.
        return _announcedProcesses.Add(identification.Evidence.Process) ? notification : null;
    }

    private TrayNotification? ObserveError(object service)
    {
        if (_errorServices.TryGetValue(service, out _))
            return null;
        _errorServices.Add(service, service);
        return new TrayNotification(
            TrayNotificationKind.MonitoringError,
            "Monitoring error",
            "Game monitoring stopped because of an error. Use Restart monitoring in the tray menu to try again.");
    }
}

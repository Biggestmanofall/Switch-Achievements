namespace SwitchAchievements.App.Services;

/// <summary>What the tray icon and its menu show.</summary>
/// <param name="StatusText">The live status line (the disabled status menu item).</param>
/// <param name="Tooltip">The icon tooltip; at most <see cref="TrayStatus.MaxTooltipLength"/> characters.</param>
/// <param name="RestartVisible">Whether "Restart monitoring" is in the menu.</param>
/// <param name="RestartEnabled">Whether "Restart monitoring" can be clicked.</param>
public sealed record TrayViewState(string StatusText, string Tooltip, bool RestartVisible, bool RestartEnabled);

/// <summary>
/// Computes the tray's view state (M5.7 Task 4). WPF- and WinForms-free. The status
/// text is the dashboard's <c>StatusSummary</c>; the only thing the tray adds is the
/// failed-restart recovery state, in which the host has no service (so the dashboard
/// truthfully shows Stopped) but the user still needs a way to retry.
/// </summary>
public static class TrayStatus
{
    public const string ApplicationName = "Switch Achievements";

    /// <summary>Status shown while a failed restart left monitoring without a service.</summary>
    public const string RestartFailedSummary = "Monitoring error — restart failed";

    /// <summary>The longest text WinForms accepts for <c>NotifyIcon.Text</c>.</summary>
    public const int MaxTooltipLength = 127;

    /// <param name="state">The dashboard's monitoring state.</param>
    /// <param name="summary">The dashboard's status summary.</param>
    /// <param name="restartInProgress">A restart attempt is running.</param>
    /// <param name="restartFailed">A previous restart failed and left no monitoring service (recovery state).</param>
    public static TrayViewState Compute(MonitoringState state, string summary, bool restartInProgress, bool restartFailed)
    {
        var status = restartFailed ? RestartFailedSummary : summary ?? string.Empty;

        // Visible during an attempt too, so the item does not vanish while the old
        // service is being replaced (the state briefly reads Stopped); disabled meanwhile.
        var restartVisible = state == MonitoringState.Error || restartFailed || restartInProgress;
        return new TrayViewState(
            StatusText: status,
            Tooltip: Tooltip(status),
            RestartVisible: restartVisible,
            RestartEnabled: restartVisible && !restartInProgress);
    }

    /// <summary>"Switch Achievements — {status}", shortened with an ellipsis to fit the tooltip limit.</summary>
    public static string Tooltip(string status)
    {
        var text = string.IsNullOrEmpty(status) ? ApplicationName : ApplicationName + " — " + status;
        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }
}

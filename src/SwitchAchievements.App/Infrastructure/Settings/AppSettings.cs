namespace SwitchAchievements.App.Infrastructure.Settings;

/// <summary>
/// Persisted application settings. Deliberately small; unknown JSON keys are
/// tolerated so adding a field later does not break existing config files.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Display the main window when the application launches.</summary>
    public bool ShowWindowOnLaunch { get; set; } = true;

    /// <summary>Minimize to the system tray when the main window is closed.</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;
}

namespace SwitchAchievements.App;

/// <summary>What closing the main window does.</summary>
public enum MainWindowCloseAction
{
    /// <summary>Let the close complete (the application is already shutting down).</summary>
    Close,

    /// <summary>Cancel the close and hide to the tray; monitoring keeps running.</summary>
    HideToTray,

    /// <summary>Cancel the close and request the application's coordinated exit.</summary>
    RequestExit,

    /// <summary>No coordinator is attached: close and shut the application down directly.</summary>
    ShutdownDirectly,
}

/// <summary>
/// The main window's close decision, kept free of WPF so it can be tested. The setting
/// must be read when the close happens (from the active settings), never cached.
/// </summary>
public static class MainWindowClosePolicy
{
    public static MainWindowCloseAction Decide(bool allowClose, bool minimizeToTrayOnClose, bool exitHandlerAttached)
    {
        if (allowClose)
            return MainWindowCloseAction.Close;
        if (minimizeToTrayOnClose)
            return MainWindowCloseAction.HideToTray;
        return exitHandlerAttached ? MainWindowCloseAction.RequestExit : MainWindowCloseAction.ShutdownDirectly;
    }
}

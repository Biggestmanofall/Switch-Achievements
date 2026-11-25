using System.Globalization;
using SwitchAchievements.App.Services;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.App.ViewModels;

/// <summary>Connection to the game's control bridge, derived from the monitoring state.</summary>
public enum ConnectionStatus
{
    /// <summary>No bridge connection: Searching, Ambiguous, Error, or Stopped (discovery is not a connection).</summary>
    NotConnected,

    /// <summary>Connecting or Reconnecting.</summary>
    Connecting,

    /// <summary>Monitoring.</summary>
    Connected,
}

/// <summary>Presentation-level game identification status.</summary>
public enum IdentificationDisplayStatus
{
    /// <summary>No game process is being tracked.</summary>
    None,

    /// <summary>A process is being connected to and has not been identified yet.</summary>
    Identifying,

    /// <summary>Identified as a supported game (the only status that carries a game).</summary>
    Identified,

    /// <summary>The game could not be recognized.</summary>
    Unknown,

    /// <summary>The game or its version is not supported.</summary>
    Unsupported,

    /// <summary>The identity evidence was contradictory.</summary>
    Conflict,

    /// <summary>Identification is impossible: the supported-game catalog failed to load.</summary>
    Unavailable,
}

/// <summary>Whether gameplay data is available and how recent it is.</summary>
public enum GameplayDataStatus
{
    /// <summary>No gameplay data and none expected (not connected).</summary>
    None,

    /// <summary>Connected, but no gameplay snapshot has arrived yet (title screen, menus).</summary>
    Waiting,

    /// <summary>A recent gameplay snapshot is available.</summary>
    Fresh,

    /// <summary>No new snapshot for at least <see cref="DashboardFormat.StaleThreshold"/>.</summary>
    Stale,
}

/// <summary>
/// Deterministic, WPF-independent text formatting for the dashboard (M5.7 Task 2).
/// </summary>
public static class DashboardFormat
{
    /// <summary>
    /// Time without a new gameplay snapshot after which data is shown as stale. Snapshots
    /// arrive about once a second during gameplay but stop entirely on the title screen,
    /// while paused, loading, or in menus, so staleness is only a gentle hint.
    /// </summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(2);

    public const string Missing = "—";

    public const string StaleHint = "No recent gameplay updates — the game may be paused, loading, or in a menu.";

    /// <summary>M5.8.1: the status heading while live tracking is disabled.</summary>
    public const string LiveTrackingDisabled = "Live tracking disabled";

    /// <summary>M5.8.1: the connection text while live tracking is disabled (no live connection exists).</summary>
    public const string LiveTrackingOff = "Live tracking off";

    /// <summary>M5.8.1: the freshness strip's text while live tracking is disabled.</summary>
    public const string LiveTrackingDisabledFreshness = "Live tracking is disabled for this game.";

    /// <summary>M5.8.1: the one-line summary (tray) while live tracking is disabled.</summary>
    public const string LiveTrackingDisabledSummary = "Live tracking disabled for this game";

    /// <summary>M5.8.1: why there is no live data.</summary>
    public const string LiveTrackingDisabledHint =
        "Longshot's live data bridge isn't enabled for this game, so gameplay data can't be read. It can't be enabled while the game is running.";

    /// <summary>M5.8.2: the action's label.</summary>
    public const string EnableLiveTrackingAction = "Enable live tracking";

    /// <summary>
    /// M5.8.2: what an "Enable live tracking" result means for the user, shown in the hint
    /// row; null for a cancellation (shown nowhere). No paths or exception text.
    /// </summary>
    public static string? LiveTrackingSetup(LiveTrackingSetupResult result, string gameName) => result switch
    {
        LiveTrackingSetupResult.Enabled =>
            $"Live tracking is enabled for the next launch. Restart {gameName} to start tracking — the in-game Restart option also works.",
        LiveTrackingSetupResult.AlreadyEnabled =>
            "Live tracking is already enabled in longshot_env.txt. Restart the game to start tracking. If it's still off after a restart, another HOS_CONTROL setting may be taking priority.",
        LiveTrackingSetupResult.AlreadyEnabledByEnvironment =>
            "Live tracking is already enabled by a Windows environment variable (HOS_CONTROL). Restart the game to start tracking.",
        LiveTrackingSetupResult.EnvironmentConflict =>
            "A Windows environment variable already sets HOS_CONTROL differently, and it takes priority over longshot_env.txt. Nothing was changed.",
        LiveTrackingSetupResult.EnvironmentUnreadable =>
            "Your Windows environment settings couldn't be checked, so nothing was changed. Try again.",
        LiveTrackingSetupResult.FileConflict =>
            "longshot_env.txt already sets HOS_CONTROL to a different value. Switch Achievements didn't change it.",
        LiveTrackingSetupResult.GameFolderUnavailable =>
            "The game's folder couldn't be verified (for example, it's on a network drive or isn't a standard Longshot install). Nothing was changed.",
        LiveTrackingSetupResult.NotEligible =>
            "Live tracking can only be enabled for an identified, supported game. Nothing was changed.",
        LiveTrackingSetupResult.ProcessChanged =>
            "The game changed before the setting could be saved. Nothing was changed — try again.",
        LiveTrackingSetupResult.FileUnsupported =>
            "longshot_env.txt isn't in a form Switch Achievements can safely edit (for example, it's very large, uses UTF-16, or is a link). Nothing was changed.",
        LiveTrackingSetupResult.AccessDenied =>
            "Windows denied access to longshot_env.txt (it may be read-only). Nothing was changed.",
        LiveTrackingSetupResult.FileInUse =>
            "longshot_env.txt is open in another program. Close it and try again. Nothing was changed.",
        LiveTrackingSetupResult.ReadFailed =>
            "longshot_env.txt couldn't be read. Nothing was changed.",
        LiveTrackingSetupResult.WriteFailed =>
            "The setting couldn't be saved; longshot_env.txt was left as it was. Try again.",
        LiveTrackingSetupResult.RecoveryFailed =>
            "Warning: saving the setting failed and longshot_env.txt couldn't be verified as restored. It may be partly changed — check the end of the file before trying again.",
        _ => null,
    };

    public static string MonitoringState(MonitoringState state) => state switch
    {
        Services.MonitoringState.Stopped => "Stopped",
        Services.MonitoringState.Searching => "Searching for game",
        Services.MonitoringState.Connecting => "Connecting",
        Services.MonitoringState.Monitoring => "Monitoring",
        Services.MonitoringState.Reconnecting => "Reconnecting",
        Services.MonitoringState.Ambiguous => "Multiple game processes found",
        Services.MonitoringState.Error => "Error",
        _ => "Unknown",
    };

    public static ConnectionStatus Connection(MonitoringState state) => state switch
    {
        Services.MonitoringState.Monitoring => ConnectionStatus.Connected,
        Services.MonitoringState.Connecting or Services.MonitoringState.Reconnecting => ConnectionStatus.Connecting,
        _ => ConnectionStatus.NotConnected,
    };

    public static string Connection(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => "Connected",
        ConnectionStatus.Connecting => "Connecting",
        _ => "Not connected",
    };

    /// <summary>Generic identification text. Only <paramref name="identifiedName"/> (an identified game's catalog name) names a game.</summary>
    public static string Identification(IdentificationDisplayStatus status, string? identifiedName = null) => status switch
    {
        IdentificationDisplayStatus.Identified when identifiedName is not null => identifiedName,
        IdentificationDisplayStatus.Identifying => "Identifying game…",
        IdentificationDisplayStatus.Unknown => "Unrecognized game",
        IdentificationDisplayStatus.Unsupported => "Unsupported game or version",
        IdentificationDisplayStatus.Conflict => "Game could not be identified",
        IdentificationDisplayStatus.Unavailable => "Game identification unavailable",
        _ => "No game detected",
    };

    /// <summary>
    /// Formats quarter-heart life values as hearts with exact quarter fractions, e.g.
    /// (11, 12) → "2¾ / 3 hearts". Missing or invalid life shows <see cref="Missing"/>;
    /// a missing maximum shows the current value alone.
    /// </summary>
    public static string Hearts(int? life, int? maxLife)
    {
        if (life is not { } current || current < 0)
            return Missing;
        if (maxLife is not { } max || max < 0)
            return Hearts(current) + (current == 4 ? " heart" : " hearts");
        return Hearts(current) + " / " + Hearts(max) + (max == 4 ? " heart" : " hearts");
    }

    /// <summary>A non-negative quarter-heart count as hearts, e.g. 9 → "2¼", 2 → "½", 0 → "0".</summary>
    public static string Hearts(int quarterHearts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quarterHearts);
        var whole = quarterHearts / 4;
        var fraction = (quarterHearts % 4) switch { 1 => "¼", 2 => "½", 3 => "¾", _ => "" };
        if (fraction.Length == 0)
            return whole.ToString(CultureInfo.InvariantCulture);
        return whole == 0 ? fraction : whole.ToString(CultureInfo.InvariantCulture) + fraction;
    }

    /// <summary>A human-readable elapsed time, e.g. "12 seconds", "1 minute", "2 hours 5 minutes".</summary>
    public static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed < TimeSpan.FromMinutes(1))
            return Unit((int)elapsed.TotalSeconds, "second");
        if (elapsed < TimeSpan.FromHours(1))
            return Unit((int)elapsed.TotalMinutes, "minute");

        var hours = Unit((int)elapsed.TotalHours, "hour");
        return elapsed.Minutes == 0 ? hours : hours + " " + Unit(elapsed.Minutes, "minute");
    }

    /// <summary>A session duration as h:mm:ss, e.g. "1:05:09"; negative values show as "0:00:00".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}");
    }

    public static string Freshness(GameplayDataStatus status, TimeSpan sinceLastSnapshot) => status switch
    {
        GameplayDataStatus.Fresh or GameplayDataStatus.Stale => sinceLastSnapshot < TimeSpan.FromSeconds(1)
            ? "Last gameplay update just now"
            : "Last gameplay update " + Elapsed(sinceLastSnapshot) + " ago",
        GameplayDataStatus.Waiting => "Waiting for gameplay data…",
        _ => "No gameplay data",
    };

    /// <summary>
    /// A concise one-line status for the tray (Task 4). A diagnostic (M5.8.1), which the
    /// service only reports while Connecting, takes the place of the state's text.
    /// </summary>
    public static string StatusSummary(
        MonitoringState state,
        IdentificationDisplayStatus identification,
        string? identifiedName,
        MonitoringDiagnostic diagnostic = MonitoringDiagnostic.None)
        => diagnostic == MonitoringDiagnostic.LiveTrackingDisabled && state == Services.MonitoringState.Connecting
            ? LiveTrackingDisabledSummary
            : StateSummary(state, identification, identifiedName);

    private static string StateSummary(MonitoringState state, IdentificationDisplayStatus identification, string? identifiedName) => state switch
    {
        Services.MonitoringState.Stopped => "Monitoring stopped",
        Services.MonitoringState.Searching => "Searching for Longshot",
        Services.MonitoringState.Connecting => "Connecting to Longshot",
        Services.MonitoringState.Reconnecting => "Reconnecting to Longshot",
        Services.MonitoringState.Ambiguous => "Multiple game processes found",
        Services.MonitoringState.Error => "Monitoring error",
        Services.MonitoringState.Monitoring => identification switch
        {
            IdentificationDisplayStatus.Identified when identifiedName is not null => "Monitoring — " + identifiedName,
            IdentificationDisplayStatus.Unknown => "Monitoring — unrecognized game",
            IdentificationDisplayStatus.Unsupported => "Monitoring — unsupported game",
            IdentificationDisplayStatus.Conflict => "Monitoring — game could not be identified",
            _ => "Monitoring Longshot",
        },
        _ => "Unknown",
    };

    private static string Unit(int value, string unit)
        => value.ToString(CultureInfo.InvariantCulture) + " " + unit + (value == 1 ? "" : "s");
}

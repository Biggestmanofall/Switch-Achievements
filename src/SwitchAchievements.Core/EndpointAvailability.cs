namespace SwitchAchievements.Core;

/// <summary>
/// Whether an integration's transport endpoint (for example a named pipe) currently
/// exists, as observed without connecting to it (M5.8.1).
/// </summary>
public enum EndpointAvailability
{
    /// <summary>Not known (no check, or the check failed): connect as usual.</summary>
    Unknown,

    /// <summary>The endpoint exists: connect as usual.</summary>
    Available,

    /// <summary>The endpoint does not exist: a connection attempt cannot succeed now.</summary>
    Absent,
}

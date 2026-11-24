namespace SwitchAchievements.Core;

/// <summary>
/// Unique identifier for a single run of a game. A new id is issued whenever a
/// new game process is detected, so that state is never compared across restarts.
/// </summary>
public readonly record struct SessionId(Guid Value)
{
    public static SessionId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>Lifecycle phase of a game session.</summary>
public enum SessionPhase
{
    /// <summary>The process was detected but no snapshot has arrived yet.</summary>
    Starting,

    /// <summary>Snapshots are being received normally.</summary>
    Sampling,

    /// <summary>The connection dropped; waiting for a reconnect.</summary>
    Disconnected,

    /// <summary>The session is over (for example the game exited).</summary>
    Ended,
}

/// <summary>
/// Tracks one run of a game. <see cref="Baseline"/> is the comparison reference —
/// the snapshot the next snapshot is compared against. It is set to the first
/// snapshot of the session, reset to the first snapshot after a reconnect, and
/// updated after each comparison. State is never compared across sessions.
/// </summary>
public sealed class GameSession
{
    public required SessionId Id { get; init; }

    public required GameId GameId { get; init; }

    /// <summary>The process instance hosting this session (its identity).</summary>
    public required ProcessFingerprint Fingerprint { get; init; }

    public SessionPhase Phase { get; set; } = SessionPhase.Starting;

    /// <summary>The comparison reference (see the class summary).</summary>
    public NormalizedSnapshot? Baseline { get; set; }
}
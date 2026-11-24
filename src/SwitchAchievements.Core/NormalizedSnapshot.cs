namespace SwitchAchievements.Core;

/// <summary>
/// A single sample of game state, converted into one consistent, integration-
/// neutral shape. This is what the achievement engine and the desktop UI work
/// with; neither ever sees integration-specific JSON.
///
/// A snapshot does not carry session identity; the session layer assigns that
/// from the process fingerprint (see <see cref="GameSession"/>).
/// </summary>
public sealed class NormalizedSnapshot
{
    /// <summary>Which integration produced this snapshot, e.g. "longshot".</summary>
    public required string IntegrationId { get; init; }

    /// <summary>Which game this snapshot is for (a canonical id, not a file path).</summary>
    public required GameId GameId { get; init; }

    /// <summary>Operating-system process id of the running game.</summary>
    public int SourceProcessId { get; init; }

    /// <summary>When that process started (UTC); combined with process id to form a session fingerprint.</summary>
    public DateTime SourceProcessStartUtc { get; init; }

    /// <summary>The process fingerprint (process id + start time) this snapshot came from.</summary>
    public ProcessFingerprint Fingerprint => new(SourceProcessId, SourceProcessStartUtc);

    /// <summary>The game's own frame number at sampling time.</summary>
    public long GameFrame { get; init; }

    /// <summary>Local wall-clock time when this snapshot was received.</summary>
    public DateTime ReceivedUtc { get; init; }

    // ---- Game state, expressed generically ----
    //
    // Nullability policy (see docs/m2-snapshot-null-policy.md): a null value
    // always means "no value was sampled in this snapshot" — never a concrete
    // gameplay value. So Life=null is not 0 hearts, EventPlaying=null is not
    // false, and Map=null is not an empty map. The comparer only reports a
    // change when both snapshots carry a concrete value.

    /// <summary>Current area/map name; null when the map has not been sampled yet.</summary>
    public string? Map { get; init; }

    /// <summary>Player age, e.g. "child" or "adult"; null when not yet sampled.</summary>
    public string? Age { get; init; }

    /// <summary>True while a cutscene/event is playing; null when Link is not being sampled.</summary>
    public bool? EventPlaying { get; init; }

    /// <summary>True while a map is loading; null when Link is not being sampled.</summary>
    public bool? Loading { get; init; }

    /// <summary>Current life (quarter-hearts); null when not sampled, never 0-as-null.</summary>
    public int? Life { get; init; }

    /// <summary>Maximum life (quarter-hearts); null when not sampled.</summary>
    public int? MaxLife { get; init; }

    /// <summary>Current rupee count; null when not sampled, never 0-as-null.</summary>
    public int? Rupees { get; init; }

    /// <summary>
    /// Explicitly-watched named flags, not the noisy bridge hash stream.
    /// Null means no flag payload was sampled; an individual null value means
    /// that flag could not be read as a number (enum, non-numeric String64, or
    /// absent/unreadable).
    /// </summary>
    public IReadOnlyDictionary<string, long?>? Flags { get; init; }
}
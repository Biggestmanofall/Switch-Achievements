namespace SwitchAchievements.Core;

/// <summary>
/// A single detected change in one field, strictly within one session. The
/// achievement engine evaluates these in Phase 2; the desktop UI can also use
/// them to show status updates.
/// </summary>
public sealed class GameStateChange
{
    public required SessionId SessionId { get; init; }

    /// <summary>Name of the field that changed, e.g. "Rupees" or "flag:PlayerStatus.Life".</summary>
    public required string Field { get; init; }

    /// <summary>The value before the change (an int/long/bool/string, or null).</summary>
    public object? PreviousValue { get; init; }

    /// <summary>The value after the change.</summary>
    public object? NewValue { get; init; }

    /// <summary>The game frame at which the change was observed.</summary>
    public long GameFrame { get; init; }

    /// <summary>When the change was observed (UTC).</summary>
    public DateTime ObservedUtc { get; init; }
}
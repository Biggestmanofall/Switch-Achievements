namespace SwitchAchievements.Core;

/// <summary>
/// Canonical identifier for a supported game. This is intentionally independent
/// of file paths, build hashes, and individual installations so the same game
/// can be recognized consistently across machines and integrations.
/// </summary>
public sealed record GameId(string Value)
{
    /// <summary>A sentinel used before a game has been positively identified.</summary>
    public static readonly GameId Unknown = new("unknown");

    public override string ToString() => Value;
}
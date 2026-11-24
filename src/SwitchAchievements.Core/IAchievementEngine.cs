namespace SwitchAchievements.Core;

/// <summary>
/// Consumes normalized game state and evaluates achievement definitions.
/// Deliberately independent of any game integration: it only ever sees
/// <see cref="NormalizedSnapshot"/>, never Longshot or pipe details.
///
/// Not implemented in Phase 1. This is a placeholder seam so the achievement
/// engine can be added in Phase 2 without touching the integrations.
/// </summary>
public interface IAchievementEngine
{
    // Defined in Phase 2: session start/end hooks, achievement evaluation, unlock events.
}
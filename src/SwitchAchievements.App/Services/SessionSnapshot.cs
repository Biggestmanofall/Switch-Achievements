using SwitchAchievements.Core;

namespace SwitchAchievements.App.Services;

/// <summary>
/// An immutable, cross-thread-safe view of the current game session. Exposes only
/// the session id and the latest normalized snapshot — never the mutable
/// <see cref="GameSession"/> owned by <see cref="SessionManager"/>.
/// </summary>
public sealed record SessionSnapshot(SessionId? Id, NormalizedSnapshot? Latest)
{
    public static readonly SessionSnapshot None = new(null, null);
}
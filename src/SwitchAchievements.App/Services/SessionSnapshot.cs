using SwitchAchievements.Core;

namespace SwitchAchievements.App.Services;

/// <summary>
/// An immutable, cross-thread-safe view of the current game session. Exposes only
/// the session id, the latest normalized snapshot, and the game identification
/// bound to that snapshot's process — never the mutable <see cref="GameSession"/>
/// owned by <see cref="SessionManager"/>.
/// <para><see cref="Identification"/> is non-null only when identification was
/// attempted for the same process fingerprint as <see cref="Latest"/> and produced a
/// result. Only <see cref="GameIdentification.Game"/> (an <see cref="IdentifiedGame"/>)
/// authorizes achievement evaluation; <see cref="NormalizedSnapshot.GameId"/> is a label.</para>
/// <para><see cref="StartedUtc"/> (M5.7) is when the session began: the receive time of
/// the snapshot that created it. It is kept for the whole session — including
/// same-process reconnects, which keep the session id — and is null without a session.</para>
/// </summary>
public sealed record SessionSnapshot(
    SessionId? Id,
    NormalizedSnapshot? Latest,
    GameIdentification? Identification = null,
    DateTime? StartedUtc = null)
{
    public static readonly SessionSnapshot None = new(null, null);
}
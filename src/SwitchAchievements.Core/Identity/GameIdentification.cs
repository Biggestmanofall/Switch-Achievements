namespace SwitchAchievements.Core;

/// <summary>
/// The result of <see cref="GameIdentityResolver.Resolve"/>: a status, the
/// evidence it was based on (retained for diagnostics), deterministic diagnostic
/// lines, and — only when <see cref="Status"/> is
/// <see cref="IdentificationStatus.Identified"/> — the <see cref="IdentifiedGame"/>.
///
/// Invariants (enforced by the non-public construction path):
/// <list type="bullet">
/// <item><see cref="IdentificationStatus.Identified"/> always carries a game.</item>
/// <item><see cref="IdentificationStatus.Unsupported"/>, <see cref="IdentificationStatus.Unknown"/>,
/// and <see cref="IdentificationStatus.Conflict"/> never do, and therefore can never
/// authorize achievement evaluation.</item>
/// </list>
/// Instances are immutable; there are no public constructors or setters.
/// </summary>
public sealed class GameIdentification
{
    private GameIdentification(
        IdentificationStatus status,
        IdentifiedGame? game,
        GameIdentityEvidence evidence,
        IReadOnlyList<string> diagnostics)
    {
        Status = status;
        Game = game;
        Evidence = evidence;
        Diagnostics = diagnostics;
    }

    public IdentificationStatus Status { get; }

    /// <summary>The authorization object; non-null only when <see cref="Status"/> is Identified.</summary>
    public IdentifiedGame? Game { get; }

    /// <summary>The evidence the result was based on.</summary>
    public GameIdentityEvidence Evidence { get; }

    /// <summary>Deterministic diagnostic lines (no file-system paths or raw file contents).</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>True only for an identified, supported game.</summary>
    public bool IsIdentified => Status == IdentificationStatus.Identified && Game is not null;

    internal static GameIdentification Identified(
        IdentifiedGame game,
        GameIdentityEvidence evidence,
        IEnumerable<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(evidence);
        return new GameIdentification(IdentificationStatus.Identified, game, evidence, Freeze(diagnostics));
    }

    internal static GameIdentification NotIdentified(
        IdentificationStatus status,
        GameIdentityEvidence evidence,
        IEnumerable<string> diagnostics)
    {
        if (status == IdentificationStatus.Identified || !Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "A non-identified result needs Unsupported, Unknown, or Conflict.");
        ArgumentNullException.ThrowIfNull(evidence);
        return new GameIdentification(status, null, evidence, Freeze(diagnostics));
    }

    private static IReadOnlyList<string> Freeze(IEnumerable<string> diagnostics)
        => Array.AsReadOnly((diagnostics ?? Array.Empty<string>()).ToArray());
}

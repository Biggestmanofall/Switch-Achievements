namespace SwitchAchievements.Core;

/// <summary>
/// Collects identity evidence for one already-selected game process. Each
/// integration supplies its own implementation (with its own fact keys and file
/// formats); Core only resolves the resulting <see cref="GameIdentityEvidence"/>
/// against the catalog (<see cref="GameIdentityResolver"/>).
///
/// Contract for implementations:
/// <list type="bullet">
/// <item>Use only the installation of the given <paramref name="selected"/> process
/// (derived from its executable path). Never search other directories or fall back
/// to a configured location.</item>
/// <item>Bind the evidence to the process: set
/// <see cref="GameIdentityEvidence.Process"/> to the selected process's fingerprint.</item>
/// <item>Keep execution bounded and honour <paramref name="ct"/>; throw
/// <see cref="OperationCanceledException"/> only when it is cancelled.</item>
/// <item>Report every other failure as an <see cref="EvidenceIssue"/> (omitting the
/// affected fact) instead of throwing, and report disagreeing sources as
/// <see cref="EvidenceIssueKind.Conflict"/>.</item>
/// <item>Emit only canonical fact values, and never put file-system paths or raw
/// file contents into facts or issue messages.</item>
/// </list>
/// </summary>
public interface IGameIdentityProvider
{
    Task<GameIdentityEvidence> CollectAsync(GameProcessInfo selected, CancellationToken ct);
}

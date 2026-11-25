using System.Collections.ObjectModel;

namespace SwitchAchievements.Core;

/// <summary>
/// Identity evidence collected by one integration for one selected process
/// instance. Facts are namespaced key/value pairs (e.g. <c>switch2.programId</c>)
/// whose values are already canonical: Core compares them ordinally and never
/// normalises or interprets them.
///
/// The constructor only makes defensive, read-only copies; it does not judge the
/// facts. Invalid keys, values, integration ids, or fingerprints are detected by
/// <see cref="GameIdentityResolver"/>, which resolves such evidence to
/// <see cref="IdentificationStatus.Unknown"/>, so a misbehaving provider fails safe.
/// </summary>
public sealed class GameIdentityEvidence
{
    private static readonly IReadOnlyDictionary<string, string> NoFacts =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    public GameIdentityEvidence(
        string integrationId,
        ProcessFingerprint process,
        IReadOnlyDictionary<string, string>? facts,
        IReadOnlyList<EvidenceIssue>? issues,
        DateTime collectedUtc)
    {
        IntegrationId = integrationId ?? throw new ArgumentNullException(nameof(integrationId));
        Process = process;
        CollectedUtc = collectedUtc;

        if (facts is null || facts.Count == 0)
        {
            Facts = NoFacts;
        }
        else
        {
            var copy = new Dictionary<string, string>(facts.Count, StringComparer.Ordinal);
            foreach (var (key, value) in facts)
                copy[key] = value;
            Facts = new ReadOnlyDictionary<string, string>(copy);
        }

        if (issues is null || issues.Count == 0)
        {
            Issues = Array.Empty<EvidenceIssue>();
        }
        else
        {
            var copy = new EvidenceIssue[issues.Count];
            for (var i = 0; i < issues.Count; i++)
                copy[i] = issues[i] ?? throw new ArgumentException("Evidence issues must not contain null.", nameof(issues));
            Issues = Array.AsReadOnly(copy);
        }
    }

    /// <summary>The integration that collected the evidence, e.g. <c>longshot</c>.</summary>
    public string IntegrationId { get; }

    /// <summary>The process instance the evidence describes.</summary>
    public ProcessFingerprint Process { get; }

    /// <summary>Canonical identity facts (read-only copy, ordinal keys).</summary>
    public IReadOnlyDictionary<string, string> Facts { get; }

    /// <summary>Problems met while collecting (read-only copy, provider order).</summary>
    public IReadOnlyList<EvidenceIssue> Issues { get; }

    /// <summary>When the evidence was collected (UTC). Informational; never used for matching.</summary>
    public DateTime CollectedUtc { get; }
}

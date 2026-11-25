namespace SwitchAchievements.Core;

/// <summary>
/// Resolves integration-supplied identity evidence against the supported-game
/// catalog. Pure: no I/O, no clock, no normalisation. Fact values are compared
/// with exact ordinal comparison; Core never reinterprets integration formats.
///
/// Rules are applied in order; the first that applies decides:
/// <list type="number">
/// <item>Invalid evidence (integration id, process fingerprint, fact key or value) → Unknown.</item>
/// <item>A provider-reported <see cref="EvidenceIssueKind.Conflict"/> → Conflict.</item>
/// <item>The integration has no catalog matchers → Unknown when the evidence has no facts, otherwise Unsupported.</item>
/// <item>An identity fact required by the integration's matchers is missing → Unknown.</item>
/// <item>The identity facts match no game → Unsupported.</item>
/// <item>Exactly one game matches: a missing build fact → Unknown; the build tuple is one of
/// that game's supported builds → Identified; it is a build of another game → Conflict;
/// otherwise → Unsupported.</item>
/// <item>More than one game matches → Conflict (unreachable for a validated catalog; kept defensively).</item>
/// </list>
/// Facts the matchers do not use are ignored. Non-conflict provider issues do not
/// by themselves prevent identification, but are always reported in the diagnostics.
/// </summary>
public static class GameIdentityResolver
{
    public static GameIdentification Resolve(GameCatalog catalog, GameIdentityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(evidence);

        var diagnostics = new List<string>();
        foreach (var issue in evidence.Issues)
        {
            diagnostics.Add(issue.FactKey is null
                ? $"provider issue ({issue.Kind}): {issue.Message}"
                : $"provider issue ({issue.Kind}, {issue.FactKey}): {issue.Message}");
        }

        // 1. Invalid evidence.
        if (!IntegrationIdentifier.IsValid(evidence.IntegrationId))
            return Unknown(evidence, diagnostics, "evidence has an invalid integration id");

        var integration = evidence.IntegrationId;
        if (evidence.Process.ProcessId <= 0 || evidence.Process.ProcessStartUtc == default)
            return Unknown(evidence, diagnostics, $"evidence from '{integration}' is not bound to a valid process fingerprint");

        foreach (var (key, value) in evidence.Facts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!IdentityFactKey.IsValid(key))
                return Unknown(evidence, diagnostics, $"evidence from '{integration}' contains an invalid fact key");
            if (!IdentityFactValue.IsValid(value))
                return Unknown(evidence, diagnostics, $"evidence from '{integration}' has an invalid value for fact '{key}'");
        }

        // 2. Provider-reported conflict.
        if (evidence.Issues.Any(i => i.Kind == EvidenceIssueKind.Conflict))
            return NotIdentified(IdentificationStatus.Conflict, evidence, diagnostics, $"provider '{integration}' reported conflicting evidence");

        // 3. No catalog matchers for the integration.
        var index = catalog.FindIntegration(integration);
        if (index is null)
        {
            return evidence.Facts.Count == 0
                ? Unknown(evidence, diagnostics, $"no identity facts from '{integration}', and the catalog has no games for it")
                : NotIdentified(IdentificationStatus.Unsupported, evidence, diagnostics, $"integration '{integration}' has no supported games in the catalog");
        }

        // 4. Missing identity facts.
        var missingIdentity = index.IdentityKeys.Where(k => !evidence.Facts.ContainsKey(k)).ToList();
        if (missingIdentity.Count > 0)
            return Unknown(evidence, diagnostics, $"missing identity fact(s) from '{integration}': {string.Join(", ", missingIdentity)}");

        var identityPairs = CatalogCopy.Pairs(evidence.Facts, index.IdentityKeys);
        var matches = index.Entries
            .Where(e => index.IdentityKeys.All(k => string.Equals(e.Matcher.IdentityFacts[k], evidence.Facts[k], StringComparison.Ordinal)))
            .ToList();

        // 5. No matching game.
        if (matches.Count == 0)
            return NotIdentified(IdentificationStatus.Unsupported, evidence, diagnostics, $"unsupported program: no catalog game for '{integration}' has identity ({identityPairs})");

        // 7. Several matching games (defensive; a validated catalog prevents it).
        if (matches.Count > 1)
        {
            var ids = string.Join(", ", matches.Select(m => $"'{m.Game.Id.Value}'").OrderBy(s => s, StringComparer.Ordinal));
            return NotIdentified(IdentificationStatus.Conflict, evidence, diagnostics, $"identity ({identityPairs}) matches several catalog games: {ids}");
        }

        // 6. Exactly one matching game.
        var (game, matcher) = matches[0];
        var gameId = game.Id.Value;

        var missingBuild = index.BuildKeys.Where(k => !evidence.Facts.ContainsKey(k)).ToList();
        if (missingBuild.Count > 0)
            return Unknown(evidence, diagnostics, $"identity matches '{gameId}', but build fact(s) are missing: {string.Join(", ", missingBuild)}");

        var buildTuple = CatalogCopy.TupleKey(evidence.Facts, index.BuildKeys);
        var buildPairs = CatalogCopy.Pairs(evidence.Facts, index.BuildKeys);

        var supported = matcher.SupportedBuilds.FirstOrDefault(b =>
            string.Equals(CatalogCopy.TupleKey(b.Facts), buildTuple, StringComparison.Ordinal));
        if (supported is not null)
        {
            diagnostics.Add($"identified '{gameId}' via '{integration}' ({identityPairs}; build {buildPairs})");
            var identified = new IdentifiedGame(game.Id, game.DisplayName, game.Platform, integration, supported, evidence.Process);
            return GameIdentification.Identified(identified, evidence, diagnostics);
        }

        var owner = index.Entries.FirstOrDefault(e =>
            !ReferenceEquals(e.Game, game)
            && e.Matcher.SupportedBuilds.Any(b => string.Equals(CatalogCopy.TupleKey(b.Facts), buildTuple, StringComparison.Ordinal)));
        if (owner.Game is not null)
            return NotIdentified(IdentificationStatus.Conflict, evidence, diagnostics, $"identity matches '{gameId}', but build ({buildPairs}) belongs to '{owner.Game.Id.Value}'");

        return NotIdentified(IdentificationStatus.Unsupported, evidence, diagnostics, $"unsupported build of '{gameId}' via '{integration}': ({buildPairs})");
    }

    private static GameIdentification Unknown(GameIdentityEvidence evidence, List<string> diagnostics, string reason)
        => NotIdentified(IdentificationStatus.Unknown, evidence, diagnostics, reason);

    private static GameIdentification NotIdentified(IdentificationStatus status, GameIdentityEvidence evidence, List<string> diagnostics, string reason)
    {
        diagnostics.Add($"{status.ToString().ToLowerInvariant()}: {reason}");
        return GameIdentification.NotIdentified(status, evidence, diagnostics);
    }
}

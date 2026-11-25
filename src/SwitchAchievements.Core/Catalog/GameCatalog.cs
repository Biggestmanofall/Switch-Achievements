namespace SwitchAchievements.Core;

/// <summary>
/// The validated, in-memory supported-game catalog. Construction either yields a
/// fully valid catalog or throws <see cref="CatalogValidationException"/> listing
/// every problem. Core only holds and validates the model; loading it (e.g. from
/// embedded JSON) and validating integration-specific value formats belong to
/// the application layer.
///
/// Rules:
/// <list type="number">
/// <item>Game ids are lowercase kebab-case (<c>^[a-z0-9]+(-[a-z0-9]+)*$</c>, at most
/// 64 characters), are not <c>unknown</c>, and are unique.</item>
/// <item>Display name and platform are non-empty text.</item>
/// <item>Each game has at least one matcher; integration ids are valid
/// (<see cref="IntegrationIdentifier"/>); at most one matcher per game per integration.</item>
/// <item>Each matcher has at least one identity fact; all keys and values are valid
/// (<see cref="IdentityFactKey"/>, <see cref="IdentityFactValue"/>).</item>
/// <item>Each matcher lists at least one supported build; every build has exactly
/// the matcher's build key set (non-empty, disjoint from the identity keys) with
/// valid keys and values; no duplicate builds within a matcher.</item>
/// <item>All matchers of one integration use the same identity key set and the
/// same build key set.</item>
/// <item>No two games share identical identity facts for the same integration.</item>
/// <item>No build tuple is listed under two different games for the same integration.</item>
/// </list>
/// </summary>
public sealed class GameCatalog
{
    public const int MaxGameIdLength = 64;
    public const int MaxDisplayNameLength = 256;
    public const int MaxPlatformLength = 64;

    private readonly Dictionary<string, IntegrationIndex> _integrations;

    public GameCatalog(IEnumerable<CatalogGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        var list = games.ToList();
        var errors = new List<string>();
        _integrations = Validate(list, errors);
        if (errors.Count > 0)
            throw new CatalogValidationException(errors);
        Games = list.AsReadOnly();
    }

    public IReadOnlyList<CatalogGame> Games { get; }

    /// <summary>The validated matchers and key sets of one integration, or null when the catalog has none.</summary>
    internal IntegrationIndex? FindIntegration(string integrationId)
        => _integrations.TryGetValue(integrationId, out var index) ? index : null;

    internal sealed class IntegrationIndex
    {
        public IntegrationIndex(IReadOnlyList<string> identityKeys, IReadOnlyList<string> buildKeys)
        {
            IdentityKeys = identityKeys;
            BuildKeys = buildKeys;
        }

        /// <summary>Identity keys shared by every matcher of the integration (ordinal order).</summary>
        public IReadOnlyList<string> IdentityKeys { get; }

        /// <summary>Build keys shared by every matcher of the integration (ordinal order).</summary>
        public IReadOnlyList<string> BuildKeys { get; }

        public List<(CatalogGame Game, CatalogIdentityMatcher Matcher)> Entries { get; } = new();
    }

    private static Dictionary<string, IntegrationIndex> Validate(List<CatalogGame> games, List<string> errors)
    {
        var integrations = new Dictionary<string, IntegrationIndex>(StringComparer.Ordinal);
        var integrationOrder = new List<string>();
        var gameIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < games.Count; i++)
        {
            var game = games[i];
            if (game is null)
            {
                errors.Add($"game #{i}: is null");
                continue;
            }

            var id = game.Id?.Value;
            var gameIdValid = IsValidGameId(id);
            var label = gameIdValid ? $"game '{id}'" : $"game #{i}";

            if (!gameIdValid)
                errors.Add($"{label}: game id {Quote(id)} must be lowercase kebab-case (a-z, 0-9, single hyphens), at most {MaxGameIdLength} characters");
            else if (string.Equals(id, GameId.Unknown.Value, StringComparison.Ordinal))
                errors.Add($"{label}: game id '{id}' is reserved");
            else if (!gameIds.Add(id!))
                errors.Add($"{label}: duplicate game id '{id}'");

            if (!IsValidText(game.DisplayName, MaxDisplayNameLength))
                errors.Add($"{label}: display name must be non-empty text without control characters or surrounding white space, at most {MaxDisplayNameLength} characters");

            if (!IsValidText(game.Platform, MaxPlatformLength))
                errors.Add($"{label}: platform must be non-empty text without control characters or surrounding white space, at most {MaxPlatformLength} characters");

            if (game.Matchers.Count == 0)
            {
                errors.Add($"{label}: has no identity matchers");
                continue;
            }

            var gameIntegrations = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < game.Matchers.Count; j++)
            {
                var matcher = game.Matchers[j];
                if (matcher is null)
                {
                    errors.Add($"{label}: matcher #{j} is null");
                    continue;
                }

                if (!IntegrationIdentifier.IsValid(matcher.IntegrationId))
                {
                    errors.Add($"{label}: matcher #{j} has invalid integration id {Quote(matcher.IntegrationId)}");
                    continue;
                }

                var integration = matcher.IntegrationId;
                var mlabel = $"{label} matcher '{integration}'";
                if (!gameIntegrations.Add(integration))
                {
                    errors.Add($"{label}: more than one matcher for integration '{integration}'");
                    continue;
                }

                var errorCount = errors.Count;
                var buildKeys = ValidateMatcher(matcher, mlabel, errors);
                if (errors.Count != errorCount || buildKeys is null || !gameIdValid)
                    continue; // only fully valid matchers of validly identified games take part in cross-game checks

                if (!integrations.TryGetValue(integration, out var index))
                {
                    index = new IntegrationIndex(
                        matcher.IdentityFacts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                        buildKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
                    integrations.Add(integration, index);
                    integrationOrder.Add(integration);
                }

                index.Entries.Add((game, matcher));
            }
        }

        foreach (var integration in integrationOrder)
            ValidateAcrossGames(integration, integrations[integration], errors);

        return integrations;
    }

    /// <summary>Validates one matcher; returns its build key set, or null when it cannot be determined.</summary>
    private static IReadOnlyCollection<string>? ValidateMatcher(CatalogIdentityMatcher matcher, string mlabel, List<string> errors)
    {
        if (matcher.IdentityFacts.Count == 0)
            errors.Add($"{mlabel}: has no identity facts");
        ValidateFacts(matcher.IdentityFacts, $"{mlabel} identity", errors);

        if (matcher.SupportedBuilds.Count == 0)
        {
            errors.Add($"{mlabel}: lists no supported builds");
            return null;
        }

        HashSet<string>? reference = null;
        var tuples = new HashSet<string>(StringComparer.Ordinal);
        for (var k = 0; k < matcher.SupportedBuilds.Count; k++)
        {
            var build = matcher.SupportedBuilds[k];
            var blabel = $"{mlabel} build #{k}";
            if (build is null)
            {
                errors.Add($"{blabel}: is null");
                continue;
            }

            if (build.Facts.Count == 0)
            {
                errors.Add($"{blabel}: has no build facts");
                continue;
            }

            var before = errors.Count;
            ValidateFacts(build.Facts, blabel, errors);

            if (build.Label is not null && !IsValidText(build.Label, MaxDisplayNameLength))
                errors.Add($"{blabel}: label must be non-empty text without control characters or surrounding white space");

            foreach (var key in build.Facts.Keys.Where(matcher.IdentityFacts.ContainsKey).OrderBy(k => k, StringComparer.Ordinal))
                errors.Add($"{blabel}: fact '{key}' is used both as an identity fact and a build fact");

            if (reference is null)
            {
                reference = new HashSet<string>(build.Facts.Keys, StringComparer.Ordinal);
            }
            else if (!reference.SetEquals(build.Facts.Keys))
            {
                errors.Add($"{blabel}: uses build keys [{CatalogCopy.KeySet(build.Facts.Keys)}], expected [{CatalogCopy.KeySet(reference)}]");
            }

            if (errors.Count == before && !tuples.Add(CatalogCopy.TupleKey(build.Facts)))
                errors.Add($"{blabel}: duplicate build ({CatalogCopy.Pairs(build.Facts)})");
        }

        return reference;
    }

    private static void ValidateAcrossGames(string integration, IntegrationIndex index, List<string> errors)
    {
        var identityOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var buildOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (game, matcher) in index.Entries)
        {
            var gameId = game.Id.Value;

            if (!SameKeys(index.IdentityKeys, matcher.IdentityFacts.Keys))
            {
                errors.Add($"integration '{integration}': game '{gameId}' uses identity keys [{CatalogCopy.KeySet(matcher.IdentityFacts.Keys)}], expected [{CatalogCopy.KeySet(index.IdentityKeys)}]");
                continue;
            }

            var buildKeys = matcher.SupportedBuilds[0].Facts.Keys;
            if (!SameKeys(index.BuildKeys, buildKeys))
            {
                errors.Add($"integration '{integration}': game '{gameId}' uses build keys [{CatalogCopy.KeySet(buildKeys)}], expected [{CatalogCopy.KeySet(index.BuildKeys)}]");
                continue;
            }

            var identityTuple = CatalogCopy.TupleKey(matcher.IdentityFacts);
            if (identityOwners.TryGetValue(identityTuple, out var identityOwner) && identityOwner != gameId)
                errors.Add($"integration '{integration}': games '{identityOwner}' and '{gameId}' share identical identity facts ({CatalogCopy.Pairs(matcher.IdentityFacts)})");
            else
                identityOwners[identityTuple] = gameId;

            foreach (var build in matcher.SupportedBuilds)
            {
                var buildTuple = CatalogCopy.TupleKey(build.Facts);
                if (buildOwners.TryGetValue(buildTuple, out var buildOwner) && buildOwner != gameId)
                    errors.Add($"integration '{integration}': build ({CatalogCopy.Pairs(build.Facts)}) is listed under both '{buildOwner}' and '{gameId}'");
                else
                    buildOwners[buildTuple] = gameId;
            }
        }
    }

    private static void ValidateFacts(IReadOnlyDictionary<string, string> facts, string label, List<string> errors)
    {
        foreach (var (key, value) in facts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!IdentityFactKey.IsValid(key))
                errors.Add($"{label}: invalid fact key {Quote(key)}");
            else if (!IdentityFactValue.IsValid(value))
                errors.Add($"{label}: fact '{key}' has an invalid value {Quote(value)}");
        }
    }

    private static bool SameKeys(IReadOnlyList<string> expected, IEnumerable<string> actual)
        => new HashSet<string>(expected, StringComparer.Ordinal).SetEquals(actual);

    private static bool IsValidGameId(string? id)
        => IntegrationIdentifier.IsKebabToken(id, MaxGameIdLength, requireLetter: false);

    private static bool IsValidText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length > maxLength)
            return false;
        if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))
            return false;
        return !text.Any(char.IsControl);
    }

    private static string Quote(string? value)
        => value is null ? "(null)" : "'" + EvidenceIssue.Sanitize(value, 64) + "'";
}

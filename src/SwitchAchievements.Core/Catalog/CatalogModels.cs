using System.Collections.ObjectModel;

namespace SwitchAchievements.Core;

/// <summary>
/// One explicitly supported build of a game for one integration: a complete
/// tuple of build facts (e.g. <c>switch2.mainBuildId</c>) that evidence must
/// match exactly. Builds are tuples, not per-key value lists, so a combination of
/// values from different builds can never be accepted.
/// </summary>
public sealed class CatalogBuild
{
    public CatalogBuild(IReadOnlyDictionary<string, string> facts, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        Facts = CatalogCopy.Facts(facts);
        Label = label;
    }

    /// <summary>The build's facts (read-only copy, ordinal keys).</summary>
    public IReadOnlyDictionary<string, string> Facts { get; }

    /// <summary>Optional human-readable label (display only; never used for matching).</summary>
    public string? Label { get; }
}

/// <summary>
/// How one integration recognises a catalog game: exact identity facts (which
/// game) plus the explicitly supported builds (which builds of it are supported).
/// </summary>
public sealed class CatalogIdentityMatcher
{
    public CatalogIdentityMatcher(
        string integrationId,
        IReadOnlyDictionary<string, string> identityFacts,
        IReadOnlyList<CatalogBuild> supportedBuilds)
    {
        ArgumentNullException.ThrowIfNull(identityFacts);
        ArgumentNullException.ThrowIfNull(supportedBuilds);
        IntegrationId = integrationId;
        IdentityFacts = CatalogCopy.Facts(identityFacts);
        SupportedBuilds = Array.AsReadOnly(supportedBuilds.ToArray());
    }

    public string IntegrationId { get; }

    /// <summary>Facts identifying the game for this integration (read-only copy).</summary>
    public IReadOnlyDictionary<string, string> IdentityFacts { get; }

    /// <summary>Explicitly supported builds (read-only copy).</summary>
    public IReadOnlyList<CatalogBuild> SupportedBuilds { get; }
}

/// <summary>A supported game: stable id, display metadata, and its integration matchers.</summary>
public sealed class CatalogGame
{
    public CatalogGame(GameId id, string displayName, string platform, IReadOnlyList<CatalogIdentityMatcher> matchers)
    {
        ArgumentNullException.ThrowIfNull(matchers);
        Id = id;
        DisplayName = displayName;
        Platform = platform;
        Matchers = Array.AsReadOnly(matchers.ToArray());
    }

    /// <summary>Stable internal id, e.g. <c>zelda-ocarina-of-time</c> (separate from the display name).</summary>
    public GameId Id { get; }

    public string DisplayName { get; }

    public string Platform { get; }

    public IReadOnlyList<CatalogIdentityMatcher> Matchers { get; }
}

/// <summary>Thrown when a <see cref="GameCatalog"/> is constructed from invalid data; lists every error found.</summary>
public sealed class CatalogValidationException : Exception
{
    public CatalogValidationException(IReadOnlyList<string> errors)
        : base(BuildMessage(errors))
    {
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    /// <summary>Every validation error, in deterministic order.</summary>
    public IReadOnlyList<string> Errors { get; }

    private static string BuildMessage(IReadOnlyList<string> errors)
        => $"The game catalog is invalid ({errors.Count} error{(errors.Count == 1 ? "" : "s")}):"
           + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "- " + e));
}

internal static class CatalogCopy
{
    public static IReadOnlyDictionary<string, string> Facts(IReadOnlyDictionary<string, string> facts)
    {
        var copy = new Dictionary<string, string>(facts.Count, StringComparer.Ordinal);
        foreach (var (key, value) in facts)
            copy[key] = value;
        return new ReadOnlyDictionary<string, string>(copy);
    }

    /// <summary>
    /// Order-independent, unambiguous representation of a fact tuple (keys sorted
    /// ordinally; separators are control characters, which valid keys and values cannot contain).
    /// </summary>
    public static string TupleKey(IReadOnlyDictionary<string, string> facts, IEnumerable<string>? keys = null)
        => string.Join('\u0001', (keys ?? facts.Keys).OrderBy(k => k, StringComparer.Ordinal).Select(k => k + '\u0000' + facts[k]));

    public static string KeySet(IEnumerable<string> keys)
        => string.Join(", ", keys.OrderBy(k => k, StringComparer.Ordinal));

    public static string Pairs(IReadOnlyDictionary<string, string> facts, IEnumerable<string>? keys = null)
        => string.Join(", ", (keys ?? facts.Keys).OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}={facts[k]}"));
}

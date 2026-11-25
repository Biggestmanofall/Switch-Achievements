using SwitchAchievements.Core;

namespace SwitchAchievements.App.Catalog;

/// <summary>
/// One catalog loading problem, attributed to the resource (and, for JSON
/// problems, the JSON path and line) it came from. Messages describe the
/// developer-owned catalog content only; they contain no stack traces or
/// file-system paths.
/// </summary>
public sealed class CatalogLoadError
{
    public CatalogLoadError(string resource, string? jsonPath, long? line, string message)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        JsonPath = jsonPath;
        Line = line;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    /// <summary>The catalog resource, e.g. <c>catalog/games/zelda-ocarina-of-time/game.json</c>, or <c>catalog</c> for cross-game problems.</summary>
    public string Resource { get; }

    /// <summary>JSON path of the problem, e.g. <c>$.identityMatchers[0].identityFacts</c>, when known.</summary>
    public string? JsonPath { get; }

    /// <summary>1-based line number, when known.</summary>
    public long? Line { get; }

    public string Message { get; }

    public override string ToString()
    {
        var location = JsonPath is null ? Resource : $"{Resource} {JsonPath}";
        if (Line is not null)
            location += $" (line {Line})";
        return $"{location}: {Message}";
    }
}

/// <summary>
/// Outcome of loading the catalog. Content problems never throw: they produce a
/// failed result listing every error found, in deterministic order.
/// </summary>
public sealed class CatalogLoadResult
{
    private CatalogLoadResult(GameCatalog? catalog, IReadOnlyList<CatalogLoadError> errors)
    {
        Catalog = catalog;
        Errors = errors;
    }

    /// <summary>The validated catalog; non-null only when <see cref="Succeeded"/>.</summary>
    public GameCatalog? Catalog { get; }

    public IReadOnlyList<CatalogLoadError> Errors { get; }

    public bool Succeeded => Catalog is not null;

    internal static CatalogLoadResult Success(GameCatalog catalog)
        => new(catalog ?? throw new ArgumentNullException(nameof(catalog)), Array.Empty<CatalogLoadError>());

    internal static CatalogLoadResult Failure(IReadOnlyList<CatalogLoadError> errors)
    {
        if (errors is null || errors.Count == 0)
            throw new ArgumentException("A failed result needs at least one error.", nameof(errors));
        return new CatalogLoadResult(null, Array.AsReadOnly(errors.ToArray()));
    }
}

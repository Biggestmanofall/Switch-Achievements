using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchAchievements.Core;

namespace SwitchAchievements.App.Catalog;

/// <summary>
/// Loads the developer-owned, embedded supported-game catalog and turns it into a
/// validated Core <see cref="GameCatalog"/>.
///
/// <para>Process (deterministic and manifest-driven):</para>
/// <list type="number">
/// <item>Every resource name must be a forward-slash name under <c>catalog/</c> ending in <c>.json</c>.</item>
/// <item><c>catalog/catalog.json</c> (schema version 1) lists game ids in order; duplicates are rejected.</item>
/// <item>Each listed id loads exactly <c>catalog/games/&lt;id&gt;/game.json</c> (schema version 1),
/// whose <c>id</c> must equal the listed id.</item>
/// <item>Each identity matcher must name an integration with a registered
/// <see cref="IIntegrationIdentityFormat"/>; its identity and build key sets must equal the
/// format's exactly, and every value must be canonical for that format.</item>
/// <item>Each game is validated on its own by Core (errors attributed to its file), then the
/// combined <see cref="GameCatalog"/> applies the cross-game rules.</item>
/// <item>Any catalog resource the manifest does not reference is an error.</item>
/// </list>
///
/// <para>JSON is strict: case-sensitive explicit property names, no unknown or duplicate
/// properties (including duplicate keys inside fact objects), no missing required
/// properties, no comments or trailing commas, no nulls where a value is required.
/// Values are never trimmed, lowercased, or otherwise repaired.</para>
///
/// <para>Content problems produce a failed <see cref="CatalogLoadResult"/>; only programming
/// errors (null or inconsistent arguments) throw.</para>
/// </summary>
public static class CatalogLoader
{
    public const int SupportedSchemaVersion = 1;
    public const string ManifestResource = "catalog/catalog.json";
    private const string CatalogPrefix = "catalog/";
    private const string CrossGameResource = "catalog";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    /// <summary>The resource name of a listed game's file.</summary>
    public static string GameResource(string gameId) => $"catalog/games/{gameId}/game.json";

    /// <summary>Loads the catalog embedded in this application.</summary>
    public static CatalogLoadResult LoadEmbedded(IEnumerable<IIntegrationIdentityFormat> formats)
        => Load(new EmbeddedCatalogResourceSource(), formats);

    public static CatalogLoadResult Load(ICatalogResourceSource source, IEnumerable<IIntegrationIdentityFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(source);
        var registry = BuildRegistry(formats);
        var errors = new List<CatalogLoadError>();

        // 1. Resource names (reported exactly as embedded; never normalised).
        var names = new SortedSet<string>(source.Names ?? throw new ArgumentException("The resource source returned no name list.", nameof(source)), StringComparer.Ordinal);
        var loadable = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (IsValidResourceName(name))
                loadable.Add(name);
            else
                errors.Add(new CatalogLoadError(name, null, null,
                    "invalid catalog resource name: expected a forward-slash name under 'catalog/' ending in '.json'"));
        }

        // 2. Manifest.
        if (!loadable.Contains(ManifestResource))
        {
            errors.Add(new CatalogLoadError(ManifestResource, null, null, "the catalog manifest is missing"));
            return CatalogLoadResult.Failure(errors);
        }

        var manifest = Read<CatalogManifestJson>(source, ManifestResource, errors);
        if (manifest is null)
            return CatalogLoadResult.Failure(errors);

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add(new CatalogLoadError(ManifestResource, "$.schemaVersion", null,
                $"unsupported schema version {manifest.SchemaVersion}; expected {SupportedSchemaVersion}"));
            return CatalogLoadResult.Failure(errors);
        }

        // 3–5. Games, in manifest order.
        var referenced = new HashSet<string>(StringComparer.Ordinal) { ManifestResource };
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var games = new List<CatalogGame>();

        for (var i = 0; i < manifest.Games.Count; i++)
        {
            var id = manifest.Games[i];
            var path = $"$.games[{i}]";
            if (!IsSafeGameIdSegment(id))
            {
                errors.Add(new CatalogLoadError(ManifestResource, path, null,
                    $"{Describe(id)} is not a valid game id (lowercase letters, digits, and hyphens)"));
                continue;
            }

            if (!listed.Add(id))
            {
                errors.Add(new CatalogLoadError(ManifestResource, path, null, $"duplicate game id '{id}'"));
                continue;
            }

            var resource = GameResource(id);
            referenced.Add(resource);
            if (!loadable.Contains(resource))
            {
                errors.Add(new CatalogLoadError(resource, null, null, $"the game file for '{id}' is missing"));
                continue;
            }

            var game = LoadGame(source, resource, id, registry, errors);
            if (game is not null)
                games.Add(game);
        }

        // 6. Unreferenced resources.
        foreach (var name in loadable)
        {
            if (!referenced.Contains(name))
                errors.Add(new CatalogLoadError(name, null, null, "catalog resource is not referenced by the manifest"));
        }

        if (errors.Count > 0)
            return CatalogLoadResult.Failure(errors);

        // Cross-game rules (identical identities, shared builds, key-set consistency).
        try
        {
            return CatalogLoadResult.Success(new GameCatalog(games));
        }
        catch (CatalogValidationException ex)
        {
            errors.AddRange(ex.Errors.Select(e => new CatalogLoadError(CrossGameResource, null, null, e)));
            return CatalogLoadResult.Failure(errors);
        }
    }

    private static CatalogGame? LoadGame(
        ICatalogResourceSource source,
        string resource,
        string listedId,
        IReadOnlyDictionary<string, IIntegrationIdentityFormat> registry,
        List<CatalogLoadError> errors)
    {
        var json = Read<GameJson>(source, resource, errors);
        if (json is null)
            return null;

        var before = errors.Count;

        if (json.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add(new CatalogLoadError(resource, "$.schemaVersion", null,
                $"unsupported schema version {json.SchemaVersion}; expected {SupportedSchemaVersion}"));
            return null;
        }

        if (!string.Equals(json.Id, listedId, StringComparison.Ordinal))
            errors.Add(new CatalogLoadError(resource, "$.id", null, $"game id {Describe(json.Id)} does not match its manifest entry '{listedId}'"));

        for (var m = 0; m < json.IdentityMatchers.Count; m++)
            CheckMatcher(resource, m, json.IdentityMatchers[m], registry, errors);

        if (errors.Count != before)
            return null;

        var game = new CatalogGame(
            new GameId(json.Id),
            json.DisplayName,
            json.Platform,
            json.IdentityMatchers.Select(m => new CatalogIdentityMatcher(
                m.IntegrationId,
                m.IdentityFacts,
                m.SupportedBuilds.Select(b => new CatalogBuild(b.Facts, b.Label)).ToArray())).ToArray());

        // Core's rules for this game alone, attributed to its file.
        try
        {
            _ = new GameCatalog(new[] { game });
        }
        catch (CatalogValidationException ex)
        {
            errors.AddRange(ex.Errors.Select(e => new CatalogLoadError(resource, null, null, e)));
            return null;
        }

        return game;
    }

    private static void CheckMatcher(
        string resource,
        int index,
        IdentityMatcherJson? matcher,
        IReadOnlyDictionary<string, IIntegrationIdentityFormat> registry,
        List<CatalogLoadError> errors)
    {
        var path = $"$.identityMatchers[{index}]";
        if (matcher is null)
        {
            errors.Add(new CatalogLoadError(resource, path, null, "identity matcher must not be null"));
            return;
        }

        if (!IntegrationIdentifier.IsValid(matcher.IntegrationId))
        {
            errors.Add(new CatalogLoadError(resource, path + ".integrationId", null, $"invalid integration id {Describe(matcher.IntegrationId)}"));
            return;
        }

        if (!registry.TryGetValue(matcher.IntegrationId, out var format))
        {
            errors.Add(new CatalogLoadError(resource, path + ".integrationId", null,
                $"no identity format is registered for integration '{matcher.IntegrationId}'"));
            return;
        }

        CheckFacts(resource, path + ".identityFacts", "identity", matcher.IdentityFacts, format.IdentityKeys, format, errors);

        for (var b = 0; b < matcher.SupportedBuilds.Count; b++)
        {
            var build = matcher.SupportedBuilds[b];
            var buildPath = $"{path}.supportedBuilds[{b}]";
            if (build is null)
            {
                errors.Add(new CatalogLoadError(resource, buildPath, null, "supported build must not be null"));
                continue;
            }

            CheckFacts(resource, buildPath + ".facts", "build", build.Facts, format.BuildKeys, format, errors);
        }
    }

    private static void CheckFacts(
        string resource,
        string path,
        string kind,
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyCollection<string> expectedKeys,
        IIntegrationIdentityFormat format,
        List<CatalogLoadError> errors)
    {
        var expected = new HashSet<string>(expectedKeys, StringComparer.Ordinal);

        var missing = expected.Where(k => !facts.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            errors.Add(new CatalogLoadError(resource, path, null,
                $"missing {kind} key(s) for integration '{format.IntegrationId}': {string.Join(", ", missing)}"));

        var extra = facts.Keys.Where(k => !expected.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (extra.Count > 0)
            errors.Add(new CatalogLoadError(resource, path, null,
                $"unexpected {kind} key(s) for integration '{format.IntegrationId}': {string.Join(", ", extra.Select(Describe))}"));

        foreach (var key in expected.Where(facts.ContainsKey).OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = facts[key];
            if (value is null)
                errors.Add(new CatalogLoadError(resource, $"{path}['{key}']", null, $"value of '{key}' must not be null"));
            else if (!format.IsCanonical(key, value))
                errors.Add(new CatalogLoadError(resource, $"{path}['{key}']", null,
                    $"value {Describe(value)} for '{key}' is not canonical for integration '{format.IntegrationId}'"));
        }
    }

    private static T? Read<T>(ICatalogResourceSource source, string resource, List<CatalogLoadError> errors)
        where T : class
    {
        Stream? stream;
        try
        {
            stream = source.Open(resource);
        }
        catch (IOException)
        {
            errors.Add(new CatalogLoadError(resource, null, null, "the resource could not be read"));
            return null;
        }

        if (stream is null)
        {
            errors.Add(new CatalogLoadError(resource, null, null, "the resource is missing"));
            return null;
        }

        using (stream)
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(stream, SerializerOptions);
                if (value is null)
                    errors.Add(new CatalogLoadError(resource, "$", null, "the document must be a JSON object, not null"));
                return value;
            }
            catch (JsonException ex)
            {
                errors.Add(new CatalogLoadError(resource, ex.Path, ex.LineNumber + 1, CleanJsonMessage(ex.Message)));
                return null;
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = null,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static IReadOnlyDictionary<string, IIntegrationIdentityFormat> BuildRegistry(IEnumerable<IIntegrationIdentityFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var registry = new Dictionary<string, IIntegrationIdentityFormat>(StringComparer.Ordinal);
        foreach (var format in formats)
        {
            if (format is null)
                throw new ArgumentException("Identity formats must not contain null.", nameof(formats));
            if (!IntegrationIdentifier.IsValid(format.IntegrationId))
                throw new ArgumentException($"Identity format has an invalid integration id {Describe(format.IntegrationId)}.", nameof(formats));
            if (!registry.TryAdd(format.IntegrationId, format))
                throw new ArgumentException($"More than one identity format is registered for '{format.IntegrationId}'.", nameof(formats));
        }

        return registry;
    }

    private static bool IsValidResourceName(string name)
        => name.StartsWith(CatalogPrefix, StringComparison.Ordinal)
           && name.EndsWith(".json", StringComparison.Ordinal)
           && !name.Contains('\\')
           && !name.Contains("//", StringComparison.Ordinal);

    // Only used to build a resource path; Core validates the full game-id grammar.
    private static bool IsSafeGameIdSegment(string? id)
        => !string.IsNullOrEmpty(id)
           && id.Length <= GameCatalog.MaxGameIdLength
           && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>Quoted, control-character-free, length-capped rendering of catalog text for messages.</summary>
    private static string Describe(string? value)
    {
        if (value is null)
            return "(null)";
        var visible = new string(value.Select(c => char.IsControl(c) ? '?' : c).Take(80).ToArray());
        return "'" + visible + (value.Length > 80 ? "…" : string.Empty) + "'";
    }

    /// <summary>System.Text.Json appends "Path: … | LineNumber: … | BytePositionInLine: …"; those are reported separately.</summary>
    private static string CleanJsonMessage(string message)
    {
        var cut = message.IndexOf(" Path: ", StringComparison.Ordinal);
        var text = cut >= 0 ? message[..cut] : message;
        return text.Replace("SwitchAchievements.App.Catalog.", string.Empty, StringComparison.Ordinal).Trim();
    }
}

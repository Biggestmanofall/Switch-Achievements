using System.Text.Json.Serialization;

namespace SwitchAchievements.App.Catalog;

// Strict JSON shapes of the developer-owned catalog (schema version 1).
// Property names are explicit and case-sensitive; every property is required
// except a build's optional "label". Deserialization rejects unknown and
// duplicate properties (see CatalogLoader.SerializerOptions).

/// <summary><c>catalog/catalog.json</c>: the ordered list of supported game ids.</summary>
internal sealed class CatalogManifestJson
{
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("games")]
    public required List<string> Games { get; init; }
}

/// <summary><c>catalog/games/&lt;id&gt;/game.json</c>: one supported game.</summary>
internal sealed class GameJson
{
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("identityMatchers")]
    public required List<IdentityMatcherJson> IdentityMatchers { get; init; }
}

/// <summary>How one integration recognises the game.</summary>
internal sealed class IdentityMatcherJson
{
    [JsonPropertyName("integrationId")]
    public required string IntegrationId { get; init; }

    [JsonPropertyName("identityFacts")]
    public required Dictionary<string, string> IdentityFacts { get; init; }

    [JsonPropertyName("supportedBuilds")]
    public required List<SupportedBuildJson> SupportedBuilds { get; init; }
}

/// <summary>One explicitly supported build: a complete tuple of build facts.</summary>
internal sealed class SupportedBuildJson
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("facts")]
    public required Dictionary<string, string> Facts { get; init; }
}

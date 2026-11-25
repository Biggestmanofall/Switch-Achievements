using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchAchievements.App.Catalog;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests.Catalog;

/// <summary>M5.6.3: the strict, manifest-driven catalog loader (in-memory resources).</summary>
public class CatalogLoaderTests
{
    private const string OotId = "zelda-ocarina-of-time";
    private const string Program = "0400486024dec000";
    private const string Build = "e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348";
    private const string ProgramKey = LongshotIdentityFormat.ProgramIdKey;
    private const string BuildKey = LongshotIdentityFormat.MainBuildIdKey;
    private const string Manifest = CatalogLoader.ManifestResource;
    private static readonly string OotResource = CatalogLoader.GameResource(OotId);
    private static readonly ProcessFingerprint Process = new(34236, new DateTime(2026, 11, 24, 5, 51, 31, 839, DateTimeKind.Utc));

    // ---------------- builders ----------------

    private static JsonObject ManifestNode(params string?[] ids)
        => new() { ["schemaVersion"] = 1, ["games"] = new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) };

    private static string ManifestJson(params string?[] ids) => ManifestNode(ids).ToJsonString();

    private static JsonObject GameNode(string id = OotId, string program = Program, string build = Build, string displayName = "The Legend of Zelda: Ocarina of Time")
        => new()
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["displayName"] = displayName,
            ["platform"] = "switch2",
            ["identityMatchers"] = new JsonArray(new JsonObject
            {
                ["integrationId"] = "longshot",
                ["identityFacts"] = new JsonObject { [ProgramKey] = program },
                ["supportedBuilds"] = new JsonArray(new JsonObject { ["facts"] = new JsonObject { [BuildKey] = build } }),
            }),
        };

    private static string GameJson(Action<JsonObject>? edit = null)
    {
        var game = GameNode();
        edit?.Invoke(game);
        return game.ToJsonString();
    }

    private static JsonObject Matcher(JsonObject game) => game["identityMatchers"]![0]!.AsObject();
    private static JsonObject FirstBuild(JsonObject game) => Matcher(game)["supportedBuilds"]![0]!.AsObject();

    private static InMemoryCatalogResourceSource Source(params (string Name, string? Content)[] resources) => new(resources);

    private static InMemoryCatalogResourceSource ValidSource() => Source((Manifest, ManifestJson(OotId)), (OotResource, GameJson()));

    private static CatalogLoadResult Load(ICatalogResourceSource source)
        => CatalogLoader.Load(source, new IIntegrationIdentityFormat[] { new LongshotIdentityFormat() });

    private static CatalogLoadResult LoadGame(string gameJson)
        => Load(Source((Manifest, ManifestJson(OotId)), (OotResource, gameJson)));

    private static CatalogLoadResult LoadManifest(string manifestJson)
        => Load(Source((Manifest, manifestJson), (OotResource, GameJson())));

    private static void AssertFailed(CatalogLoadResult result, string resource, string fragment)
    {
        Assert.False(result.Succeeded);
        Assert.Null(result.Catalog);
        Assert.True(
            result.Errors.Any(e => e.Resource == resource && e.ToString().Contains(fragment, StringComparison.Ordinal)),
            $"expected an error for {resource} containing \"{fragment}\"; got:\n{string.Join("\n", result.Errors)}");
    }

    private static GameIdentityEvidence OotEvidence(string build = Build)
        => new("longshot", Process,
            new Dictionary<string, string> { [ProgramKey] = Program, [BuildKey] = build }, null, DateTime.UtcNow);

    // ---------------- success ----------------

    [Fact]
    public void Valid_manifest_and_game_file_load_into_a_GameCatalog()
    {
        var result = Load(ValidSource());

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Empty(result.Errors);
        var game = Assert.Single(result.Catalog!.Games);
        Assert.Equal(OotId, game.Id.Value);
        Assert.Equal("The Legend of Zelda: Ocarina of Time", game.DisplayName);
        Assert.Equal("switch2", game.Platform);
        var matcher = Assert.Single(game.Matchers);
        Assert.Equal("longshot", matcher.IntegrationId);
        Assert.Equal(Program, Assert.Single(matcher.IdentityFacts).Value);
        Assert.Equal(ProgramKey, matcher.IdentityFacts.Keys.Single());
        var build = Assert.Single(matcher.SupportedBuilds);
        Assert.Equal(Build, build.Facts[BuildKey]);
        Assert.Single(build.Facts);
        Assert.Null(build.Label);
    }

    [Fact]
    public void Loaded_catalog_lets_the_resolver_identify_ocarina_of_time()
    {
        var catalog = Load(ValidSource()).Catalog!;

        var identified = GameIdentityResolver.Resolve(catalog, OotEvidence());
        Assert.Equal(IdentificationStatus.Identified, identified.Status);
        Assert.Equal(OotId, identified.Game!.GameId.Value);
        Assert.Equal(Process, identified.Game.Process);

        var unsupported = GameIdentityResolver.Resolve(catalog, OotEvidence(build: "ffffffffffffffffffffffffffffffffffffffff"));
        Assert.Equal(IdentificationStatus.Unsupported, unsupported.Status);
        Assert.Null(unsupported.Game);
    }

    [Fact]
    public void Optional_build_label_is_loaded_when_present_and_may_be_null()
    {
        var labelled = LoadGame(GameJson(g => FirstBuild(g)["label"] = "v131072"));
        Assert.Equal("v131072", labelled.Catalog!.Games[0].Matchers[0].SupportedBuilds[0].Label);

        var nullLabel = LoadGame(GameJson(g => FirstBuild(g)["label"] = null));
        Assert.True(nullLabel.Succeeded, string.Join("\n", nullLabel.Errors));
    }

    [Fact]
    public void Games_load_in_manifest_order()
    {
        const string second = "synthetic-game";
        var result = Load(Source(
            (Manifest, ManifestJson(second, OotId)),
            (OotResource, GameJson()),
            (CatalogLoader.GameResource(second), GameNode(second, "0400000000000001", "1111111111111111111111111111111111111111", "Synthetic").ToJsonString())));

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(new[] { second, OotId }, result.Catalog!.Games.Select(g => g.Id.Value));
    }

    // ---------------- resource errors ----------------

    [Fact]
    public void Missing_manifest_fails()
        => AssertFailed(Load(Source((OotResource, GameJson()))), Manifest, "the catalog manifest is missing");

    [Fact]
    public void Missing_game_file_fails()
        => AssertFailed(Load(Source((Manifest, ManifestJson(OotId)))), OotResource, $"the game file for '{OotId}' is missing");

    [Fact]
    public void Listed_resource_that_cannot_be_opened_fails()
        => AssertFailed(Load(Source((Manifest, ManifestJson(OotId)), (OotResource, null))), OotResource, "the resource is missing");

    [Theory]
    [InlineData("catalog/games/other-game/game.json")]
    [InlineData("catalog/games/zelda-ocarina-of-time/achievements.json")] // deferred; must not be silently embedded
    [InlineData("catalog/extra.json")]
    public void Unreferenced_resource_fails(string extra)
    {
        var source = Source((Manifest, ManifestJson(OotId)), (OotResource, GameJson()), (extra, "{}"));
        AssertFailed(Load(source), extra, "catalog resource is not referenced by the manifest");
    }

    [Fact]
    public void Duplicate_manifest_entry_fails()
        => AssertFailed(LoadManifest(ManifestJson(OotId, OotId)), Manifest, $"$.games[1]: duplicate game id '{OotId}'");

    [Theory]
    [InlineData("catalog\\catalog.json")]                                   // backslashes (incorrect embedding)
    [InlineData("catalog\\games\\zelda-ocarina-of-time\\game.json")]
    [InlineData("catalog/games/zelda-ocarina-of-time/game.txt")]
    [InlineData("catalog//catalog.json")]
    [InlineData("Catalog/catalog.json")]
    [InlineData("SwitchAchievements.App.catalog.catalog.json")]             // MSBuild's default naming
    public void Incorrect_resource_naming_fails(string badName)
    {
        var source = Source((Manifest, ManifestJson(OotId)), (OotResource, GameJson()), (badName, "{}"));
        AssertFailed(Load(source), badName, "invalid catalog resource name");
    }

    [Fact]
    public void Backslash_named_resources_are_reported_not_normalised()
    {
        // Only backslash names: nothing is silently treated as the forward-slash manifest.
        var source = Source(("catalog\\catalog.json", ManifestJson(OotId)), ("catalog\\games\\zelda-ocarina-of-time\\game.json", GameJson()));
        var result = Load(source);
        AssertFailed(result, "catalog\\catalog.json", "invalid catalog resource name");
        AssertFailed(result, Manifest, "the catalog manifest is missing");
    }

    [Theory]
    [InlineData("Zelda")]
    [InlineData("../zelda")]
    [InlineData("zelda/oot")]
    [InlineData("zelda oot")]
    [InlineData("")]
    public void Invalid_manifest_game_ids_fail(string id)
        => AssertFailed(LoadManifest(ManifestJson(id)), Manifest, "is not a valid game id");

    [Fact]
    public void Null_manifest_game_entry_fails()
        => AssertFailed(LoadManifest(ManifestJson((string?)null)), Manifest, "$.games[0]: (null) is not a valid game id");

    // ---------------- JSON errors ----------------

    [Fact]
    public void Malformed_json_fails_with_a_location()
    {
        var result = LoadGame("{ \"schemaVersion\": 1, \"id\": ");
        AssertFailed(result, OotResource, "");
        Assert.NotNull(result.Errors.Single(e => e.Resource == OotResource).Line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"schemaVersion\": 1, \"games\": [\"zelda-ocarina-of-time\"], }")] // trailing comma
    [InlineData("/* comment */ { \"schemaVersion\": 1, \"games\": [\"zelda-ocarina-of-time\"] }")]
    public void Invalid_manifest_documents_fail(string manifest)
    {
        var result = LoadManifest(manifest);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Resource == Manifest);
    }

    [Fact]
    public void Duplicate_top_level_properties_fail()
    {
        AssertFailed(LoadManifest("{\"schemaVersion\":1,\"schemaVersion\":1,\"games\":[\"zelda-ocarina-of-time\"]}"), Manifest, "schemaVersion");

        var game = GameJson();
        var duplicatedId = "{\"id\":\"zelda-ocarina-of-time\"," + game[1..];
        AssertFailed(LoadGame(duplicatedId), OotResource, "id");
    }

    [Theory]
    [InlineData(Program)]                 // identical duplicate value
    [InlineData("0400000000000001")]      // different duplicate value (would be "last wins" by default)
    public void Duplicate_identity_fact_keys_fail(string secondValue)
    {
        var original = $"\"identityFacts\":{{\"{ProgramKey}\":\"{Program}\"}}";
        var duplicated = $"\"identityFacts\":{{\"{ProgramKey}\":\"{Program}\",\"{ProgramKey}\":\"{secondValue}\"}}";
        var json = GameJson();
        Assert.Contains(original, json);

        var result = LoadGame(json.Replace(original, duplicated));
        AssertFailed(result, OotResource, ProgramKey);
        Assert.Contains("identityFacts", result.Errors.Single(e => e.Resource == OotResource).JsonPath);
    }

    [Theory]
    [InlineData(Build)]
    [InlineData("1111111111111111111111111111111111111111")]
    public void Duplicate_build_fact_keys_fail(string secondValue)
    {
        var original = $"\"facts\":{{\"{BuildKey}\":\"{Build}\"}}";
        var duplicated = $"\"facts\":{{\"{BuildKey}\":\"{Build}\",\"{BuildKey}\":\"{secondValue}\"}}";
        var json = GameJson();
        Assert.Contains(original, json);

        var result = LoadGame(json.Replace(original, duplicated));
        AssertFailed(result, OotResource, BuildKey);
        Assert.Contains("facts", result.Errors.Single(e => e.Resource == OotResource).JsonPath);
    }

    [Fact]
    public void Platform_default_would_accept_duplicate_dictionary_keys_so_the_loader_must_not()
    {
        // Documents why AllowDuplicateProperties = false is required: by default the
        // last duplicate silently wins, even inside dictionary-like objects.
        const string json = "{\"a\":\"1\",\"a\":\"2\"}";
        Assert.Equal("2", JsonSerializer.Deserialize<Dictionary<string, string>>(json)!["a"]);
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { AllowDuplicateProperties = false }));
    }

    public static TheoryData<string, string> UnknownProperties() => new()
    {
        { "manifest", "extra" },
        { "game", "extra" },
        { "game", "DisplayName" },   // property names are case-sensitive
        { "matcher", "extra" },
        { "build", "extra" },
    };

    [Theory]
    [MemberData(nameof(UnknownProperties))]
    public void Unknown_properties_fail(string level, string property)
    {
        if (level == "manifest")
        {
            var manifest = ManifestNode(OotId);
            manifest[property] = 1;
            AssertFailed(LoadManifest(manifest.ToJsonString()), Manifest, property);
            return;
        }

        var json = GameJson(g =>
        {
            var target = level switch { "game" => g, "matcher" => Matcher(g), _ => FirstBuild(g) };
            target[property] = "x";
        });
        AssertFailed(LoadGame(json), OotResource, property);
    }

    public static TheoryData<string, string> RequiredProperties() => new()
    {
        { "manifest", "schemaVersion" }, { "manifest", "games" },
        { "game", "schemaVersion" }, { "game", "id" }, { "game", "displayName" }, { "game", "platform" }, { "game", "identityMatchers" },
        { "matcher", "integrationId" }, { "matcher", "identityFacts" }, { "matcher", "supportedBuilds" },
        { "build", "facts" },
    };

    [Theory]
    [MemberData(nameof(RequiredProperties))]
    public void Missing_required_properties_fail_at_every_level(string level, string property)
    {
        if (level == "manifest")
        {
            var manifest = ManifestNode(OotId);
            manifest.Remove(property);
            AssertFailed(LoadManifest(manifest.ToJsonString()), Manifest, $"'{property}'");
            return;
        }

        var json = GameJson(g =>
        {
            var target = level switch { "game" => g, "matcher" => Matcher(g), _ => FirstBuild(g) };
            Assert.True(target.Remove(property));
        });
        AssertFailed(LoadGame(json), OotResource, $"'{property}'");
    }

    public static TheoryData<string, string> NullValues() => new()
    {
        { "manifest", "games" },
        { "game", "id" }, { "game", "displayName" }, { "game", "platform" }, { "game", "identityMatchers" },
        { "matcher", "integrationId" }, { "matcher", "identityFacts" }, { "matcher", "supportedBuilds" },
        { "build", "facts" },
    };

    [Theory]
    [MemberData(nameof(NullValues))]
    public void Null_values_fail_where_prohibited(string level, string property)
    {
        if (level == "manifest")
        {
            var manifest = ManifestNode(OotId);
            manifest[property] = null;
            AssertFailed(LoadManifest(manifest.ToJsonString()), Manifest, property);
            return;
        }

        var json = GameJson(g =>
        {
            var target = level switch { "game" => g, "matcher" => Matcher(g), _ => FirstBuild(g) };
            target[property] = null;
        });
        AssertFailed(LoadGame(json), OotResource, property);
    }

    [Fact]
    public void Null_fact_values_fail()
    {
        AssertFailed(LoadGame(GameJson(g => Matcher(g)["identityFacts"]![ProgramKey] = null)), OotResource, $"value of '{ProgramKey}' must not be null");
        AssertFailed(LoadGame(GameJson(g => FirstBuild(g)["facts"]![BuildKey] = null)), OotResource, $"value of '{BuildKey}' must not be null");
    }

    public static TheoryData<string, string, string> WrongTypes() => new()
    {
        { "manifest", "schemaVersion", "\"1\"" },
        { "manifest", "schemaVersion", "1.5" },
        { "manifest", "games", "{}" },
        { "manifest", "games", "[1]" },
        { "game", "id", "5" },
        { "game", "displayName", "true" },
        { "game", "identityMatchers", "{}" },
        { "matcher", "integrationId", "[]" },
        { "matcher", "identityFacts", "[]" },
        { "matcher", "supportedBuilds", "{}" },
        { "build", "facts", "\"e0f3\"" },
    };

    [Theory]
    [MemberData(nameof(WrongTypes))]
    public void Incorrect_json_value_types_fail(string level, string property, string rawValue)
    {
        var value = JsonNode.Parse(rawValue);
        if (level == "manifest")
        {
            var manifest = ManifestNode(OotId);
            manifest[property] = value;
            AssertFailed(LoadManifest(manifest.ToJsonString()), Manifest, "");
            return;
        }

        var json = GameJson(g =>
        {
            var target = level switch { "game" => g, "matcher" => Matcher(g), _ => FirstBuild(g) };
            target[property] = value;
        });
        AssertFailed(LoadGame(json), OotResource, "");
    }

    [Fact]
    public void Non_string_fact_values_fail()
    {
        Assert.False(LoadGame(GameJson(g => Matcher(g)["identityFacts"]![ProgramKey] = 400486024)).Succeeded);
        Assert.False(LoadGame(GameJson(g => FirstBuild(g)["facts"]![BuildKey] = true)).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Invalid_manifest_schema_versions_fail(int version)
    {
        var manifest = ManifestNode(OotId);
        manifest["schemaVersion"] = version;
        AssertFailed(LoadManifest(manifest.ToJsonString()), Manifest, $"unsupported schema version {version}; expected 1");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Invalid_game_schema_versions_fail(int version)
        => AssertFailed(LoadGame(GameJson(g => g["schemaVersion"] = version)), OotResource, $"unsupported schema version {version}; expected 1");

    // ---------------- integration format errors ----------------

    [Theory]
    [InlineData("Longshot")]
    [InlineData("other--recomp")]
    [InlineData("-longshot")]
    [InlineData("")]
    public void Invalid_integration_identifiers_fail(string integration)
        => AssertFailed(LoadGame(GameJson(g => Matcher(g)["integrationId"] = integration)), OotResource, $"invalid integration id '{integration}'");

    [Fact]
    public void Integration_without_a_registered_format_fails()
    {
        AssertFailed(LoadGame(GameJson(g => Matcher(g)["integrationId"] = "other-recomp")),
            OotResource, "no identity format is registered for integration 'other-recomp'");

        var noFormats = CatalogLoader.Load(ValidSource(), Array.Empty<IIntegrationIdentityFormat>());
        AssertFailed(noFormats, OotResource, "no identity format is registered for integration 'longshot'");
    }

    [Fact]
    public void Incorrect_identity_key_sets_fail()
    {
        AssertFailed(LoadGame(GameJson(g => Matcher(g)["identityFacts"] = new JsonObject { ["switch2.titleId"] = Program })),
            OotResource, $"missing identity key(s) for integration 'longshot': {ProgramKey}");
        AssertFailed(LoadGame(GameJson(g => Matcher(g)["identityFacts"] = new JsonObject { ["switch2.titleId"] = Program })),
            OotResource, "unexpected identity key(s) for integration 'longshot': 'switch2.titleId'");
        AssertFailed(LoadGame(GameJson(g => Matcher(g)["identityFacts"]!["switch2.extra"] = "x")),
            OotResource, "unexpected identity key(s) for integration 'longshot': 'switch2.extra'");
        AssertFailed(LoadGame(GameJson(g => Matcher(g)["identityFacts"] = new JsonObject { ["switch2.programid"] = Program })),
            OotResource, $"missing identity key(s) for integration 'longshot': {ProgramKey}"); // key case matters
    }

    [Fact]
    public void Incorrect_build_key_sets_fail()
    {
        AssertFailed(LoadGame(GameJson(g => FirstBuild(g)["facts"] = new JsonObject { ["switch2.otherBuildId"] = Build })),
            OotResource, $"missing build key(s) for integration 'longshot': {BuildKey}");
        AssertFailed(LoadGame(GameJson(g => FirstBuild(g)["facts"]!["switch2.extra"] = "x")),
            OotResource, "unexpected build key(s) for integration 'longshot': 'switch2.extra'");
        AssertFailed(LoadGame(GameJson(g => FirstBuild(g)["facts"] = new JsonObject())),
            OotResource, $"missing build key(s) for integration 'longshot': {BuildKey}");
    }

    [Theory]
    [InlineData("0400486024DEC000")]
    [InlineData("0x0400486024dec000")]
    [InlineData("0400486024dec00")]
    [InlineData("0400486024dec0000")]
    [InlineData("0400486024dec00g")]
    [InlineData(" 0400486024dec000")]
    [InlineData("0400486024dec000 ")]
    public void Malformed_program_ids_are_rejected_not_repaired(string program)
    {
        var result = LoadGame(GameJson(g => Matcher(g)["identityFacts"]![ProgramKey] = program));
        // The error quotes the exact original value: nothing was lowercased, trimmed, or stripped.
        AssertFailed(result, OotResource, $"value '{program}' for '{ProgramKey}' is not canonical for integration 'longshot'");
        Assert.Null(result.Catalog);
    }

    [Theory]
    [InlineData("E0F3CCFAF50065AE4EA01EB5AEB13B1A45DA3348")]
    [InlineData("0xe0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348")]
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da334")]
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da33480")]
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348000000000000000000000000")]
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da334z")]
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348 ")]
    public void Malformed_build_ids_are_rejected_not_repaired(string build)
    {
        var result = LoadGame(GameJson(g => FirstBuild(g)["facts"]![BuildKey] = build));
        AssertFailed(result, OotResource, $"value '{build}' for '{BuildKey}' is not canonical for integration 'longshot'");
        Assert.Null(result.Catalog);
    }

    // ---------------- catalog structure errors ----------------

    [Fact]
    public void Missing_matchers_fail()
        => AssertFailed(LoadGame(GameJson(g => g["identityMatchers"] = new JsonArray())), OotResource, "has no identity matchers");

    [Fact]
    public void Missing_builds_fail()
        => AssertFailed(LoadGame(GameJson(g => Matcher(g)["supportedBuilds"] = new JsonArray())), OotResource, "lists no supported builds");

    [Fact]
    public void Empty_identity_facts_fail()
        => AssertFailed(LoadGame(GameJson(g => Matcher(g)["identityFacts"] = new JsonObject())),
            OotResource, $"missing identity key(s) for integration 'longshot': {ProgramKey}");

    [Fact]
    public void Game_id_must_match_its_manifest_entry()
        => AssertFailed(LoadGame(GameJson(g => g["id"] = "zelda-majoras-mask")),
            OotResource, $"$.id: game id 'zelda-majoras-mask' does not match its manifest entry '{OotId}'");

    [Fact]
    public void Core_game_rules_are_applied_and_attributed_to_the_game_file()
    {
        AssertFailed(LoadGame(GameJson(g => g["displayName"] = "")), OotResource, "display name must be non-empty");
        AssertFailed(LoadGame(GameJson(g => g["platform"] = " switch2")), OotResource, "platform must be non-empty");
    }

    [Fact]
    public void Duplicate_builds_inside_one_game_fail()
        => AssertFailed(LoadGame(GameJson(g => ((JsonArray)Matcher(g)["supportedBuilds"]!).Add(
                new JsonObject { ["facts"] = new JsonObject { [BuildKey] = Build } }))),
            OotResource, $"duplicate build ({BuildKey}={Build})");

    [Fact]
    public void Duplicate_identities_across_games_fail()
    {
        const string second = "synthetic-game";
        var result = Load(Source(
            (Manifest, ManifestJson(OotId, second)),
            (OotResource, GameJson()),
            (CatalogLoader.GameResource(second), GameNode(second, Program, "1111111111111111111111111111111111111111", "Synthetic").ToJsonString())));

        AssertFailed(result, "catalog", $"games '{OotId}' and '{second}' share identical identity facts");
    }

    [Fact]
    public void Duplicate_builds_across_games_fail()
    {
        const string second = "synthetic-game";
        var result = Load(Source(
            (Manifest, ManifestJson(OotId, second)),
            (OotResource, GameJson()),
            (CatalogLoader.GameResource(second), GameNode(second, "0400000000000001", Build, "Synthetic").ToJsonString())));

        AssertFailed(result, "catalog", $"build ({BuildKey}={Build}) is listed under both '{OotId}' and '{second}'");
    }

    // ---------------- determinism ----------------

    [Fact]
    public void Successful_loads_are_identical_regardless_of_enumeration_order()
    {
        const string second = "synthetic-game";
        var source = Source(
            (Manifest, ManifestJson(OotId, second)),
            (OotResource, GameJson()),
            (CatalogLoader.GameResource(second), GameNode(second, "0400000000000001", "1111111111111111111111111111111111111111", "Synthetic").ToJsonString()));

        var first = Load(source);
        var second2 = Load(source.Reversed());

        Assert.True(first.Succeeded && second2.Succeeded);
        Assert.Equal(Describe(first.Catalog!), Describe(second2.Catalog!));
    }

    [Fact]
    public void Error_lists_are_identical_regardless_of_enumeration_order()
    {
        var source = Source(
            (Manifest, ManifestJson(OotId, "missing-game", OotId)),
            (OotResource, GameJson(g => Matcher(g)["identityFacts"]![ProgramKey] = "0400486024DEC000")),
            ("catalog/extra.json", "{}"),
            ("catalog/games/zelda-ocarina-of-time/achievements.json", "{}"),
            ("catalog\\bad.json", "{}"));

        var first = Load(source).Errors.Select(e => e.ToString()).ToArray();
        var reversed = Load(source.Reversed()).Errors.Select(e => e.ToString()).ToArray();

        Assert.True(first.Length >= 6, string.Join("\n", first));
        Assert.Equal(first, reversed);
    }

    private static string Describe(GameCatalog catalog)
        => string.Join(" | ", catalog.Games.Select(g =>
            $"{g.Id.Value};{g.DisplayName};{g.Platform};" + string.Join(",", g.Matchers.Select(m =>
                $"{m.IntegrationId}:{string.Join("&", m.IdentityFacts.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + "=" + f.Value))}:"
                + string.Join("/", m.SupportedBuilds.Select(b => string.Join("&", b.Facts.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + "=" + f.Value))))))));

    // ---------------- programming errors ----------------

    [Fact]
    public void Null_and_inconsistent_arguments_throw()
    {
        var formats = new IIntegrationIdentityFormat[] { new LongshotIdentityFormat() };
        Assert.Throws<ArgumentNullException>(() => CatalogLoader.Load(null!, formats));
        Assert.Throws<ArgumentNullException>(() => CatalogLoader.Load(ValidSource(), null!));
        Assert.Throws<ArgumentException>(() => CatalogLoader.Load(ValidSource(), new IIntegrationIdentityFormat[] { null! }));
        Assert.Throws<ArgumentException>(() => CatalogLoader.Load(ValidSource(),
            new IIntegrationIdentityFormat[] { new LongshotIdentityFormat(), new LongshotIdentityFormat() }));
    }
}

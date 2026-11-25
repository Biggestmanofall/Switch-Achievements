using SwitchAchievements.Core;
using static SwitchAchievements.Tests.Identity.IdentityTestData;

namespace SwitchAchievements.Tests.Identity;

/// <summary>M5.6.2: in-memory catalog model and every validation rule.</summary>
public class GameCatalogValidationTests
{
    private static CatalogValidationException Invalid(params CatalogGame[] games)
        => Assert.Throws<CatalogValidationException>(() => new GameCatalog(games));

    private static void AssertError(CatalogValidationException ex, string expectedFragment)
        => Assert.True(ex.Errors.Any(e => e.Contains(expectedFragment, StringComparison.Ordinal)),
            $"expected an error containing \"{expectedFragment}\"; got:\n{string.Join("\n", ex.Errors)}");

    // ---------------- valid catalogs ----------------

    [Fact]
    public void Valid_ocarina_of_time_catalog_is_accepted()
    {
        var catalog = OotCatalog();

        var game = Assert.Single(catalog.Games);
        Assert.Equal(OotId, game.Id.Value);
        Assert.Equal(OotName, game.DisplayName);
        Assert.NotEqual(game.Id.Value, game.DisplayName); // stable id is separate from the display name
        var matcher = Assert.Single(game.Matchers);
        Assert.Equal(LongshotIntegration, matcher.IntegrationId);
        Assert.Equal(OotProgram, matcher.IdentityFacts[ProgramKey]);
        Assert.Equal(OotBuild, Assert.Single(matcher.SupportedBuilds).Facts[BuildKey]);
    }

    [Fact]
    public void Several_games_and_a_second_integration_with_different_keys_are_accepted()
    {
        var catalog = new GameCatalog(new[] { Oot(), OtherLongshotGame(), OtherRecompGame() });
        Assert.Equal(3, catalog.Games.Count);
    }

    [Fact]
    public void A_game_may_list_several_supported_builds_and_several_integrations()
    {
        var game = Game(OotId, OotName,
            LongshotMatcher(OotProgram, OotBuild, "2222222222222222222222222222222222222222"),
            new CatalogIdentityMatcher(OtherIntegration, Facts((OtherIdentityKey, "h1")), new[] { new CatalogBuild(Facts((OtherBuildKey, "r1"))) }));
        Assert.Single(new GameCatalog(new[] { game }).Games);
    }

    [Fact]
    public void Empty_catalog_is_valid()
        => Assert.Empty(new GameCatalog(Array.Empty<CatalogGame>()).Games);

    [Fact]
    public void Catalog_models_copy_their_inputs()
    {
        var facts = Facts((ProgramKey, OotProgram));
        var builds = new List<CatalogBuild> { LongshotBuild(OotBuild) };
        var matcher = new CatalogIdentityMatcher(LongshotIntegration, facts, builds);
        facts[ProgramKey] = "changed";
        builds.Add(LongshotBuild("3333333333333333333333333333333333333333"));

        Assert.Equal(OotProgram, matcher.IdentityFacts[ProgramKey]);
        Assert.Single(matcher.SupportedBuilds);
    }

    [Fact]
    public void Null_game_sequence_throws()
        => Assert.Throws<ArgumentNullException>(() => new GameCatalog(null!));

    // ---------------- rule 1: game ids ----------------

    [Fact]
    public void Duplicate_game_ids_are_rejected()
        => AssertError(Invalid(Oot(), Game(OotId, "Copy", LongshotMatcher(OtherProgram, OtherBuild))),
            $"duplicate game id '{OotId}'");

    [Fact]
    public void Reserved_unknown_game_id_is_rejected()
        => AssertError(Invalid(Game("unknown", "X", LongshotMatcher(OotProgram, OotBuild))), "game id 'unknown' is reserved");

    [Theory]
    [InlineData("Zelda")]
    [InlineData("zelda_oot")]
    [InlineData("-zelda")]
    [InlineData("zelda-")]
    [InlineData("zelda--oot")]
    [InlineData("zelda oot")]
    [InlineData("")]
    public void Invalid_game_ids_are_rejected(string id)
        => AssertError(Invalid(Game(id, "X", LongshotMatcher(OotProgram, OotBuild))), "must be lowercase kebab-case");

    [Fact]
    public void Overlong_and_null_game_ids_are_rejected()
    {
        AssertError(Invalid(Game(new string('z', 65), "X", LongshotMatcher(OotProgram, OotBuild))), "must be lowercase kebab-case");
        AssertError(Invalid(new CatalogGame(null!, "X", "switch2", new[] { LongshotMatcher(OotProgram, OotBuild) })), "game id (null)");
    }

    [Fact]
    public void Null_game_entry_is_reported()
        => AssertError(Invalid(Oot(), null!), "game #1: is null");

    // ---------------- rule 2: display name and platform ----------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" padded")]
    [InlineData("bad\nname")]
    public void Invalid_display_names_are_rejected(string name)
        => AssertError(Invalid(Game(OotId, name, LongshotMatcher(OotProgram, OotBuild))), $"game '{OotId}': display name must be non-empty");

    [Fact]
    public void Null_display_name_and_empty_platform_are_rejected()
    {
        var ex = Invalid(new CatalogGame(new GameId(OotId), null!, "", new[] { LongshotMatcher(OotProgram, OotBuild) }));
        AssertError(ex, "display name must be non-empty");
        AssertError(ex, "platform must be non-empty");
    }

    // ---------------- rule 3: matchers and integration ids ----------------

    [Fact]
    public void Game_without_matchers_is_rejected()
        => AssertError(Invalid(Game(OotId, OotName)), $"game '{OotId}': has no identity matchers");

    [Fact]
    public void Null_matcher_is_rejected()
        => AssertError(Invalid(Game(OotId, OotName, new CatalogIdentityMatcher[] { null! })), "matcher #0 is null");

    [Theory]
    [InlineData("Longshot")]
    [InlineData("other--recomp")]
    [InlineData("-longshot")]
    [InlineData("")]
    public void Invalid_integration_ids_are_rejected(string integration)
        => AssertError(Invalid(Game(OotId, OotName,
                new CatalogIdentityMatcher(integration, Facts((ProgramKey, OotProgram)), new[] { LongshotBuild(OotBuild) }))),
            "has invalid integration id");

    [Fact]
    public void Two_matchers_for_the_same_integration_in_one_game_are_rejected()
        => AssertError(Invalid(Game(OotId, OotName, LongshotMatcher(OotProgram, OotBuild), LongshotMatcher(OotProgram, OtherBuild))),
            "more than one matcher for integration 'longshot'");

    // ---------------- rule 4: identity facts ----------------

    [Fact]
    public void Matcher_without_identity_facts_is_rejected()
        => AssertError(Invalid(Game(OotId, OotName,
                new CatalogIdentityMatcher(LongshotIntegration, Facts(), new[] { LongshotBuild(OotBuild) }))),
            "matcher 'longshot': has no identity facts");

    [Fact]
    public void Invalid_identity_fact_key_is_rejected()
        => AssertError(Invalid(Game(OotId, OotName,
                new CatalogIdentityMatcher(LongshotIntegration, Facts(("Switch2.ProgramId", OotProgram)), new[] { LongshotBuild(OotBuild) }))),
            "identity: invalid fact key 'Switch2.ProgramId'");

    [Theory]
    [InlineData("")]
    [InlineData(" 0400486024dec000")]
    [InlineData("0400486024dec000\n")]
    public void Invalid_identity_fact_value_is_rejected(string value)
        => AssertError(Invalid(Game(OotId, OotName, LongshotMatcher(value, OotBuild))),
            $"identity: fact '{ProgramKey}' has an invalid value");

    // ---------------- rule 5: builds ----------------

    [Fact]
    public void Matcher_without_builds_is_rejected()
        => AssertError(Invalid(Game(OotId, OotName, LongshotMatcher(OotProgram))), "matcher 'longshot': lists no supported builds");

    [Fact]
    public void Null_build_is_rejected()
        => AssertError(Invalid(Game(OotId, OotName,
                new CatalogIdentityMatcher(LongshotIntegration, Facts((ProgramKey, OotProgram)), new CatalogBuild[] { null! }))),
            "build #0: is null");

    [Fact]
    public void Build_without_facts_is_rejected()
        => AssertError(Invalid(Game(OotId, OotName,
                new CatalogIdentityMatcher(LongshotIntegration, Facts((ProgramKey, OotProgram)), new[] { new CatalogBuild(Facts()) }))),
            "build #0: has no build facts");

    [Fact]
    public void Duplicate_build_tuples_in_one_matcher_are_rejected()
        => AssertError(Invalid(Game(OotId, OotName, LongshotMatcher(OotProgram, OotBuild, OotBuild))),
            $"build #1: duplicate build ({BuildKey}={OotBuild})");

    [Fact]
    public void Builds_with_inconsistent_key_sets_in_one_matcher_are_rejected()
        => AssertError(Invalid(Game(OotId, OotName,
                new CatalogIdentityMatcher(LongshotIntegration, Facts((ProgramKey, OotProgram)), new[]
                {
                    LongshotBuild(OotBuild),
                    new CatalogBuild(Facts((BuildKey, OtherBuild), ("switch2.extra", "x"))),
                }))),
            $"build #1: uses build keys [switch2.extra, {BuildKey}], expected [{BuildKey}]");

    [Fact]
    public void Build_key_that_is_also_an_identity_key_is_rejected()
        => AssertError(Invalid(Game(OotId, OotName,
                new CatalogIdentityMatcher(LongshotIntegration, Facts((ProgramKey, OotProgram)), new[] { new CatalogBuild(Facts((ProgramKey, OotProgram))) }))),
            $"fact '{ProgramKey}' is used both as an identity fact and a build fact");

    [Fact]
    public void Invalid_build_keys_values_and_labels_are_rejected()
    {
        var ex = Invalid(Game(OotId, OotName,
            new CatalogIdentityMatcher(LongshotIntegration, Facts((ProgramKey, OotProgram)), new[]
            {
                new CatalogBuild(Facts(("bad key", OotBuild))),
            }),
            new CatalogIdentityMatcher(OtherIntegration, Facts((OtherIdentityKey, "h")), new[]
            {
                new CatalogBuild(Facts((OtherBuildKey, "")), label: " padded label"),
            })));

        AssertError(ex, "build #0: invalid fact key 'bad key'");
        AssertError(ex, $"build #0: fact '{OtherBuildKey}' has an invalid value");
        AssertError(ex, "build #0: label must be non-empty text");
    }

    // ---------------- rule 6: consistent key sets per integration ----------------

    [Fact]
    public void Inconsistent_identity_key_sets_across_games_are_rejected()
        => AssertError(Invalid(Oot(), Game(OtherGameId, "Other",
                new CatalogIdentityMatcher(LongshotIntegration, Facts(("switch2.titleId", OtherProgram)), new[] { LongshotBuild(OtherBuild) }))),
            $"integration 'longshot': game '{OtherGameId}' uses identity keys [switch2.titleId], expected [{ProgramKey}]");

    [Fact]
    public void Inconsistent_build_key_sets_across_games_are_rejected()
        => AssertError(Invalid(Oot(), Game(OtherGameId, "Other",
                new CatalogIdentityMatcher(LongshotIntegration, Facts((ProgramKey, OtherProgram)), new[] { new CatalogBuild(Facts(("switch2.otherBuildId", OtherBuild))) }))),
            $"integration 'longshot': game '{OtherGameId}' uses build keys [switch2.otherBuildId], expected [{BuildKey}]");

    // ---------------- rules 7 and 8: uniqueness across games ----------------

    [Fact]
    public void Duplicate_identity_facts_across_games_are_rejected()
        => AssertError(Invalid(Oot(), Game(OtherGameId, "Other", LongshotMatcher(OotProgram, OtherBuild))),
            $"integration 'longshot': games '{OotId}' and '{OtherGameId}' share identical identity facts ({ProgramKey}={OotProgram})");

    [Fact]
    public void Build_tuple_under_two_games_is_rejected()
        => AssertError(Invalid(Oot(), Game(OtherGameId, "Other", LongshotMatcher(OtherProgram, OotBuild))),
            $"integration 'longshot': build ({BuildKey}={OotBuild}) is listed under both '{OotId}' and '{OtherGameId}'");

    [Fact]
    public void Same_values_under_different_integrations_do_not_collide()
    {
        // Uniqueness is per integration: another integration may reuse identical values under its own keys.
        var game = Game(OtherRecompGameId, "Other",
            new CatalogIdentityMatcher(OtherIntegration, Facts((OtherIdentityKey, OotProgram)), new[] { new CatalogBuild(Facts((OtherBuildKey, OotBuild))) }));
        Assert.Equal(2, new GameCatalog(new[] { Oot(), game }).Games.Count);
    }

    // ---------------- aggregation and determinism ----------------

    [Fact]
    public void Multiple_errors_are_reported_together()
    {
        var ex = Invalid(
            Game("Bad Id", "", LongshotMatcher(OotProgram)),
            Game(OotId, OotName, new CatalogIdentityMatcher("Longshot", Facts((ProgramKey, OotProgram)), new[] { LongshotBuild(OotBuild) })),
            Game(OotId, OotName, LongshotMatcher(OotProgram, OotBuild)));

        AssertError(ex, "game #0: game id 'Bad Id' must be lowercase kebab-case");
        AssertError(ex, "game #0: display name must be non-empty");
        AssertError(ex, "lists no supported builds");
        AssertError(ex, "has invalid integration id 'Longshot'");
        AssertError(ex, $"duplicate game id '{OotId}'");
        Assert.True(ex.Errors.Count >= 5);
        Assert.Contains($"({ex.Errors.Count} errors)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_errors_are_deterministic()
    {
        CatalogGame[] Build() => new[]
        {
            Game("Bad", "", LongshotMatcher(OotProgram)),
            Game(OotId, OotName, LongshotMatcher(OotProgram, OotBuild, OotBuild)),
        };

        var first = Invalid(Build()).Errors;
        var second = Invalid(Build()).Errors;
        Assert.Equal(first, second);
    }
}

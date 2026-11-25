using SwitchAchievements.App.Catalog;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests.Catalog;

/// <summary>
/// M5.6.3: the real catalog embedded in the App assembly — exact resource names
/// as compiled, successful loading, exact values, and consumption by the resolver.
/// Acts as a build-time gate: a broken shipped catalog fails these tests.
/// </summary>
public class EmbeddedCatalogTests
{
    private const string Program = "0400486024dec000";
    private const string Build = "e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348";

    private static readonly IIntegrationIdentityFormat[] Formats = { new LongshotIdentityFormat() };

    [Fact]
    public void Compiled_assembly_embeds_exactly_the_expected_forward_slash_resource_names()
    {
        var all = typeof(CatalogLoader).Assembly.GetManifestResourceNames();
        var catalogNames = all
            .Where(n => n.StartsWith("catalog", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("catalog.json", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("game.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            "catalog/catalog.json",
            "catalog/games/zelda-ocarina-of-time/game.json",
        }, catalogNames);
        Assert.DoesNotContain(all, n => n.Contains('\\'));
    }

    [Fact]
    public void Embedded_source_lists_the_resources_unmodified()
        => Assert.Equal(
            new[] { "catalog/catalog.json", "catalog/games/zelda-ocarina-of-time/game.json" },
            new EmbeddedCatalogResourceSource().Names);

    [Fact]
    public void Achievements_file_is_not_embedded_yet()
        => Assert.DoesNotContain(typeof(CatalogLoader).Assembly.GetManifestResourceNames(),
            n => n.EndsWith("achievements.json", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Embedded_catalog_loads_with_the_exact_ocarina_of_time_values()
    {
        var result = CatalogLoader.LoadEmbedded(Formats);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var game = Assert.Single(result.Catalog!.Games);
        Assert.Equal("zelda-ocarina-of-time", game.Id.Value);
        Assert.Equal("The Legend of Zelda: Ocarina of Time", game.DisplayName);
        Assert.Equal("switch2", game.Platform);

        var matcher = Assert.Single(game.Matchers);
        Assert.Equal("longshot", matcher.IntegrationId);
        var identity = Assert.Single(matcher.IdentityFacts);
        Assert.Equal(LongshotIdentityFormat.ProgramIdKey, identity.Key);
        Assert.Equal(Program, identity.Value);

        var build = Assert.Single(matcher.SupportedBuilds);
        var buildFact = Assert.Single(build.Facts);
        Assert.Equal(LongshotIdentityFormat.MainBuildIdKey, buildFact.Key);
        Assert.Equal(Build, buildFact.Value);
        Assert.Null(build.Label);
    }

    [Fact]
    public void Embedded_catalog_is_consumed_by_the_resolver()
    {
        var catalog = CatalogLoader.LoadEmbedded(Formats).Catalog!;
        var process = new ProcessFingerprint(34236, new DateTime(2026, 11, 24, 5, 51, 31, 839, DateTimeKind.Utc));

        GameIdentification Resolve(string program, string build) => GameIdentityResolver.Resolve(catalog,
            new GameIdentityEvidence("longshot", process,
                new Dictionary<string, string>
                {
                    [LongshotIdentityFormat.ProgramIdKey] = program,
                    [LongshotIdentityFormat.MainBuildIdKey] = build,
                }, null, DateTime.UtcNow));

        var oot = Resolve(Program, Build);
        Assert.Equal(IdentificationStatus.Identified, oot.Status);
        Assert.Equal("zelda-ocarina-of-time", oot.Game!.GameId.Value);
        Assert.Equal(process, oot.Game.Process);

        Assert.Equal(IdentificationStatus.Unsupported, Resolve(Program, "ffffffffffffffffffffffffffffffffffffffff").Status);
        Assert.Equal(IdentificationStatus.Unsupported, Resolve("0100000000000000", Build).Status);
        Assert.Equal(IdentificationStatus.Unsupported, Resolve("0400486024DEC000", Build).Status); // exact canonical form only
    }

    [Fact]
    public void Embedded_loads_are_deterministic()
    {
        var first = CatalogLoader.LoadEmbedded(Formats).Catalog!;
        var second = CatalogLoader.LoadEmbedded(Formats).Catalog!;
        Assert.Equal(first.Games.Select(g => g.Id.Value), second.Games.Select(g => g.Id.Value));
        Assert.Equal(first.Games[0].Matchers[0].SupportedBuilds[0].Facts, second.Games[0].Matchers[0].SupportedBuilds[0].Facts);
    }
}

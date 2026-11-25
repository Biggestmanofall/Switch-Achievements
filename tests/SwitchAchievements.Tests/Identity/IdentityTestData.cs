using SwitchAchievements.Core;

namespace SwitchAchievements.Tests.Identity;

/// <summary>
/// In-memory identity fixtures (M5.6.2). The Ocarina of Time values are the
/// verified Longshot v3.0.0 ones, written in the canonical lowercase-hex form the
/// later provider and catalog loader will produce. Core itself treats every
/// fact key and value as an opaque string.
/// </summary>
internal static class IdentityTestData
{
    public const string LongshotIntegration = "longshot";
    public const string ProgramKey = "switch2.programId";
    public const string BuildKey = "switch2.mainBuildId";

    public const string OotId = "zelda-ocarina-of-time";
    public const string OotName = "The Legend of Zelda: Ocarina of Time";
    public const string OotProgram = "0400486024dec000";
    public const string OotBuild = "e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348";

    // A second, synthetic Longshot game (not a real title).
    public const string OtherGameId = "synthetic-longshot-game";
    public const string OtherProgram = "0400000000000001";
    public const string OtherBuild = "1111111111111111111111111111111111111111";

    // A synthetic second integration with entirely different fact keys.
    public const string OtherIntegration = "other-recomp";
    public const string OtherIdentityKey = "otherrecomp.contentHash";
    public const string OtherBuildKey = "otherrecomp.revision";
    public const string OtherRecompGameId = "synthetic-other-recomp-game";

    public static readonly ProcessFingerprint Process =
        new(34236, new DateTime(2026, 11, 24, 5, 51, 31, 839, DateTimeKind.Utc));

    public static Dictionary<string, string> Facts(params (string Key, string Value)[] pairs)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            facts[key] = value;
        return facts;
    }

    public static Dictionary<string, string> OotFacts(string program = OotProgram, string build = OotBuild)
        => Facts((ProgramKey, program), (BuildKey, build));

    public static CatalogBuild LongshotBuild(string build, string? label = null)
        => new(Facts((BuildKey, build)), label);

    public static CatalogIdentityMatcher LongshotMatcher(string program, params string[] builds)
        => new(LongshotIntegration, Facts((ProgramKey, program)), builds.Select(b => LongshotBuild(b)).ToArray());

    public static CatalogGame Game(string id, string name, params CatalogIdentityMatcher[] matchers)
        => new(new GameId(id), name, "switch2", matchers);

    public static CatalogGame Oot()
        => Game(OotId, OotName, new CatalogIdentityMatcher(LongshotIntegration, Facts((ProgramKey, OotProgram)), new[] { LongshotBuild(OotBuild, "v131072") }));

    public static CatalogGame OtherLongshotGame()
        => Game(OtherGameId, "Synthetic Longshot Game", LongshotMatcher(OtherProgram, OtherBuild));

    public static CatalogGame OtherRecompGame()
        => Game(OtherRecompGameId, "Synthetic Other-Recomp Game",
            new CatalogIdentityMatcher(OtherIntegration, Facts((OtherIdentityKey, "sha256:abc123")),
                new[] { new CatalogBuild(Facts((OtherBuildKey, "r42"))) }));

    public static GameCatalog OotCatalog() => new(new[] { Oot() });

    public static GameIdentityEvidence Evidence(
        IReadOnlyDictionary<string, string>? facts,
        string integration = LongshotIntegration,
        ProcessFingerprint? process = null,
        params EvidenceIssue[] issues)
        => new(integration, process ?? Process, facts, issues, new DateTime(2026, 11, 24, 6, 0, 0, DateTimeKind.Utc));
}

using SwitchAchievements.Core;
using static SwitchAchievements.Tests.Identity.IdentityTestData;

namespace SwitchAchievements.Tests.Identity;

/// <summary>M5.6.2: the pure catalog-driven resolver and every identification outcome.</summary>
public class GameIdentityResolverTests
{
    private static GameIdentification Resolve(GameIdentityEvidence evidence, GameCatalog? catalog = null)
        => GameIdentityResolver.Resolve(catalog ?? OotCatalog(), evidence);

    private static void AssertNotIdentified(GameIdentification result, IdentificationStatus expected, string diagnosticFragment)
    {
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Game);
        Assert.False(result.IsIdentified);
        Assert.True(result.Diagnostics.Any(d => d.Contains(diagnosticFragment, StringComparison.Ordinal)),
            $"expected a diagnostic containing \"{diagnosticFragment}\"; got:\n{string.Join("\n", result.Diagnostics)}");
    }

    // ---------------- Identified ----------------

    [Fact]
    public void Exact_supported_ocarina_of_time_identity_is_identified()
    {
        var catalog = OotCatalog();
        var evidence = Evidence(OotFacts());

        var result = GameIdentityResolver.Resolve(catalog, evidence);

        Assert.Equal(IdentificationStatus.Identified, result.Status);
        Assert.True(result.IsIdentified);
        var game = Assert.IsType<IdentifiedGame>(result.Game);
        Assert.Equal(OotId, game.GameId.Value);
        Assert.Equal(OotName, game.DisplayName);
        Assert.Equal("switch2", game.Platform);
        Assert.Equal(LongshotIntegration, game.IntegrationId);
        Assert.Same(catalog.Games[0].Matchers[0].SupportedBuilds[0], game.Build);
        Assert.Equal("v131072", game.Build.Label);
        Assert.Equal(Process, game.Process);            // bound to the evidence's process instance
        Assert.Same(evidence, result.Evidence);         // evidence retained
        Assert.Contains(result.Diagnostics, d => d.StartsWith($"identified '{OotId}' via 'longshot'", StringComparison.Ordinal));
    }

    [Fact]
    public void Extra_unused_facts_are_ignored()
    {
        var facts = OotFacts();
        facts["switch2.nupVersion"] = "131072";
        facts["longshot.runtimeVersion"] = "v3.0.0";
        Assert.Equal(IdentificationStatus.Identified, Resolve(Evidence(facts)).Status);
    }

    [Fact]
    public void Non_conflict_provider_issues_do_not_prevent_identification_but_are_reported()
    {
        var result = Resolve(Evidence(OotFacts(), issues: new EvidenceIssue(EvidenceIssueKind.Other, "optional corroboration unavailable")));
        Assert.Equal(IdentificationStatus.Identified, result.Status);
        Assert.Contains("provider issue (Other): optional corroboration unavailable", result.Diagnostics);
    }

    [Fact]
    public void Matching_one_of_several_supported_builds_identifies_that_build()
    {
        const string second = "2222222222222222222222222222222222222222";
        var catalog = new GameCatalog(new[] { Game(OotId, OotName, LongshotMatcher(OotProgram, OotBuild, second)) });

        var result = Resolve(Evidence(OotFacts(build: second)), catalog);

        Assert.Equal(IdentificationStatus.Identified, result.Status);
        Assert.Equal(second, result.Game!.Build.Facts[BuildKey]);
    }

    // ---------------- Unsupported ----------------

    [Fact]
    public void Unsupported_program_is_unsupported()
        => AssertNotIdentified(Resolve(Evidence(OotFacts(program: "0100000000000000"))),
            IdentificationStatus.Unsupported, $"unsupported program: no catalog game for 'longshot' has identity ({ProgramKey}=0100000000000000)");

    [Fact]
    public void Unsupported_build_of_a_known_program_is_unsupported()
        => AssertNotIdentified(Resolve(Evidence(OotFacts(build: "ffffffffffffffffffffffffffffffffffffffff"))),
            IdentificationStatus.Unsupported, $"unsupported build of '{OotId}' via 'longshot': ({BuildKey}=ffffffffffffffffffffffffffffffffffffffff)");

    [Theory]
    [InlineData("0400486024DEC000", OotBuild)]                                  // program id, wrong case
    [InlineData(OotProgram, "E0F3CCFAF50065AE4EA01EB5AEB13B1A45DA3348")]       // build id, wrong case
    public void Wrong_case_identifiers_fail_safely_as_unsupported(string program, string build)
    {
        var result = Resolve(Evidence(OotFacts(program, build)));
        Assert.Equal(IdentificationStatus.Unsupported, result.Status);
        Assert.Null(result.Game);
    }

    [Fact]
    public void Wrong_integration_with_facts_is_unsupported()
        => AssertNotIdentified(Resolve(Evidence(OotFacts(), integration: "other-recomp")),
            IdentificationStatus.Unsupported, "integration 'other-recomp' has no supported games in the catalog");

    // ---------------- Unknown ----------------

    [Fact]
    public void Wrong_integration_without_facts_is_unknown()
        => AssertNotIdentified(Resolve(Evidence(Facts(), integration: "other-recomp")),
            IdentificationStatus.Unknown, "no identity facts from 'other-recomp'");

    [Fact]
    public void Integration_with_no_catalog_matchers_is_unknown_or_unsupported()
    {
        var empty = new GameCatalog(Array.Empty<CatalogGame>());
        AssertNotIdentified(Resolve(Evidence(Facts()), empty), IdentificationStatus.Unknown, "no identity facts from 'longshot'");
        AssertNotIdentified(Resolve(Evidence(OotFacts()), empty), IdentificationStatus.Unsupported, "integration 'longshot' has no supported games");
    }

    [Fact]
    public void Missing_identity_facts_are_unknown()
        => AssertNotIdentified(Resolve(Evidence(Facts((BuildKey, OotBuild)))),
            IdentificationStatus.Unknown, $"missing identity fact(s) from 'longshot': {ProgramKey}");

    [Fact]
    public void Missing_build_facts_are_unknown()
        => AssertNotIdentified(Resolve(Evidence(Facts((ProgramKey, OotProgram)))),
            IdentificationStatus.Unknown, $"identity matches '{OotId}', but build fact(s) are missing: {BuildKey}");

    [Fact]
    public void No_facts_at_all_is_unknown()
        => AssertNotIdentified(Resolve(Evidence(Facts())), IdentificationStatus.Unknown, "missing identity fact(s)");

    [Theory]
    [InlineData("Longshot")]
    [InlineData("other--recomp")]
    [InlineData("")]
    public void Invalid_evidence_integration_id_is_unknown(string integration)
        => AssertNotIdentified(Resolve(Evidence(OotFacts(), integration: integration)),
            IdentificationStatus.Unknown, "evidence has an invalid integration id");

    [Fact]
    public void Invalid_fact_key_is_unknown_and_not_echoed()
    {
        var facts = OotFacts();
        facts["C:\\Users\\secret\\key"] = "x";
        var result = Resolve(Evidence(facts));
        AssertNotIdentified(result, IdentificationStatus.Unknown, "contains an invalid fact key");
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(" 0400486024dec000")]
    [InlineData("0400486024dec000 ")]
    [InlineData("")]
    [InlineData("0400486024dec000\nC:\\Users\\secret\\main.npdm")]
    public void Invalid_fact_value_is_unknown_and_not_echoed(string program)
    {
        var result = Resolve(Evidence(OotFacts(program: program)));
        AssertNotIdentified(result, IdentificationStatus.Unknown, $"has an invalid value for fact '{ProgramKey}'");
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Null_fact_value_is_unknown()
    {
        var facts = OotFacts();
        facts[BuildKey] = null!;
        AssertNotIdentified(Resolve(Evidence(facts)), IdentificationStatus.Unknown, $"has an invalid value for fact '{BuildKey}'");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-4, true)]
    [InlineData(34236, false)] // valid PID but no start time
    public void Invalid_process_fingerprint_is_unknown_even_with_exact_facts(int pid, bool withStart)
    {
        var fingerprint = new ProcessFingerprint(pid, withStart ? Process.ProcessStartUtc : default);
        AssertNotIdentified(Resolve(Evidence(OotFacts(), process: fingerprint)),
            IdentificationStatus.Unknown, "is not bound to a valid process fingerprint");
    }

    // ---------------- Conflict ----------------

    [Fact]
    public void Build_tuple_belonging_to_another_game_is_a_conflict()
    {
        var catalog = new GameCatalog(new[] { Oot(), OtherLongshotGame() });
        AssertNotIdentified(Resolve(Evidence(OotFacts(build: OtherBuild)), catalog),
            IdentificationStatus.Conflict, $"identity matches '{OotId}', but build ({BuildKey}={OtherBuild}) belongs to '{OtherGameId}'");
    }

    [Fact]
    public void Provider_reported_conflict_is_a_conflict_even_with_exact_facts()
    {
        var result = Resolve(Evidence(OotFacts(), issues: new EvidenceIssue(EvidenceIssueKind.Conflict, "sources disagree", ProgramKey)));
        AssertNotIdentified(result, IdentificationStatus.Conflict, "provider 'longshot' reported conflicting evidence");
        Assert.Contains($"provider issue (Conflict, {ProgramKey}): sources disagree", result.Diagnostics);
    }

    [Fact]
    public void Invalid_evidence_takes_precedence_over_a_provider_conflict()
    {
        var result = Resolve(Evidence(OotFacts(), process: new ProcessFingerprint(0, default),
            issues: new EvidenceIssue(EvidenceIssueKind.Conflict, "x")));
        Assert.Equal(IdentificationStatus.Unknown, result.Status);
    }

    // ---------------- invariants and determinism ----------------

    public static TheoryData<string> NonIdentifiedCases() => new()
    {
        "unsupported-program", "unsupported-build", "wrong-integration", "missing-identity", "missing-build",
        "invalid-key", "invalid-value", "invalid-fingerprint", "provider-conflict", "other-game-build", "no-facts",
    };

    [Theory]
    [MemberData(nameof(NonIdentifiedCases))]
    public void Non_identified_results_never_carry_an_IdentifiedGame(string scenario)
    {
        var catalog = new GameCatalog(new[] { Oot(), OtherLongshotGame() });
        var evidence = scenario switch
        {
            "unsupported-program" => Evidence(OotFacts(program: "0100000000000000")),
            "unsupported-build" => Evidence(OotFacts(build: "ffffffffffffffffffffffffffffffffffffffff")),
            "wrong-integration" => Evidence(OotFacts(), integration: "other-recomp"),
            "missing-identity" => Evidence(Facts((BuildKey, OotBuild))),
            "missing-build" => Evidence(Facts((ProgramKey, OotProgram))),
            "invalid-key" => Evidence(Facts(("Bad Key", "x"))),
            "invalid-value" => Evidence(OotFacts(program: " x")),
            "invalid-fingerprint" => Evidence(OotFacts(), process: new ProcessFingerprint(0, default)),
            "provider-conflict" => Evidence(OotFacts(), issues: new EvidenceIssue(EvidenceIssueKind.Conflict, "x")),
            "other-game-build" => Evidence(OotFacts(build: OtherBuild)),
            "no-facts" => Evidence(Facts()),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var result = GameIdentityResolver.Resolve(catalog, evidence);

        Assert.NotEqual(IdentificationStatus.Identified, result.Status);
        Assert.Null(result.Game);
        Assert.False(result.IsIdentified);
        Assert.Same(evidence, result.Evidence);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Diagnostics_are_deterministic_regardless_of_fact_insertion_order()
    {
        var forward = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProgramKey] = OotProgram, [BuildKey] = "ffffffffffffffffffffffffffffffffffffffff", ["z.extra"] = "1", ["a.extra"] = "2",
        };
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in forward.Keys.Reverse())
            reverse[key] = forward[key];

        var issue = new EvidenceIssue(EvidenceIssueKind.Other, "note");
        var first = Resolve(Evidence(forward, issues: issue));
        var second = Resolve(Evidence(reverse, issues: issue));

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Equal(Resolve(Evidence(OotFacts())).Diagnostics, Resolve(Evidence(OotFacts())).Diagnostics);
    }

    [Fact]
    public void Resolver_does_not_mutate_its_inputs()
    {
        var catalog = OotCatalog();
        var evidence = Evidence(OotFacts());
        GameIdentityResolver.Resolve(catalog, evidence);

        Assert.Equal(2, evidence.Facts.Count);
        Assert.Single(catalog.Games);
        Assert.Single(catalog.Games[0].Matchers[0].SupportedBuilds);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => GameIdentityResolver.Resolve(null!, Evidence(OotFacts())));
        Assert.Throws<ArgumentNullException>(() => GameIdentityResolver.Resolve(OotCatalog(), null!));
    }

    // ---------------- a second integration with entirely different keys ----------------

    [Fact]
    public void Synthetic_second_integration_resolves_independently()
    {
        var catalog = new GameCatalog(new[] { Oot(), OtherRecompGame() });
        var otherFacts = Facts((OtherIdentityKey, "sha256:abc123"), (OtherBuildKey, "r42"));

        // The other integration identifies its own game through its own keys.
        var other = GameIdentityResolver.Resolve(catalog, Evidence(otherFacts, integration: OtherIntegration));
        Assert.Equal(IdentificationStatus.Identified, other.Status);
        Assert.Equal(OtherRecompGameId, other.Game!.GameId.Value);
        Assert.Equal(OtherIntegration, other.Game.IntegrationId);

        // Longshot is unaffected by the other integration's entry.
        var longshot = GameIdentityResolver.Resolve(catalog, Evidence(OotFacts()));
        Assert.Equal(OotId, longshot.Game!.GameId.Value);

        // Keys never cross integrations: Longshot facts under the other integration, and vice versa, are Unknown.
        Assert.Equal(IdentificationStatus.Unknown, GameIdentityResolver.Resolve(catalog, Evidence(OotFacts(), integration: OtherIntegration)).Status);
        Assert.Equal(IdentificationStatus.Unknown, GameIdentityResolver.Resolve(catalog, Evidence(otherFacts)).Status);

        // An unsupported revision of the other game is Unsupported, not someone else's game.
        var unsupported = GameIdentityResolver.Resolve(catalog,
            Evidence(Facts((OtherIdentityKey, "sha256:abc123"), (OtherBuildKey, "r43")), integration: OtherIntegration));
        Assert.Equal(IdentificationStatus.Unsupported, unsupported.Status);
        Assert.Null(unsupported.Game);
    }
}

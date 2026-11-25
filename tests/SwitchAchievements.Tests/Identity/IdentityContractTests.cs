using System.Reflection;
using SwitchAchievements.Core;
using static SwitchAchievements.Tests.Identity.IdentityTestData;

namespace SwitchAchievements.Tests.Identity;

/// <summary>M5.6.2: generic validators, evidence/issue models, and the IdentifiedGame authorization boundary.</summary>
public class IdentityContractTests
{
    // ---------------- integration ids ----------------

    [Theory]
    [InlineData("longshot")]
    [InlineData("other-recomp")]
    [InlineData("switch2")]
    [InlineData("a")]
    [InlineData("a-b-c")]
    [InlineData("recomp2-x9")]
    public void Valid_integration_ids(string id) => Assert.True(IntegrationIdentifier.IsValid(id));

    [Theory]
    [InlineData("Longshot")]
    [InlineData("other--recomp")]
    [InlineData("-longshot")]
    [InlineData("longshot-")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("123")]          // no letter
    [InlineData("12-34")]        // no letter
    [InlineData("long shot")]
    [InlineData("long_shot")]
    [InlineData("long.shot")]
    [InlineData("lóngshot")]     // non-ASCII
    [InlineData("-")]
    public void Invalid_integration_ids(string? id) => Assert.False(IntegrationIdentifier.IsValid(id));

    [Fact]
    public void Integration_id_length_is_capped_at_64()
    {
        Assert.True(IntegrationIdentifier.IsValid(new string('a', 64)));
        Assert.False(IntegrationIdentifier.IsValid(new string('a', 65)));
    }

    // ---------------- fact keys ----------------

    [Theory]
    [InlineData("switch2.programId")]
    [InlineData("switch2.mainBuildId")]
    [InlineData("otherrecomp.contentHash")]
    [InlineData("a.b")]
    [InlineData("a.b.c")]
    [InlineData("x1.y2Z3")]
    public void Valid_fact_keys(string key) => Assert.True(IdentityFactKey.IsValid(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("switch2")]              // a single segment
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a..b")]
    [InlineData("Switch2.programId")]    // segment must start lowercase
    [InlineData("switch2.ProgramId")]
    [InlineData("2a.b")]
    [InlineData("a.2b")]
    [InlineData("a.b-c")]
    [InlineData("a.b_c")]
    [InlineData("a.b c")]
    [InlineData("a.bé")]
    public void Invalid_fact_keys(string? key) => Assert.False(IdentityFactKey.IsValid(key));

    [Fact]
    public void Fact_key_length_is_capped_at_64()
    {
        var max = "a." + new string('b', 62);
        Assert.True(IdentityFactKey.IsValid(max));
        Assert.False(IdentityFactKey.IsValid(max + "b"));
    }

    // ---------------- fact values ----------------

    [Theory]
    [InlineData(OotProgram)]
    [InlineData(OotBuild)]
    [InlineData("0400486024DEC000")]     // Core does not judge case or hex
    [InlineData("x")]
    [InlineData("inner spaces are fine")]
    [InlineData("sha256:abc123")]
    public void Valid_fact_values(string value) => Assert.True(IdentityFactValue.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("\ttab")]
    [InlineData("new\nline")]
    [InlineData("nul\u0000")]
    public void Invalid_fact_values(string? value) => Assert.False(IdentityFactValue.IsValid(value));

    [Fact]
    public void Fact_value_length_is_capped_at_256()
    {
        Assert.True(IdentityFactValue.IsValid(new string('v', 256)));
        Assert.False(IdentityFactValue.IsValid(new string('v', 257)));
    }

    // ---------------- evidence ----------------

    [Fact]
    public void Evidence_copies_facts_and_issues_defensively()
    {
        var facts = OotFacts();
        var issues = new List<EvidenceIssue> { new(EvidenceIssueKind.Other, "note") };
        var evidence = new GameIdentityEvidence(LongshotIntegration, Process, facts, issues, DateTime.UtcNow);

        facts[ProgramKey] = "changed";
        facts["switch2.extra"] = "added";
        issues.Add(new EvidenceIssue(EvidenceIssueKind.Conflict, "added later"));

        Assert.Equal(OotProgram, evidence.Facts[ProgramKey]);
        Assert.False(evidence.Facts.ContainsKey("switch2.extra"));
        Assert.Single(evidence.Issues);
        Assert.Equal(EvidenceIssueKind.Other, evidence.Issues[0].Kind);
    }

    [Fact]
    public void Evidence_collections_are_read_only()
    {
        var evidence = Evidence(OotFacts(), issues: new EvidenceIssue(EvidenceIssueKind.Other, "x"));

        var facts = Assert.IsAssignableFrom<IDictionary<string, string>>(evidence.Facts);
        Assert.Throws<NotSupportedException>(() => facts.Add("a.b", "c"));
        var issues = Assert.IsAssignableFrom<IList<EvidenceIssue>>(evidence.Issues);
        Assert.Throws<NotSupportedException>(() => issues.Add(new EvidenceIssue(EvidenceIssueKind.Other, "y")));
    }

    [Fact]
    public void Evidence_null_collections_become_empty_and_null_arguments_throw()
    {
        var evidence = new GameIdentityEvidence(LongshotIntegration, Process, null, null, DateTime.UtcNow);
        Assert.Empty(evidence.Facts);
        Assert.Empty(evidence.Issues);

        Assert.Throws<ArgumentNullException>(() => new GameIdentityEvidence(null!, Process, null, null, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => new GameIdentityEvidence(LongshotIntegration, Process, null, new EvidenceIssue[] { null! }, DateTime.UtcNow));
    }

    [Fact]
    public void Evidence_uses_ordinal_fact_keys()
    {
        var evidence = Evidence(Facts(("a.b", "1"), ("a.B", "2")));
        Assert.Equal(2, evidence.Facts.Count);
        Assert.Equal("1", evidence.Facts["a.b"]);
        Assert.Equal("2", evidence.Facts["a.B"]);
    }

    // ---------------- issue sanitisation ----------------

    [Fact]
    public void Issue_message_control_characters_are_replaced_and_trimmed()
    {
        var issue = new EvidenceIssue(EvidenceIssueKind.Malformed, "  bad\r\nheader\tvalue\u0000  ", "switch2.programId");
        Assert.Equal("bad  header value", issue.Message);
        Assert.Equal("switch2.programId", issue.FactKey);
    }

    [Fact]
    public void Issue_message_and_fact_key_are_length_capped()
    {
        var issue = new EvidenceIssue(EvidenceIssueKind.Other, new string('m', 5000), new string('k', 500));
        Assert.Equal(EvidenceIssue.MaxMessageLength, issue.Message.Length);
        Assert.Equal(IdentityFactKey.MaxLength, issue.FactKey!.Length);
    }

    [Fact]
    public void Issue_null_message_is_empty_and_undefined_kind_throws()
    {
        Assert.Equal(string.Empty, new EvidenceIssue(EvidenceIssueKind.Missing, null).Message);
        Assert.Null(new EvidenceIssue(EvidenceIssueKind.Missing, "m").FactKey);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceIssue((EvidenceIssueKind)99, "m"));
    }

    // ---------------- construction boundary (public API surface) ----------------

    private static readonly Assembly Core = typeof(GameIdentityResolver).Assembly;

    private static bool IsExternallyVisible(MethodBase? m)
        => m is not null && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly);

    [Theory]
    [InlineData(typeof(IdentifiedGame))]
    [InlineData(typeof(GameIdentification))]
    public void Authorization_types_have_no_externally_visible_constructors_or_setters(Type type)
    {
        Assert.True(type.IsSealed);
        Assert.DoesNotContain(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            c => IsExternallyVisible(c));
        Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => Assert.False(IsExternallyVisible(p.SetMethod), $"{type.Name}.{p.Name} has a visible setter"));
        // Not a record: no compiler-generated clone (which a 'with' expression would use).
        Assert.Null(type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void Only_the_resolver_publicly_produces_a_GameIdentification()
    {
        var producers = PublicMethodsReturning(typeof(GameIdentification));
        Assert.Equal(new[] { "GameIdentityResolver.Resolve" }, producers);
    }

    [Fact]
    public void No_public_method_produces_an_IdentifiedGame_except_reading_a_resolved_result()
    {
        var producers = PublicMethodsReturning(typeof(IdentifiedGame));
        Assert.Equal(new[] { "GameIdentification.get_Game" }, producers);
    }

    [Fact]
    public void GameIdentification_factories_are_not_public()
    {
        var statics = typeof(GameIdentification).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Empty(statics);
    }

    private static string[] PublicMethodsReturning(Type returnType)
        => Core.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Concat<MethodInfo>(t.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => m.IsFamily || m.IsFamilyOrAssembly)))
            .Where(m => m.ReturnType == returnType || Nullable.GetUnderlyingType(m.ReturnType) == returnType)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

    // ---------------- GameId is a label, IdentifiedGame is authorization ----------------

    [Fact]
    public void A_GameId_label_alone_never_yields_an_IdentifiedGame()
    {
        // Anyone can construct the label...
        var label = new GameId(OotId);
        Assert.Equal(OotId, label.Value);

        // ...but a connected session without identity evidence is never authorized.
        var result = GameIdentityResolver.Resolve(OotCatalog(), Evidence(facts: null));
        Assert.Equal(IdentificationStatus.Unknown, result.Status);
        Assert.Null(result.Game);
        Assert.False(result.IsIdentified);
    }

    [Fact]
    public void GameId_Unknown_is_unchanged_and_is_not_a_catalog_id()
    {
        Assert.Equal("unknown", GameId.Unknown.Value);
        var ex = Assert.Throws<CatalogValidationException>(() =>
            new GameCatalog(new[] { Game("unknown", "Placeholder", LongshotMatcher(OotProgram, OotBuild)) }));
        Assert.Contains(ex.Errors, e => e.Contains("game id 'unknown' is reserved", StringComparison.Ordinal));
    }
}

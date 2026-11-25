using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests.Catalog;

/// <summary>M5.6.3: Longshot's canonical identity-fact format (validation only).</summary>
public class LongshotIdentityFormatTests
{
    private const string Program = "0400486024dec000";
    private const string Build = "e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348";

    private static readonly IIntegrationIdentityFormat Format = new LongshotIdentityFormat();

    [Fact]
    public void Exposes_the_longshot_integration_and_exact_key_sets()
    {
        Assert.Equal("longshot", Format.IntegrationId);
        Assert.True(IntegrationIdentifier.IsValid(Format.IntegrationId));
        Assert.Equal(new[] { "switch2.programId" }, Format.IdentityKeys);
        Assert.Equal(new[] { "switch2.mainBuildId" }, Format.BuildKeys);
        Assert.All(Format.IdentityKeys.Concat(Format.BuildKeys), k => Assert.True(IdentityFactKey.IsValid(k)));
    }

    [Fact]
    public void Exact_canonical_program_id_is_accepted()
    {
        Assert.True(Format.IsCanonical(LongshotIdentityFormat.ProgramIdKey, Program));
        Assert.True(LongshotIdentityFormat.IsCanonicalProgramId(Program));
    }

    [Fact]
    public void Exact_canonical_build_id_is_accepted()
    {
        Assert.True(Format.IsCanonical(LongshotIdentityFormat.MainBuildIdKey, Build));
        Assert.True(LongshotIdentityFormat.IsCanonicalMainBuildId(Build));
    }

    [Theory]
    [InlineData("0400486024DEC000")]          // uppercase
    [InlineData("0400486024Dec000")]          // mixed case
    [InlineData("0x0400486024dec000")]        // 0x prefix
    [InlineData("0x400486024dec000")]         // 0x prefix at the right length
    [InlineData("0400486024dec00")]           // 15 characters
    [InlineData("0400486024dec0000")]         // 17 characters
    [InlineData("0400486024dec00g")]          // non-hex
    [InlineData("0400486024-ec000")]
    [InlineData(" 0400486024dec000")]         // surrounding white space
    [InlineData("0400486024dec000 ")]
    [InlineData("0400486024dec000\n")]
    [InlineData("")]
    public void Non_canonical_program_ids_are_rejected(string value)
    {
        Assert.False(Format.IsCanonical(LongshotIdentityFormat.ProgramIdKey, value));
        Assert.False(LongshotIdentityFormat.IsCanonicalProgramId(value));
    }

    [Theory]
    [InlineData("E0F3CCFAF50065AE4EA01EB5AEB13B1A45DA3348")]                          // uppercase
    [InlineData("0xe0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348")]                        // 0x prefix
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da334")]                           // 39 characters
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da33480")]                         // 41 characters
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348000000000000000000000000")] // full 32-byte field
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da334z")]                          // non-hex
    [InlineData(" e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348")]                         // surrounding white space
    [InlineData("e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348\t")]
    [InlineData("")]
    public void Non_canonical_build_ids_are_rejected(string value)
    {
        Assert.False(Format.IsCanonical(LongshotIdentityFormat.MainBuildIdKey, value));
        Assert.False(LongshotIdentityFormat.IsCanonicalMainBuildId(value));
    }

    [Theory]
    [InlineData("switch2.programid", Program)]    // key case differs
    [InlineData("switch2.titleId", Program)]
    [InlineData("other.key", Program)]
    [InlineData("", Program)]
    public void Unknown_fact_keys_are_rejected(string key, string value)
        => Assert.False(Format.IsCanonical(key, value));

    [Fact]
    public void Values_are_not_interchangeable_between_keys()
    {
        Assert.False(Format.IsCanonical(LongshotIdentityFormat.ProgramIdKey, Build));
        Assert.False(Format.IsCanonical(LongshotIdentityFormat.MainBuildIdKey, Program));
    }

    [Fact]
    public void Null_values_are_rejected_by_the_reusable_static_checks()
    {
        Assert.False(LongshotIdentityFormat.IsCanonicalProgramId(null));
        Assert.False(LongshotIdentityFormat.IsCanonicalMainBuildId(null));
    }
}

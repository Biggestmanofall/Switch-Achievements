using System.Buffers.Binary;
using SwitchAchievements.Longshot;
using static SwitchAchievements.Tests.LongshotIdentity.ExefsBytes;

namespace SwitchAchievements.Tests.LongshotIdentity;

/// <summary>M5.6.4: pure NPDM / NSO header parsing (synthetic bytes).</summary>
public class LongshotExefsParserTests
{
    private static (bool Ok, string Value, string Failure) Program(byte[] npdm)
    {
        var ok = LongshotExefsParser.TryParseProgramId(npdm, out var value, out var failure);
        return (ok, value, failure);
    }

    private static (bool Ok, string Value, string Failure) Build(byte[] header)
    {
        var ok = LongshotExefsParser.TryParseMainBuildId(header, out var value, out var failure);
        return (ok, value, failure);
    }

    private static void AssertProgramFails(byte[] npdm, string fragment)
    {
        var (ok, value, failure) = Program(npdm);
        Assert.False(ok);
        Assert.Equal(string.Empty, value);
        Assert.Contains(fragment, failure, StringComparison.Ordinal);
    }

    private static void AssertBuildFails(byte[] header, string fragment)
    {
        var (ok, value, failure) = Build(header);
        Assert.False(ok);
        Assert.Equal(string.Empty, value);
        Assert.Contains(fragment, failure, StringComparison.Ordinal);
    }

    // ---------------- NPDM ----------------

    [Fact]
    public void Real_shaped_npdm_yields_the_ocarina_of_time_program_id()
    {
        var npdm = Npdm();
        Assert.Equal(0x724, npdm.Length);                                 // same size as the real file
        Assert.Equal(0x0400486024DEC000UL, BinaryPrimitives.ReadUInt64LittleEndian(npdm.AsSpan(0x570))); // program id at 0x570

        var (ok, value, failure) = Program(npdm);
        Assert.True(ok, failure);
        Assert.Equal(OotProgramIdHex, value);
        Assert.True(LongshotIdentityFormat.IsCanonicalProgramId(value));
    }

    [Theory]
    [InlineData(0x0102030405060708UL, "0102030405060708")]   // big-endian reading would give 0807060504030201
    [InlineData(0x00000000000000FFUL, "00000000000000ff")]   // leading zeros preserved
    [InlineData(0xFF00000000000000UL, "ff00000000000000")]
    [InlineData(0x0400486024DEC000UL, OotProgramIdHex)]
    public void Program_id_is_read_little_endian_and_formatted_as_16_lowercase_hex(ulong programId, string expected)
    {
        var (ok, value, _) = Program(Npdm(programId));
        Assert.True(ok);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Invalid_meta_magic_fails()
        => AssertProgramFails(Npdm(metaMagic: "ATEM"), "does not start with the META magic");

    [Fact]
    public void File_shorter_than_the_meta_header_fails()
        => AssertProgramFails(Npdm(length: 0x7F), "shorter than the 0x80-byte META header");

    [Fact]
    public void File_larger_than_64_KiB_fails()
        => AssertProgramFails(Npdm(length: 64 * 1024 + 1), "exceeds the 65536-byte size limit");

    [Fact]
    public void File_of_exactly_64_KiB_is_accepted()
    {
        var npdm = Npdm(aci0Offset: 64 * 1024 - 0x40, aci0Size: 0x40, length: 64 * 1024);
        Assert.True(Program(npdm).Ok);
    }

    [Fact]
    public void Aci0_offset_below_0x80_fails()
        => AssertProgramFails(Npdm(aci0Offset: 0x7F, length: 0x7F + 0x1C4), "ACI0 offset 0x7f overlaps the META header");

    [Fact]
    public void Aci0_offset_of_exactly_0x80_is_accepted()
        => Assert.True(Program(Npdm(aci0Offset: 0x80, aci0Size: 0x40)).Ok);

    [Fact]
    public void Aci0_size_below_0x40_fails()
        => AssertProgramFails(Npdm(aci0Size: 0x3F, length: 0x724), "ACI0 size 0x3f is smaller than the 0x40-byte ACI0 header");

    [Fact]
    public void Aci0_size_of_exactly_0x40_is_accepted()
        => Assert.True(Program(Npdm(aci0Size: 0x40, length: 0x724)).Ok);

    [Fact]
    public void Aci0_region_one_byte_past_the_end_fails()
        => AssertProgramFails(Npdm(length: 0x723), "ACI0 region (offset 0x560, size 0x1c4) extends past the end of the file");

    [Fact]
    public void Aci0_region_ending_exactly_at_the_end_is_accepted()
    {
        var npdm = Npdm(length: 0x724);
        Assert.Equal(0x560 + 0x1C4, npdm.Length);
        Assert.True(Program(npdm).Ok);
    }

    [Theory]
    [InlineData(0xFFFFFFF0u, 0x40u)]          // offset + size overflows 32 bits
    [InlineData(0x80u, 0xFFFFFFFFu)]
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)]
    public void Overflowing_offset_and_size_fail_safely(uint offset, uint size)
        => AssertProgramFails(Npdm(aci0Offset: offset, aci0Size: size, length: 0x724), "extends past the end of the file");

    [Fact]
    public void Invalid_aci0_magic_fails()
        => AssertProgramFails(Npdm(aci0Magic: "ACID"), "ACI0 section does not start with the ACI0 magic");

    [Fact]
    public void Zero_program_id_fails()
        => AssertProgramFails(Npdm(programId: 0), "program id is zero");

    // ---------------- NSO ----------------

    [Fact]
    public void Real_shaped_nso_header_yields_the_exact_40_character_build_id()
    {
        var (ok, value, failure) = Build(NsoHeader());
        Assert.True(ok, failure);
        Assert.Equal(OotBuildIdHex, value);
        Assert.Equal(40, value.Length);
        Assert.True(LongshotIdentityFormat.IsCanonicalMainBuildId(value));
        Assert.NotEqual("e0f3ccfa", value); // never just the builder's 8-character prefix
    }

    [Fact]
    public void Build_id_keeps_leading_zero_bytes()
    {
        var id = new byte[20];
        id[19] = 0x01;
        Assert.Equal("0000000000000000000000000000000000000001", Build(NsoHeader(buildId20: id)).Value);
    }

    [Fact]
    public void Invalid_nso_magic_fails()
        => AssertBuildFails(NsoHeader(magic: "NSO1"), "does not start with the NSO0 magic");

    [Theory]
    [InlineData(1u)]
    [InlineData(0xFFFFFFFFu)]
    public void Unsupported_nso_versions_fail(uint version)
        => AssertBuildFails(NsoHeader(version: version), $"unsupported NSO version {version}");

    [Theory]
    [InlineData(0x5F)]
    [InlineData(0x60)]
    [InlineData(0xFF)]
    public void Truncated_headers_fail(int length)
        => AssertBuildFails(NsoHeader(length: length), "shorter than the 0x100-byte NSO header");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void Non_zero_padding_in_each_of_the_final_12_bytes_fails(int paddingIndex)
    {
        var padding = new byte[12];
        padding[paddingIndex] = 0x01;
        AssertBuildFails(NsoHeader(padding12: padding), "non-zero bytes after the first 20");
    }

    [Fact]
    public void All_zero_build_id_fails()
        => AssertBuildFails(NsoHeader(buildId20: new byte[20]), "build id is all zero");

    [Fact]
    public void Only_the_fixed_header_is_needed()
    {
        // Longer input (e.g. a whole module) is fine; only the header fields are read.
        var withBody = NsoHeader().Concat(new byte[1000]).ToArray();
        Assert.Equal(OotBuildIdHex, Build(withBody).Value);
    }
}

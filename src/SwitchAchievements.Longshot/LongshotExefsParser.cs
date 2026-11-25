using System;
using System.Buffers.Binary;
using System.Globalization;

namespace SwitchAchievements.Longshot;

/// <summary>
/// Pure parsers for the two identity sources in a Longshot game's
/// <c>.longshot_exefs</c> folder (M5.6.4). They work on bytes already read by
/// <see cref="LongshotGameIdentityProvider"/>, never touch the file system, never
/// trust an offset or size from the data without bounds-checking it, and return
/// either a canonical value (checked with <see cref="LongshotIdentityFormat"/>)
/// or a fixed, safe failure reason.
///
/// Layouts verified against Longshot v3.0.0's files for Ocarina of Time (all
/// integers little-endian):
/// <list type="bullet">
/// <item><c>main.npdm</c>: a 0x80-byte META header (magic <c>META</c>; ACI0 offset at
/// 0x70 and size at 0x74), and an ACI0 section (magic <c>ACI0</c>) whose program id is
/// the u64 at ACI0 + 0x10. The ACID section is deliberately not parsed: its layout
/// differs from the Switch 1 documentation and it is not needed for identity.</item>
/// <item><c>main</c> (NSO): a 0x100-byte fixed header (magic <c>NSO0</c>, version 0 at
/// 0x04) whose 32-byte build-id field at 0x40 holds a 20-byte build id followed by 12
/// zero bytes.</item>
/// </list>
/// </summary>
internal static class LongshotExefsParser
{
    // ---- main.npdm ----
    public const int MetaHeaderLength = 0x80;
    public const int MaxNpdmLength = 64 * 1024;
    public const int Aci0OffsetField = 0x70;
    public const int Aci0SizeField = 0x74;
    public const int Aci0HeaderLength = 0x40;
    public const int Aci0ProgramIdOffset = 0x10;
    private static ReadOnlySpan<byte> MetaMagic => "META"u8;
    private static ReadOnlySpan<byte> Aci0Magic => "ACI0"u8;

    // ---- main (NSO) ----
    public const int NsoHeaderLength = 0x100;
    public const int NsoVersionOffset = 0x04;
    public const uint SupportedNsoVersion = 0;
    public const int BuildIdFieldOffset = 0x40;
    public const int BuildIdFieldLength = 0x20;
    public const int BuildIdLength = 20;
    private static ReadOnlySpan<byte> NsoMagic => "NSO0"u8;

    /// <summary>
    /// Extracts the program id from a complete <c>main.npdm</c> as 16 lowercase hex
    /// characters, or returns false with a fixed failure reason.
    /// </summary>
    public static bool TryParseProgramId(ReadOnlySpan<byte> npdm, out string programId, out string failure)
    {
        programId = string.Empty;

        if (npdm.Length < MetaHeaderLength)
            return Fail($"main.npdm is shorter than the 0x{MetaHeaderLength:x}-byte META header", out failure);
        if (npdm.Length > MaxNpdmLength)
            return Fail($"main.npdm exceeds the {MaxNpdmLength}-byte size limit", out failure);
        if (!npdm[..4].SequenceEqual(MetaMagic))
            return Fail("main.npdm does not start with the META magic", out failure);

        var aci0Offset = BinaryPrimitives.ReadUInt32LittleEndian(npdm.Slice(Aci0OffsetField, 4));
        var aci0Size = BinaryPrimitives.ReadUInt32LittleEndian(npdm.Slice(Aci0SizeField, 4));

        if (aci0Offset < MetaHeaderLength)
            return Fail($"main.npdm ACI0 offset 0x{aci0Offset:x} overlaps the META header", out failure);
        if (aci0Size < Aci0HeaderLength)
            return Fail($"main.npdm ACI0 size 0x{aci0Size:x} is smaller than the 0x{Aci0HeaderLength:x}-byte ACI0 header", out failure);

        // 64-bit arithmetic: two u32 values cannot overflow a ulong.
        var aci0End = (ulong)aci0Offset + aci0Size;
        if (aci0End > (ulong)npdm.Length)
            return Fail($"main.npdm ACI0 region (offset 0x{aci0Offset:x}, size 0x{aci0Size:x}) extends past the end of the file", out failure);

        // Safe: aci0Offset + Aci0HeaderLength <= aci0End <= npdm.Length <= MaxNpdmLength.
        var aci0 = npdm.Slice((int)aci0Offset, (int)aci0Size);
        if (!aci0[..4].SequenceEqual(Aci0Magic))
            return Fail("main.npdm ACI0 section does not start with the ACI0 magic", out failure);

        var value = BinaryPrimitives.ReadUInt64LittleEndian(aci0.Slice(Aci0ProgramIdOffset, 8));
        if (value == 0)
            return Fail("main.npdm program id is zero", out failure);

        var formatted = value.ToString("x16", CultureInfo.InvariantCulture);
        if (!LongshotIdentityFormat.IsCanonicalProgramId(formatted))
            return Fail("main.npdm program id could not be formatted canonically", out failure);

        programId = formatted;
        failure = string.Empty;
        return true;
    }

    /// <summary>
    /// Extracts the main module's build id from the NSO's fixed header as 40
    /// lowercase hex characters (exactly the first 20 bytes of the 32-byte field,
    /// whose last 12 bytes must be zero), or returns false with a fixed failure reason.
    /// </summary>
    public static bool TryParseMainBuildId(ReadOnlySpan<byte> nsoHeader, out string buildId, out string failure)
    {
        buildId = string.Empty;

        if (nsoHeader.Length < NsoHeaderLength)
            return Fail($"main is shorter than the 0x{NsoHeaderLength:x}-byte NSO header", out failure);
        if (!nsoHeader[..4].SequenceEqual(NsoMagic))
            return Fail("main does not start with the NSO0 magic", out failure);

        var version = BinaryPrimitives.ReadUInt32LittleEndian(nsoHeader.Slice(NsoVersionOffset, 4));
        if (version != SupportedNsoVersion)
            return Fail($"main has unsupported NSO version {version}", out failure);

        var field = nsoHeader.Slice(BuildIdFieldOffset, BuildIdFieldLength);
        var id = field[..BuildIdLength];
        var padding = field[BuildIdLength..];

        if (padding.ContainsAnyExcept((byte)0))
            return Fail("main build-id field has non-zero bytes after the first 20", out failure);
        if (!id.ContainsAnyExcept((byte)0))
            return Fail("main build id is all zero", out failure);

        var formatted = Convert.ToHexStringLower(id);
        if (!LongshotIdentityFormat.IsCanonicalMainBuildId(formatted))
            return Fail("main build id could not be formatted canonically", out failure);

        buildId = formatted;
        failure = string.Empty;
        return true;
    }

    private static bool Fail(string reason, out string failure)
    {
        failure = reason;
        return false;
    }
}

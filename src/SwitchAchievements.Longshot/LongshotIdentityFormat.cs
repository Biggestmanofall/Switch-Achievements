using System;
using System.Collections.Generic;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>
/// Longshot's identity-fact format (M5.6.3): the fact keys the Longshot
/// integration reports and the canonical string form of each value. This is the
/// single definition of those rules; the catalog loader uses it to reject
/// non-canonical catalog values, and the later Longshot identity provider must
/// produce values that satisfy the same static checks.
///
/// <list type="bullet">
/// <item><see cref="ProgramIdKey"/>: the game's program id (from its NPDM), as
/// exactly 16 lowercase hexadecimal characters, e.g. <c>0400486024dec000</c>.</item>
/// <item><see cref="MainBuildIdKey"/>: the main executable module's build id —
/// the first 20 bytes of the NSO's 32-byte build-id field — as exactly 40
/// lowercase hexadecimal characters.</item>
/// </list>
///
/// Validation only: no file I/O, no process discovery, no NPDM/NSO parsing, and
/// values are never trimmed, lowercased, or otherwise repaired.
/// </summary>
public sealed class LongshotIdentityFormat : IIntegrationIdentityFormat
{
    public const string Integration = "longshot";
    public const string ProgramIdKey = "switch2.programId";
    public const string MainBuildIdKey = "switch2.mainBuildId";

    /// <summary>Hex characters in a canonical program id (8 bytes).</summary>
    public const int ProgramIdLength = 16;

    /// <summary>Hex characters in a canonical main build id (20 bytes).</summary>
    public const int MainBuildIdLength = 40;

    private static readonly IReadOnlyCollection<string> IdentityKeySet = Array.AsReadOnly(new[] { ProgramIdKey });
    private static readonly IReadOnlyCollection<string> BuildKeySet = Array.AsReadOnly(new[] { MainBuildIdKey });

    public string IntegrationId => Integration;

    public IReadOnlyCollection<string> IdentityKeys => IdentityKeySet;

    public IReadOnlyCollection<string> BuildKeys => BuildKeySet;

    public bool IsCanonical(string factKey, string value) => factKey switch
    {
        ProgramIdKey => IsCanonicalProgramId(value),
        MainBuildIdKey => IsCanonicalMainBuildId(value),
        _ => false,
    };

    /// <summary>True for exactly 16 lowercase hexadecimal characters.</summary>
    public static bool IsCanonicalProgramId(string? value) => IsLowercaseHex(value, ProgramIdLength);

    /// <summary>True for exactly 40 lowercase hexadecimal characters.</summary>
    public static bool IsCanonicalMainBuildId(string? value) => IsLowercaseHex(value, MainBuildIdLength);

    private static bool IsLowercaseHex(string? value, int length)
    {
        if (value is null || value.Length != length)
            return false;

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        return true;
    }
}

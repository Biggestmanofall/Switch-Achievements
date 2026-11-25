namespace SwitchAchievements.Core;

/// <summary>
/// Generic syntax of an integration identifier, e.g. <c>longshot</c> or
/// <c>other-recomp</c>. Core never interprets what an integration represents.
/// </summary>
/// <remarks>
/// Grammar: one or more groups of lowercase ASCII letters/digits separated by
/// single hyphens (<c>^[a-z0-9]+(-[a-z0-9]+)*$</c>), containing at least one
/// letter, at most <see cref="MaxLength"/> characters. No leading, trailing, or
/// consecutive hyphens.
/// </remarks>
public static class IntegrationIdentifier
{
    public const int MaxLength = 64;

    public static bool IsValid(string? value) => IsKebabToken(value, MaxLength, requireLetter: true);

    /// <summary>Shared kebab-token check (also used for catalog game ids).</summary>
    internal static bool IsKebabToken(string? value, int maxLength, bool requireLetter)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
            return false;

        var hasLetter = false;
        var previousWasHyphen = true; // rejects a leading hyphen
        foreach (var c in value)
        {
            if (c == '-')
            {
                if (previousWasHyphen)
                    return false; // leading or consecutive hyphen
                previousWasHyphen = true;
                continue;
            }

            if (c is >= 'a' and <= 'z')
                hasLetter = true;
            else if (c is not (>= '0' and <= '9'))
                return false;

            previousWasHyphen = false;
        }

        return !previousWasHyphen && (hasLetter || !requireLetter); // rejects a trailing hyphen
    }
}

/// <summary>
/// Generic syntax of an identity fact key, e.g. <c>switch2.programId</c>. Core
/// validates only the shape; the meaning of a namespace belongs to the
/// integrations and the catalog that agree on it.
/// </summary>
/// <remarks>
/// Grammar: two or more dot-separated segments; each segment starts with a
/// lowercase ASCII letter followed by ASCII letters or digits
/// (<c>^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)+$</c>); at most
/// <see cref="MaxLength"/> characters.
/// </remarks>
public static class IdentityFactKey
{
    public const int MaxLength = 64;

    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength)
            return false;

        var segments = 0;
        var atSegmentStart = true;
        foreach (var c in key)
        {
            if (c == '.')
            {
                if (atSegmentStart)
                    return false; // empty segment
                atSegmentStart = true;
                continue;
            }

            if (atSegmentStart)
            {
                if (c is not (>= 'a' and <= 'z'))
                    return false;
                segments++;
                atSegmentStart = false;
                continue;
            }

            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
                return false;
        }

        return !atSegmentStart && segments >= 2;
    }
}

/// <summary>
/// Generic syntax of an identity fact value. Core does not know or normalise any
/// integration-specific format (hexadecimal, case, length): providers and the
/// catalog loader supply canonical values, and Core compares them ordinally.
/// </summary>
/// <remarks>
/// A valid value is non-empty, at most <see cref="MaxLength"/> characters, has no
/// leading or trailing white space, and contains no control characters.
/// </remarks>
public static class IdentityFactValue
{
    public const int MaxLength = 256;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            return false;

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            return false;

        foreach (var c in value)
        {
            if (char.IsControl(c))
                return false;
        }

        return true;
    }
}

using System.Text;

namespace SwitchAchievements.Core;

/// <summary>The kind of problem a provider met while collecting identity evidence.</summary>
public enum EvidenceIssueKind
{
    /// <summary>A required source (file, field, record) was not present.</summary>
    Missing,

    /// <summary>A source exists but could not be read (access denied, I/O error, timeout).</summary>
    Unreadable,

    /// <summary>A source was read but did not have the expected structure.</summary>
    Malformed,

    /// <summary>Two sources disagreed about the same identity. Resolves to <see cref="IdentificationStatus.Conflict"/>.</summary>
    Conflict,

    /// <summary>Any other diagnostic worth reporting.</summary>
    Other,
}

/// <summary>
/// One problem reported by an <see cref="IGameIdentityProvider"/>. Messages must
/// never contain file-system paths or raw file contents (a provider obligation);
/// as a safety net, the message and fact key are sanitised here: control
/// characters become spaces, surrounding white space is trimmed, and the length
/// is capped.
/// </summary>
public sealed class EvidenceIssue
{
    public const int MaxMessageLength = 200;

    public EvidenceIssue(EvidenceIssueKind kind, string? message, string? factKey = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown evidence issue kind.");

        Kind = kind;
        Message = Sanitize(message, MaxMessageLength);
        FactKey = factKey is null ? null : Sanitize(factKey, IdentityFactKey.MaxLength);
    }

    public EvidenceIssueKind Kind { get; }

    /// <summary>The fact the issue concerns, when there is one (sanitised; not necessarily a valid key).</summary>
    public string? FactKey { get; }

    /// <summary>Sanitised, length-capped description.</summary>
    public string Message { get; }

    internal static string Sanitize(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        foreach (var c in text)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
            if (builder.Length > maxLength + 64)
                break; // enough to trim and cap
        }

        var result = builder.ToString().Trim();
        return result.Length <= maxLength ? result : result[..maxLength];
    }
}

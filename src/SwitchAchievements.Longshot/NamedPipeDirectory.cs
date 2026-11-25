using System.IO;

namespace SwitchAchievements.Longshot;

/// <summary>Answers whether a named pipe exists, without connecting to it (M5.8.1).</summary>
public interface INamedPipeDirectory
{
    /// <summary>
    /// True when a pipe named <paramref name="pipeName"/> (with or without the
    /// <c>\\.\pipe\</c> prefix) exists, false when it does not, and null when that
    /// could not be determined. Never throws.
    /// </summary>
    bool? Exists(string pipeName);
}

/// <summary>
/// Lists the Windows named-pipe namespace (<c>\\.\pipe\</c>) and compares names,
/// ignoring case as Windows does. Listing never opens a pipe, so it can neither take
/// a server's listening instance nor appear to the server as a client. (Querying a
/// pipe path's attributes, as <c>File.Exists</c> does, can open the pipe, so it is
/// deliberately not used.) Any failure to list yields null, never "absent".
/// </summary>
public sealed class WindowsNamedPipeDirectory : INamedPipeDirectory
{
    internal const string Prefix = @"\\.\pipe\";

    private readonly Func<IEnumerable<string>> _enumerate;

    public WindowsNamedPipeDirectory()
        : this(() => Directory.EnumerateFiles(Prefix))
    {
    }

    /// <summary>Test seam: supplies the pipe-namespace listing.</summary>
    internal WindowsNamedPipeDirectory(Func<IEnumerable<string>> enumerate)
        => _enumerate = enumerate ?? throw new ArgumentNullException(nameof(enumerate));

    public bool? Exists(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            return null;

        var name = WithoutPrefix(pipeName);
        try
        {
            foreach (var entry in _enumerate())
            {
                if (string.Equals(WithoutPrefix(entry), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception)
        {
            return null; // listing failed or was refused: unknown, not absent
        }
    }

    private static string WithoutPrefix(string name)
        => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? name[Prefix.Length..] : name;
}

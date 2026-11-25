using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="ICommandLineReader"/>: command lines by PID. A PID with
/// no entry is unreadable (null). A PID in <see cref="Throwing"/> throws.
/// </summary>
public sealed class FakeCommandLineReader : ICommandLineReader
{
    private readonly object _lock = new();
    private readonly Dictionary<int, string?> _commandLines = new();
    private readonly HashSet<int> _throwing = new();

    public int ReadCount { get; private set; }

    public FakeCommandLineReader Set(int pid, string? commandLine)
    {
        lock (_lock) { _commandLines[pid] = commandLine; }
        return this;
    }

    public FakeCommandLineReader Throw(int pid)
    {
        lock (_lock) { _throwing.Add(pid); }
        return this;
    }

    public string? ReadCommandLine(GameProcessInfo process)
    {
        lock (_lock)
        {
            ReadCount++;
            if (_throwing.Contains(process.ProcessId))
                throw new InvalidOperationException("simulated reader failure");
            return _commandLines.TryGetValue(process.ProcessId, out var line) ? line : null;
        }
    }
}

/// <summary>Builds realistic Longshot command lines (quoted exactly as Longshot's hos_crash_quote_arg writes them).</summary>
internal static class LongshotCommandLines
{
    public const string Exe = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    public const string GameFolder = @"C:\Recomp Library\longshot\Longshot";
    public const string Log = @"C:\Recomp Library\longshot\Longshot\logs\longshot_log.txt";
    public const string RunId = "dc92dc01bb0e52897df861005ee6682a";

    public static string Game() => $"\"{Exe}\"";

    public static string CrashWatch(int gamePid, string? options = "-")
        => $"\"{Exe}\" \"--crash-watch\" \"{gamePid}\" \"{RunId}\" \"{GameFolder}\" \"{Log}\""
           + (options is null ? "" : $" \"{options}\"");

    public static string CrashShow(string record = "crash-20261121T201400Z.txt", string? options = "-")
        => $"\"{Exe}\" \"--crash-show\" \"{record}\" \"{GameFolder}\" \"{Log}\""
           + (options is null ? "" : $" \"{options}\"");
}

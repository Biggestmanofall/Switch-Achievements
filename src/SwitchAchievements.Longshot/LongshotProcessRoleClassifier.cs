using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>The role of a <c>bin\longshot.exe</c> process, decided from its own command line.</summary>
public enum LongshotProcessRole
{
    /// <summary>The command line was read and is not a crash-reporter helper's: a game candidate.</summary>
    Game,

    /// <summary><c>longshot --crash-watch &lt;game pid&gt; &lt;run id&gt; &lt;game folder&gt; &lt;log&gt; [&lt;options&gt;]</c>.</summary>
    CrashWatchHelper,

    /// <summary><c>longshot --crash-show &lt;record&gt; &lt;game folder&gt; &lt;log&gt; [&lt;options&gt;]</c>.</summary>
    CrashShowHelper,

    /// <summary>
    /// The command line could not be read, or it names a helper mode with
    /// incomplete or malformed arguments. Retained as a candidate (fails toward ambiguity).
    /// </summary>
    Unknown,
}

/// <summary>
/// Whether a crash-watch helper's referenced game PID corresponds to an earlier-
/// started candidate. Diagnostic only: it never decides exclusion or selection.
/// </summary>
public enum HelperRelationship
{
    /// <summary>Not a crash-watch helper.</summary>
    NotApplicable,

    /// <summary>The referenced PID is a non-helper candidate that started before the helper.</summary>
    Confirmed,

    /// <summary>The referenced PID is not among the candidates (the game has exited).</summary>
    Orphaned,

    /// <summary>The referenced PID is a candidate, but not provably the helper's game (started later, or start times unknown).</summary>
    Unconfirmed,
}

/// <summary>
/// The classification of one process. Deliberately carries no command-line text.
/// </summary>
public sealed record LongshotProcessClassification(
    int ProcessId,
    LongshotProcessRole Role,
    HelperRelationship Relationship = HelperRelationship.NotApplicable,
    ulong? ReferencedProcessId = null)
{
    /// <summary>True only for a positively identified crash-reporter helper.</summary>
    public bool IsHelper => Role is LongshotProcessRole.CrashWatchHelper or LongshotProcessRole.CrashShowHelper;
}

/// <summary>Result of <see cref="LongshotProcessRoleClassifier.SelectCandidates"/>.</summary>
public sealed record LongshotCandidateSelection(
    IReadOnlyList<GameProcessInfo> Candidates,
    IReadOnlyList<LongshotProcessClassification> Classifications,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Recognises Longshot's crash-reporter helpers by their own command-line
/// arguments and removes them from the game candidates (the approved R1 "A′"
/// approach; see docs/m3-process-selection.md, "Addendum — crash-reporter helpers").
///
/// Longshot's game process starts helper copies of the same executable
/// (host/hos_crash_dialog.c). Their <c>main()</c> returns before any game or
/// control-bridge code runs, so a helper can never be the game. The recognition
/// rules below mirror Longshot's own argument validation (<c>hos_crash_args</c>,
/// <c>hos_crash_hex_id</c>, <c>hos_crash_record_name</c>) exactly; anything that
/// does not match exactly is <see cref="LongshotProcessRole.Unknown"/> (when it
/// names a helper mode) or <see cref="LongshotProcessRole.Game"/>, and in both
/// cases the process is retained.
///
/// Safety properties:
/// <list type="bullet">
/// <item>Only positively identified helpers are excluded. Unreadable command
/// lines are retained, so the selection policy fails toward ambiguity.</item>
/// <item>Parent PID and start time never cause exclusion or selection; they only
/// feed the diagnostic <see cref="HelperRelationship"/>.</item>
/// <item>The remaining candidates go to <see cref="ProcessSelector"/> unchanged,
/// so two genuine games stay ambiguous.</item>
/// <item>Command lines are parsed and discarded; diagnostics contain only PIDs,
/// roles and relationships.</item>
/// </list>
/// </summary>
public sealed class LongshotProcessRoleClassifier
{
    public const string CrashWatchArgument = "--crash-watch";
    public const string CrashShowArgument = "--crash-show";

    /// <summary>Upper bound on cached classifications (R1.1).</summary>
    internal const int CacheCapacity = 64;

    private readonly ICommandLineReader _reader;
    private readonly Func<string?, (LongshotProcessRole Role, ulong? Referenced)> _parse;

    // R1.1 cache: positively identified roles only, keyed by process instance
    // (PID + start time). A process's command line cannot change, so a role read
    // for one instance stays valid for that instance. Holds no command-line text.
    private readonly object _cacheLock = new();
    private readonly Dictionary<ProcessFingerprint, (LongshotProcessRole Role, ulong? Referenced)> _cache = new();

    public LongshotProcessRoleClassifier(ICommandLineReader reader)
        : this(reader, ParseRole)
    {
    }

    /// <summary>Test seam: <paramref name="parse"/> replaces <see cref="ParseRole"/>.</summary>
    internal LongshotProcessRoleClassifier(
        ICommandLineReader reader,
        Func<string?, (LongshotProcessRole Role, ulong? Referenced)> parse)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _parse = parse ?? throw new ArgumentNullException(nameof(parse));
    }

    /// <summary>Number of cached classifications (tests/diagnostics).</summary>
    internal int CachedCount
    {
        get { lock (_cacheLock) return _cache.Count; }
    }

    /// <summary>
    /// Classifies already path-filtered game-binary processes and returns those
    /// that remain game candidates (everything except positively identified
    /// helpers), in their original order. Command lines are read only when there
    /// is at least one process: a lone process can be an orphaned helper.
    /// </summary>
    public LongshotCandidateSelection SelectCandidates(IReadOnlyList<GameProcessInfo> gameBinaryProcesses)
    {
        if (gameBinaryProcesses.Count == 0)
        {
            lock (_cacheLock)
                _cache.Clear(); // every cached instance has gone

            return new LongshotCandidateSelection(
                Array.Empty<GameProcessInfo>(), Array.Empty<LongshotProcessClassification>(), Array.Empty<string>());
        }

        var classifications = Classify(gameBinaryProcesses);
        var candidates = new List<GameProcessInfo>(gameBinaryProcesses.Count);
        var diagnostics = new List<string>(gameBinaryProcesses.Count);

        for (var i = 0; i < gameBinaryProcesses.Count; i++)
        {
            var classification = classifications[i];
            if (!classification.IsHelper)
                candidates.Add(gameBinaryProcesses[i]);
            diagnostics.Add(Describe(classification));
        }

        return new LongshotCandidateSelection(candidates, classifications, diagnostics);
    }

    /// <summary>
    /// Classifies each process (same order as the input) and, for crash-watch
    /// helpers, records the diagnostic relationship to the referenced game PID.
    /// The input is treated as the complete current process list: cached roles of
    /// process instances absent from it are discarded.
    /// </summary>
    public IReadOnlyList<LongshotProcessClassification> Classify(IReadOnlyList<GameProcessInfo> processes)
    {
        var parsed = new (LongshotProcessRole Role, ulong? Referenced)[processes.Count];
        lock (_cacheLock)
        {
            PruneCache(processes);
            for (var i = 0; i < processes.Count; i++)
                parsed[i] = GetRole(processes[i]);
        }

        var result = new LongshotProcessClassification[processes.Count];
        for (var i = 0; i < processes.Count; i++)
        {
            var (role, referenced) = parsed[i];
            var relationship = role == LongshotProcessRole.CrashWatchHelper
                ? Relate(processes[i], referenced!.Value, processes, parsed)
                : HelperRelationship.NotApplicable;
            result[i] = new LongshotProcessClassification(processes[i].ProcessId, role, relationship, referenced);
        }

        return result;
    }

    // Caller holds _cacheLock.
    private void PruneCache(IReadOnlyList<GameProcessInfo> processes)
    {
        if (_cache.Count == 0)
            return;

        var live = new HashSet<ProcessFingerprint>();
        foreach (var p in processes)
            live.Add(new ProcessFingerprint(p.ProcessId, p.ProcessStartUtc));

        List<ProcessFingerprint>? stale = null;
        foreach (var key in _cache.Keys)
        {
            if (!live.Contains(key))
                (stale ??= new List<ProcessFingerprint>()).Add(key);
        }

        if (stale is not null)
        {
            foreach (var key in stale)
                _cache.Remove(key);
        }
    }

    // Caller holds _cacheLock.
    private (LongshotProcessRole Role, ulong? Referenced) GetRole(GameProcessInfo process)
    {
        // Without a full identity (PID + start time) a cached role could belong to
        // a different instance, so such processes are never cached or served from cache.
        var identifiable = process.ProcessId > 0 && process.ProcessStartUtc != default;
        var key = new ProcessFingerprint(process.ProcessId, process.ProcessStartUtc);

        if (identifiable && _cache.TryGetValue(key, out var cached))
            return cached;

        var role = ReadRole(process);

        // Only positive identifications are cached; Unknown is retried next time.
        if (identifiable && role.Role != LongshotProcessRole.Unknown && _cache.Count < CacheCapacity)
            _cache[key] = role;

        return role;
    }

    private (LongshotProcessRole Role, ulong? Referenced) ReadRole(GameProcessInfo process)
    {
        string? commandLine;
        try
        {
            commandLine = _reader.ReadCommandLine(process);
        }
        catch
        {
            // A reader failure must never exclude a process.
            return (LongshotProcessRole.Unknown, null);
        }

        (LongshotProcessRole Role, ulong? Referenced) parsed;
        try
        {
            parsed = _parse(commandLine);
        }
        catch
        {
            // Defensive (R1.1): an unexpected parser failure is "unrecognised".
            // The exception is deliberately not logged or kept: its message could
            // contain command-line text.
            return (LongshotProcessRole.Unknown, null);
        }

        // A crash-watch result must carry the referenced game PID; anything
        // inconsistent is treated as unrecognised rather than trusted.
        if (parsed.Role == LongshotProcessRole.CrashWatchHelper && parsed.Referenced is null)
            return (LongshotProcessRole.Unknown, null);

        // The command line goes out of scope here; only the role survives.
        return parsed;
    }

    private static HelperRelationship Relate(
        GameProcessInfo helper,
        ulong referencedPid,
        IReadOnlyList<GameProcessInfo> processes,
        (LongshotProcessRole Role, ulong? Referenced)[] parsed)
    {
        var index = -1;
        for (var i = 0; i < processes.Count; i++)
        {
            if (processes[i].ProcessId > 0 && (ulong)processes[i].ProcessId == referencedPid)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return HelperRelationship.Orphaned;

        var game = processes[index];
        var gameIsHelper = parsed[index].Role is LongshotProcessRole.CrashWatchHelper or LongshotProcessRole.CrashShowHelper;
        var timesKnown = game.ProcessStartUtc != default && helper.ProcessStartUtc != default;

        return !gameIsHelper && timesKnown && game.ProcessStartUtc < helper.ProcessStartUtc
            ? HelperRelationship.Confirmed
            : HelperRelationship.Unconfirmed;
    }

    private static string Describe(LongshotProcessClassification c)
    {
        var pid = c.ProcessId.ToString(CultureInfo.InvariantCulture);
        return c.Role switch
        {
            LongshotProcessRole.CrashWatchHelper =>
                $"pid={pid} role=crash-watch helper (references pid={c.ReferencedProcessId?.ToString(CultureInfo.InvariantCulture)}, {RelationshipText(c.Relationship)}); excluded",
            LongshotProcessRole.CrashShowHelper => $"pid={pid} role=crash-show helper; excluded",
            LongshotProcessRole.Game => $"pid={pid} role=game; retained",
            _ => $"pid={pid} role=unknown (command line unreadable or helper arguments not recognised); retained",
        };
    }

    private static string RelationshipText(HelperRelationship r) => r switch
    {
        HelperRelationship.Confirmed => "game confirmed",
        HelperRelationship.Orphaned => "orphaned: referenced game not running",
        HelperRelationship.Unconfirmed => "relationship unconfirmed",
        _ => "n/a",
    };

    // ------------------------------------------------------------------
    // Pure parsing (no I/O). Internal so tests can exercise it directly.
    // ------------------------------------------------------------------

    /// <summary>
    /// Decides a role from a raw command line. Null or empty means unreadable
    /// (<see cref="LongshotProcessRole.Unknown"/>). The referenced game PID is
    /// returned only for a valid crash-watch helper.
    /// </summary>
    internal static (LongshotProcessRole Role, ulong? Referenced) ParseRole(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
            return (LongshotProcessRole.Unknown, null);

        var argv = SplitCommandLine(commandLine);
        if (argv.Count < 2)
            return (LongshotProcessRole.Game, null); // no arguments: the game itself

        // Exact, case-sensitive match, as Longshot's strcmp.
        if (argv[1] == CrashWatchArgument)
        {
            return TryParseCrashWatch(argv, out var pid)
                ? (LongshotProcessRole.CrashWatchHelper, pid)
                : (LongshotProcessRole.Unknown, null);
        }

        if (argv[1] == CrashShowArgument)
        {
            return IsValidCrashShow(argv)
                ? (LongshotProcessRole.CrashShowHelper, null)
                : (LongshotProcessRole.Unknown, null);
        }

        return (LongshotProcessRole.Game, null);
    }

    // hos_crash_args, watch: argc 6 or 7; argv[2] 1..20 ASCII digits, non-zero;
    // argv[3] a 32-digit lowercase hex run id; argv[4] dir and argv[5] log
    // non-empty; optional argv[6] (options) non-empty.
    private static bool TryParseCrashWatch(IReadOnlyList<string> argv, out ulong pid)
    {
        pid = 0;
        if (argv.Count is not (6 or 7))
            return false;

        var pidText = argv[2];
        if (pidText.Length is < 1 or > 20 || !pidText.All(char.IsAsciiDigit))
            return false;
        if (!ulong.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid == 0)
            return false;

        if (!IsHexRunId(argv[3]))
            return false;

        return argv[4].Length > 0 && argv[5].Length > 0 && (argv.Count == 6 || argv[6].Length > 0);
    }

    // hos_crash_args, show: argc 5 or 6; argv[2] a bare record name; argv[3]
    // dir and argv[4] log non-empty; optional argv[5] (options) non-empty.
    private static bool IsValidCrashShow(IReadOnlyList<string> argv)
    {
        if (argv.Count is not (5 or 6))
            return false;

        return IsRecordName(argv[2])
            && argv[3].Length > 0 && argv[4].Length > 0
            && (argv.Count == 5 || argv[5].Length > 0);
    }

    // hos_crash_hex_id: exactly 32 of [0-9a-f].
    private static bool IsHexRunId(string s)
        => s.Length == 32 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    // hos_crash_record_name: (crash-|unclean-)YYYYMMDDTHHMMSSZ[-<digits>-<digits>].txt
    private static bool IsRecordName(string name)
    {
        int p;
        if (name.StartsWith("crash-", StringComparison.Ordinal)) p = 6;
        else if (name.StartsWith("unclean-", StringComparison.Ordinal)) p = 8;
        else return false;

        if (name.Length < p + 16)
            return false;

        for (var i = 0; i < 16; i++)
        {
            var c = name[p + i];
            var ok = i == 8 ? c == 'T' : i == 15 ? c == 'Z' : char.IsAsciiDigit(c);
            if (!ok)
                return false;
        }

        var s = p + 16;
        if (s < name.Length && name[s] == '-')
        {
            s++;
            if (!SkipDigits(name, ref s))
                return false;
            if (s >= name.Length || name[s] != '-')
                return false;
            s++;
            if (!SkipDigits(name, ref s))
                return false;
        }

        return string.Equals(name[s..], ".txt", StringComparison.Ordinal);
    }

    private static bool SkipDigits(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
            i++;
        return i > start;
    }

    /// <summary>
    /// Splits a Windows command line the way <c>CommandLineToArgvW</c> does
    /// (the rules Longshot's <c>hos_crash_quote_arg</c> writes for): argv[0]
    /// runs to the next quote when quoted, else to whitespace; later arguments
    /// honour quotes, 2n backslashes + quote → n backslashes and a quote toggle,
    /// 2n+1 backslashes + quote → n backslashes and a literal quote, and "" in a
    /// quoted span → a literal quote.
    /// </summary>
    internal static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var i = 0;
        var n = commandLine.Length;

        // argv[0]
        var first = new StringBuilder();
        if (i < n && commandLine[i] == '"')
        {
            i++;
            while (i < n && commandLine[i] != '"')
                first.Append(commandLine[i++]);
            if (i < n) i++; // closing quote
        }
        else
        {
            while (i < n && commandLine[i] is not (' ' or '\t'))
                first.Append(commandLine[i++]);
        }
        args.Add(first.ToString());

        while (true)
        {
            while (i < n && commandLine[i] is ' ' or '\t')
                i++;
            if (i >= n)
                break;

            var arg = new StringBuilder();
            var quoted = false;
            while (i < n)
            {
                var c = commandLine[i];
                if (!quoted && c is ' ' or '\t')
                    break;

                if (c == '\\')
                {
                    var slashes = 0;
                    while (i < n && commandLine[i] == '\\') { slashes++; i++; }
                    if (i < n && commandLine[i] == '"')
                    {
                        arg.Append('\\', slashes / 2);
                        if (slashes % 2 == 1)
                        {
                            arg.Append('"');
                            i++;
                        }
                        // even: the quote is handled by the next iteration
                    }
                    else
                    {
                        arg.Append('\\', slashes);
                    }
                    continue;
                }

                if (c == '"')
                {
                    if (quoted && i + 1 < n && commandLine[i + 1] == '"')
                    {
                        arg.Append('"');
                        i += 2;
                        continue;
                    }
                    quoted = !quoted;
                    i++;
                    continue;
                }

                arg.Append(c);
                i++;
            }

            args.Add(arg.ToString());
        }

        return args;
    }
}

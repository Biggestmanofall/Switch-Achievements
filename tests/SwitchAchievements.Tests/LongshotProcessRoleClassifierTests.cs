using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Fakes;
using static SwitchAchievements.Tests.Fakes.LongshotCommandLines;

namespace SwitchAchievements.Tests;

/// <summary>
/// M5.6 R1: crash-reporter helper classification (synthetic; no game required).
/// </summary>
public class LongshotProcessRoleClassifierTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static GameProcessInfo Proc(int pid, DateTime start) => new(pid, Exe, start);

    private static LongshotProcessRole Role(string? commandLine)
        => LongshotProcessRoleClassifier.ParseRole(commandLine).Role;

    // ---------------- parsing / roles ----------------

    [Fact]
    public void Crash_watch_with_valid_arguments_is_a_crash_watch_helper()
    {
        var (role, referenced) = LongshotProcessRoleClassifier.ParseRole(CrashWatch(31480));
        Assert.Equal(LongshotProcessRole.CrashWatchHelper, role);
        Assert.Equal(31480UL, referenced);
    }

    [Fact]
    public void Crash_watch_without_the_optional_options_argument_is_a_helper()
        => Assert.Equal(LongshotProcessRole.CrashWatchHelper, Role(CrashWatch(31480, options: null)));

    [Fact]
    public void Crash_watch_with_unquoted_arguments_is_a_helper()
        => Assert.Equal(LongshotProcessRole.CrashWatchHelper,
            Role($"\"{Exe}\" --crash-watch 31480 {RunId} C:\\Game C:\\Game\\log.txt -"));

    [Theory]
    [InlineData("crash-20261121T201400Z.txt")]
    [InlineData("unclean-20261121T201400Z.txt")]
    [InlineData("crash-20261121T201400Z-31480-2.txt")]
    public void Crash_show_with_valid_arguments_is_a_crash_show_helper(string record)
    {
        var (role, referenced) = LongshotProcessRoleClassifier.ParseRole(CrashShow(record));
        Assert.Equal(LongshotProcessRole.CrashShowHelper, role);
        Assert.Null(referenced);
    }

    [Fact]
    public void Crash_show_without_the_optional_options_argument_is_a_helper()
        => Assert.Equal(LongshotProcessRole.CrashShowHelper, Role(CrashShow(options: null)));

    [Fact]
    public void No_arguments_is_the_game()
    {
        Assert.Equal(LongshotProcessRole.Game, Role(Game()));
        Assert.Equal(LongshotProcessRole.Game, Role(Exe)); // unquoted program, no args
    }

    [Fact]
    public void Prepare_shaders_is_retained_as_a_game_candidate()
        => Assert.Equal(LongshotProcessRole.Game, Role($"\"{Exe}\" --prepare-shaders"));

    [Theory]
    [InlineData("--foo bar")]
    [InlineData("--crash-watcher 1 2")]          // partial / near match
    [InlineData("--CRASH-WATCH 1")]              // case differs (Longshot uses strcmp)
    [InlineData("-crash-watch")]
    [InlineData("x --crash-watch 31480")]        // helper flag not in argv[1]
    public void Unrelated_or_near_miss_arguments_are_the_game(string args)
        => Assert.Equal(LongshotProcessRole.Game, Role($"\"{Exe}\" {args}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_command_line_is_unknown(string? commandLine)
        => Assert.Equal(LongshotProcessRole.Unknown, Role(commandLine));

    [Theory]
    [InlineData("--crash-watch")]                                                   // nothing else
    [InlineData("--crash-watch 31480")]                                             // incomplete
    [InlineData("--crash-watch 31480 " + RunId)]                                    // incomplete
    [InlineData("--crash-watch 31480 " + RunId + " C:\\G")]                         // incomplete (argc 5)
    [InlineData("--crash-watch 31480 " + RunId + " C:\\G C:\\L - extra")]           // too many (argc 8)
    public void Incomplete_crash_watch_arguments_are_unknown_not_helper(string args)
        => Assert.Equal(LongshotProcessRole.Unknown, Role($"\"{Exe}\" {args}"));

    [Theory]
    [InlineData("--crash-watch abc " + RunId + " C:\\G C:\\L")]                                  // pid not digits
    [InlineData("--crash-watch 0 " + RunId + " C:\\G C:\\L")]                                    // pid zero
    [InlineData("--crash-watch -5 " + RunId + " C:\\G C:\\L")]                                   // signed
    [InlineData("--crash-watch 123456789012345678901 " + RunId + " C:\\G C:\\L")]               // 21 digits
    [InlineData("--crash-watch 31480 DC92DC01BB0E52897DF861005EE6682A C:\\G C:\\L")]             // uppercase hex
    [InlineData("--crash-watch 31480 dc92dc01 C:\\G C:\\L")]                                     // short run id
    [InlineData("--crash-watch 31480 " + RunId + " \"\" C:\\L")]                                 // empty dir
    [InlineData("--crash-watch 31480 " + RunId + " C:\\G \"\"")]                                 // empty log
    [InlineData("--crash-watch 31480 " + RunId + " C:\\G C:\\L \"\"")]                           // empty options
    [InlineData("--crash-show C:\\path\\crash-20261121T201400Z.txt C:\\G C:\\L")]               // a path, not a bare name
    [InlineData("--crash-show crash-2026112XT201400Z.txt C:\\G C:\\L")]                         // bad timestamp
    [InlineData("--crash-show crash-20261121T201400Z.log C:\\G C:\\L")]                         // wrong extension
    [InlineData("--crash-show crash-20261121T201400Z-1.txt C:\\G C:\\L")]                       // half suffix
    [InlineData("--crash-show other-20261121T201400Z.txt C:\\G C:\\L")]                         // wrong prefix
    [InlineData("--crash-show crash-20261121T201400Z.txt C:\\G")]                               // incomplete
    [InlineData("--crash-show crash-20261121T201400Z.txt \"\" C:\\L")]                          // empty dir
    public void Malformed_helper_arguments_are_unknown_not_helper(string args)
        => Assert.Equal(LongshotProcessRole.Unknown, Role($"\"{Exe}\" {args}"));

    [Fact]
    public void Splitter_follows_CommandLineToArgvW_quoting_rules()
    {
        var argv = LongshotProcessRoleClassifier.SplitCommandLine(
            "\"C:\\a b\\x.exe\" \"one two\" three \"a\\\"b\" \"c:\\dir\\\\\" d\\e \"\"");
        Assert.Equal(new[] { @"C:\a b\x.exe", "one two", "three", "a\"b", @"c:\dir\", @"d\e", "" }, argv);
    }

    // ---------------- reader failures ----------------

    [Fact]
    public void Reader_exception_is_unknown_and_the_process_is_retained()
    {
        var reader = new FakeCommandLineReader().Throw(100);
        var classifier = new LongshotProcessRoleClassifier(reader);

        var selection = classifier.SelectCandidates(new[] { Proc(100, T0) });

        Assert.Equal(LongshotProcessRole.Unknown, Assert.Single(selection.Classifications).Role);
        Assert.Equal(100, Assert.Single(selection.Candidates).ProcessId);
    }

    [Fact]
    public void Unreadable_process_is_retained()
    {
        var classifier = new LongshotProcessRoleClassifier(new FakeCommandLineReader()); // reads null
        var selection = classifier.SelectCandidates(new[] { Proc(100, T0) });
        Assert.Equal(100, Assert.Single(selection.Candidates).ProcessId);
    }

    [Fact]
    public void Malformed_helper_process_is_retained()
    {
        var reader = new FakeCommandLineReader().Set(200, $"\"{Exe}\" --crash-watch 100");
        var classifier = new LongshotProcessRoleClassifier(reader);
        var selection = classifier.SelectCandidates(new[] { Proc(200, T0) });
        Assert.Equal(200, Assert.Single(selection.Candidates).ProcessId);
    }

    [Fact]
    public void No_processes_reads_no_command_lines()
    {
        var reader = new FakeCommandLineReader();
        var selection = new LongshotProcessRoleClassifier(reader).SelectCandidates(Array.Empty<GameProcessInfo>());
        Assert.Empty(selection.Candidates);
        Assert.Equal(0, reader.ReadCount);
    }

    // ---------------- helper relationship ----------------

    [Fact]
    public void Helper_referencing_an_earlier_started_game_is_confirmed_and_excluded()
    {
        var reader = new FakeCommandLineReader().Set(100, Game()).Set(200, CrashWatch(100));
        var selection = new LongshotProcessRoleClassifier(reader)
            .SelectCandidates(new[] { Proc(100, T0), Proc(200, T0.AddMilliseconds(30)) });

        var helper = selection.Classifications.Single(c => c.ProcessId == 200);
        Assert.Equal(LongshotProcessRole.CrashWatchHelper, helper.Role);
        Assert.Equal(HelperRelationship.Confirmed, helper.Relationship);
        Assert.Equal(100UL, helper.ReferencedProcessId);
        Assert.Equal(100, Assert.Single(selection.Candidates).ProcessId);
    }

    [Fact]
    public void Orphaned_helper_is_still_excluded()
    {
        var reader = new FakeCommandLineReader().Set(200, CrashWatch(100)); // pid 100 no longer running
        var selection = new LongshotProcessRoleClassifier(reader)
            .SelectCandidates(new[] { Proc(200, T0) });

        Assert.Equal(HelperRelationship.Orphaned, Assert.Single(selection.Classifications).Relationship);
        Assert.Empty(selection.Candidates);
    }

    [Fact]
    public void Referenced_pid_of_a_later_started_process_is_unconfirmed_but_helper_still_excluded()
    {
        // PID reuse: the helper's referenced PID now belongs to a process that started after it.
        var reader = new FakeCommandLineReader().Set(100, Game()).Set(200, CrashWatch(100));
        var selection = new LongshotProcessRoleClassifier(reader)
            .SelectCandidates(new[] { Proc(100, T0.AddSeconds(10)), Proc(200, T0) });

        var helper = selection.Classifications.Single(c => c.ProcessId == 200);
        Assert.Equal(HelperRelationship.Unconfirmed, helper.Relationship);
        Assert.Equal(100, Assert.Single(selection.Candidates).ProcessId);
    }

    [Fact]
    public void Relationship_never_changes_which_processes_are_candidates()
    {
        // Same helper role in all three relationship states → same exclusion; the
        // referenced game is never "selected" by the relationship either.
        var reader = new FakeCommandLineReader().Set(100, Game()).Set(300, Game()).Set(200, CrashWatch(100));
        var selection = new LongshotProcessRoleClassifier(reader).SelectCandidates(
            new[] { Proc(100, T0), Proc(200, T0.AddMilliseconds(30)), Proc(300, T0.AddSeconds(1)) });

        Assert.Equal(new[] { 100, 300 }, selection.Candidates.Select(p => p.ProcessId));
    }

    // ---------------- PoC selector (shared classification) ----------------

    [Fact]
    public void Poc_selector_with_classifier_selects_the_game_and_excludes_the_helper()
    {
        var reader = new FakeCommandLineReader().Set(100, Game()).Set(200, CrashWatch(100));
        var processes = new[] { Proc(100, T0), Proc(200, T0.AddMilliseconds(30)) };

        var result = LongshotProcessSelector.Select(processes, bridge: null, verifiedOwnership: null,
            classifier: new LongshotProcessRoleClassifier(reader));

        Assert.Equal(ProcessSelectionStatus.Unique, result.Selection);
        Assert.Equal(100, result.SelectedProcess!.ProcessId);
        Assert.False(result.OwnerVerified);
    }

    [Fact]
    public void Poc_selector_without_classifier_is_unchanged_and_ambiguous()
    {
        var processes = new[] { Proc(100, T0), Proc(200, T0.AddMilliseconds(30)) };
        var result = LongshotProcessSelector.Select(processes, bridge: null);
        Assert.Equal(ProcessSelectionStatus.Ambiguous, result.Selection);
    }

    [Fact]
    public void Poc_selector_still_excludes_the_launcher_stub()
    {
        var reader = new FakeCommandLineReader().Set(100, Game()).Set(50, $"\"{GameFolder}\\longshot.exe\"");
        var processes = new[]
        {
            new GameProcessInfo(50, GameFolder + @"\longshot.exe", T0),
            Proc(100, T0.AddMilliseconds(5)),
        };

        var result = LongshotProcessSelector.Select(processes, bridge: null, verifiedOwnership: null,
            classifier: new LongshotProcessRoleClassifier(reader));

        Assert.Equal(ProcessSelectionStatus.Unique, result.Selection);
        Assert.Equal(100, result.SelectedProcess!.ProcessId);
    }

    [Fact]
    public void Experimental_server_pid_still_does_not_resolve_two_genuine_games()
    {
        var reader = new FakeCommandLineReader().Set(100, Game()).Set(300, Game());
        var bridge = new BridgeProbeResult(true, true, ExperimentalServerProcessId: 100, Error: null);
        var result = LongshotProcessSelector.Select(new[] { Proc(100, T0), Proc(300, T0.AddSeconds(1)) },
            bridge, verifiedOwnership: null, classifier: new LongshotProcessRoleClassifier(reader));

        Assert.Equal(ProcessSelectionStatus.Ambiguous, result.Selection);
        Assert.Null(result.SelectedProcess);
    }
}

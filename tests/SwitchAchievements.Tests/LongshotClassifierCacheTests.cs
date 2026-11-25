using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Fakes;
using static SwitchAchievements.Tests.Fakes.LongshotCommandLines;

namespace SwitchAchievements.Tests;

/// <summary>M5.6 R1.1: classification cache and defensive parsing.</summary>
public class LongshotClassifierCacheTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static GameProcessInfo Proc(int pid, DateTime start) => new(pid, Exe, start);

    private sealed class CountingReader : ICommandLineReader
    {
        private readonly Func<GameProcessInfo, string?> _read;
        public CountingReader(Func<GameProcessInfo, string?> read) => _read = read;
        public Dictionary<int, int> Reads { get; } = new();

        public string? ReadCommandLine(GameProcessInfo process)
        {
            Reads[process.ProcessId] = Reads.GetValueOrDefault(process.ProcessId) + 1;
            return _read(process);
        }
    }

    [Fact]
    public void Same_pid_and_start_time_is_read_once_and_reused()
    {
        var reader = new FakeCommandLineReader().Set(100, Game()).Set(200, CrashWatch(100));
        var classifier = new LongshotProcessRoleClassifier(reader);
        var processes = new[] { Proc(100, T0), Proc(200, T0.AddMilliseconds(30)) };

        for (var i = 0; i < 4; i++)
        {
            var selection = classifier.SelectCandidates(processes);
            Assert.Equal(100, Assert.Single(selection.Candidates).ProcessId);
        }

        Assert.Equal(2, reader.ReadCount); // one read per instance, then cache hits
        Assert.Equal(2, classifier.CachedCount);
    }

    [Fact]
    public void Same_pid_with_a_different_start_time_is_reread_not_served_from_cache()
    {
        // PID 100 was a helper; the PID is reused by a new (game) instance.
        var reader = new CountingReader(p => p.ProcessStartUtc == T0 ? CrashWatch(999) : Game());
        var classifier = new LongshotProcessRoleClassifier(reader);

        var first = classifier.SelectCandidates(new[] { Proc(100, T0) });
        Assert.Empty(first.Candidates); // helper excluded

        var second = classifier.SelectCandidates(new[] { Proc(100, T0.AddMinutes(10)) });
        Assert.Equal(100, Assert.Single(second.Candidates).ProcessId); // new instance re-read: a game
        Assert.Equal(2, reader.Reads[100]);
        Assert.Equal(1, classifier.CachedCount); // the old instance's entry is gone
    }

    [Fact]
    public void Terminated_instances_are_removed_from_the_cache()
    {
        var reader = new FakeCommandLineReader().Set(100, Game()).Set(200, CrashWatch(100)).Set(300, Game());
        var classifier = new LongshotProcessRoleClassifier(reader);

        classifier.SelectCandidates(new[] { Proc(100, T0), Proc(200, T0.AddMilliseconds(30)) });
        Assert.Equal(2, classifier.CachedCount);

        classifier.SelectCandidates(new[] { Proc(200, T0.AddMilliseconds(30)) }); // game exited
        Assert.Equal(1, classifier.CachedCount);

        classifier.SelectCandidates(Array.Empty<GameProcessInfo>()); // everything exited
        Assert.Equal(0, classifier.CachedCount);

        classifier.SelectCandidates(new[] { Proc(300, T0.AddMinutes(1)) });
        Assert.Equal(1, classifier.CachedCount);
    }

    [Fact]
    public void Unknown_results_are_not_cached_and_are_retried()
    {
        string? line = null; // unreadable at first
        var reader = new CountingReader(_ => line);
        var classifier = new LongshotProcessRoleClassifier(reader);
        var processes = new[] { Proc(100, T0) };

        var first = classifier.Classify(processes);
        Assert.Equal(LongshotProcessRole.Unknown, Assert.Single(first).Role);
        Assert.Equal(0, classifier.CachedCount);

        line = CrashWatch(42); // becomes readable
        var second = classifier.Classify(processes);
        Assert.Equal(LongshotProcessRole.CrashWatchHelper, Assert.Single(second).Role);
        Assert.Equal(2, reader.Reads[100]);
        Assert.Equal(1, classifier.CachedCount);
    }

    [Fact]
    public void Process_without_a_known_start_time_is_never_cached()
    {
        var reader = new FakeCommandLineReader().Set(100, Game());
        var classifier = new LongshotProcessRoleClassifier(reader);
        var processes = new[] { new GameProcessInfo(100, Exe, default) };

        classifier.SelectCandidates(processes);
        classifier.SelectCandidates(processes);

        Assert.Equal(2, reader.ReadCount);
        Assert.Equal(0, classifier.CachedCount);
    }

    [Fact]
    public void Cache_size_is_bounded()
    {
        var reader = new CountingReader(_ => Game());
        var classifier = new LongshotProcessRoleClassifier(reader);
        var many = Enumerable.Range(1, LongshotProcessRoleClassifier.CacheCapacity + 40)
            .Select(i => Proc(i * 4, T0.AddSeconds(i)))
            .ToArray();

        var selection = classifier.SelectCandidates(many);

        Assert.Equal(many.Length, selection.Candidates.Count); // all still classified and retained
        Assert.Equal(LongshotProcessRoleClassifier.CacheCapacity, classifier.CachedCount);
    }

    [Fact]
    public void Cached_helper_relationship_is_recomputed_from_the_current_list()
    {
        var reader = new FakeCommandLineReader().Set(100, Game()).Set(200, CrashWatch(100));
        var classifier = new LongshotProcessRoleClassifier(reader);
        var helper = Proc(200, T0.AddMilliseconds(30));

        var before = classifier.Classify(new[] { Proc(100, T0), helper });
        Assert.Equal(HelperRelationship.Confirmed, before.Single(c => c.ProcessId == 200).Relationship);

        var after = classifier.Classify(new[] { helper }); // game exited; helper served from cache
        Assert.Equal(HelperRelationship.Orphaned, Assert.Single(after).Relationship);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public void Two_genuine_games_remain_ambiguous_with_a_warm_cache()
    {
        var reader = new FakeCommandLineReader()
            .Set(100, Game()).Set(101, CrashWatch(100))
            .Set(300, Game()).Set(301, CrashWatch(300));
        var classifier = new LongshotProcessRoleClassifier(reader);
        var processes = new[]
        {
            Proc(100, T0), Proc(101, T0.AddMilliseconds(30)),
            Proc(300, T0.AddSeconds(5)), Proc(301, T0.AddSeconds(5).AddMilliseconds(30)),
        };

        for (var i = 0; i < 3; i++)
        {
            var candidates = classifier.SelectCandidates(processes).Candidates;
            var result = ProcessSelector.Select(candidates, bridge: null);
            Assert.Equal(ProcessSelectionStatus.Ambiguous, result.Selection);
            Assert.Null(result.SelectedProcess);
        }
    }

    // ---------------- defensive parsing ----------------

    private const string Secret = "PRIVATE-USER-91c2";

    [Fact]
    public void Parser_exception_becomes_unknown_is_retained_and_is_not_cached()
    {
        var reader = new FakeCommandLineReader().Set(100, $"C:\\Users\\{Secret}\\x.exe");
        var parses = 0;
        var classifier = new LongshotProcessRoleClassifier(reader, _ =>
        {
            parses++;
            throw new InvalidOperationException("parse failed for C:\\Users\\" + Secret);
        });

        var selection = classifier.SelectCandidates(new[] { Proc(100, T0) });
        classifier.SelectCandidates(new[] { Proc(100, T0) });

        Assert.Equal(LongshotProcessRole.Unknown, Assert.Single(selection.Classifications).Role);
        Assert.Equal(100, Assert.Single(selection.Candidates).ProcessId);
        Assert.All(selection.Diagnostics, d => Assert.DoesNotContain(Secret, d));
        Assert.Equal(2, parses); // retried, not cached
        Assert.Equal(0, classifier.CachedCount);
    }

    [Fact]
    public void Crash_watch_result_without_a_referenced_pid_is_treated_as_unknown()
    {
        var reader = new FakeCommandLineReader().Set(100, "x");
        var classifier = new LongshotProcessRoleClassifier(reader, _ => (LongshotProcessRole.CrashWatchHelper, null));

        var selection = classifier.SelectCandidates(new[] { Proc(100, T0) });

        Assert.Equal(LongshotProcessRole.Unknown, Assert.Single(selection.Classifications).Role);
        Assert.Equal(100, Assert.Single(selection.Candidates).ProcessId); // retained, not excluded
    }
}

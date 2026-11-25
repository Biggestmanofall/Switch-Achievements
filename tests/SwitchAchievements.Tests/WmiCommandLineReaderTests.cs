using System.Diagnostics;
using System.Management;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests;

/// <summary>
/// M5.6 R1.1: WMI reader identity matching and bounded execution. The WMI
/// round-trip is injected, except for two smoke tests against this test process.
/// </summary>
public class WmiCommandLineReaderTests
{
    private const string CommandLine = "\"C:\\x\\bin\\longshot.exe\"";
    private static readonly DateTime StartUtc = new(2026, 11, 23, 4, 32, 17, 147, DateTimeKind.Utc);

    private static GameProcessInfo Proc(int pid = 100, DateTime? start = null)
        => new(pid, @"C:\x\bin\longshot.exe", start ?? StartUtc);

    // WMI reports CreationDate as a CIM datetime in local time with a UTC offset.
    private static string Dmtf(DateTime utc) => ManagementDateTimeConverter.ToDmtfDateTime(utc.ToLocalTime());

    private static WmiCommandLineReader Reader(Func<int, TimeSpan, WmiProcessRow?> query, TimeSpan? deadline = null)
        => new(query, deadline ?? TimeSpan.FromSeconds(5));

    // ---------------- identity matching ----------------

    [Fact]
    public void Matching_pid_and_start_time_returns_the_command_line()
    {
        var queried = new List<int>();
        var reader = Reader((pid, _) => { queried.Add(pid); return new WmiProcessRow(CommandLine, Dmtf(StartUtc)); });

        Assert.Equal(CommandLine, reader.ReadCommandLine(Proc(100)));
        Assert.Equal(new[] { 100 }, queried);
    }

    [Fact]
    public void Start_time_within_tolerance_is_accepted()
    {
        var reader = Reader((_, _) => new WmiProcessRow(CommandLine, Dmtf(StartUtc.AddMilliseconds(400))));
        Assert.Equal(CommandLine, reader.ReadCommandLine(Proc()));
    }

    [Fact]
    public void Missing_row_returns_null()
        => Assert.Null(Reader((_, _) => null).ReadCommandLine(Proc()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cim-datetime")]
    public void Null_empty_or_malformed_creation_date_returns_null(string? creationDate)
        => Assert.Null(Reader((_, _) => new WmiProcessRow(CommandLine, creationDate)).ReadCommandLine(Proc()));

    [Fact]
    public void Missing_expected_start_time_returns_null_without_querying()
    {
        var queries = 0;
        var reader = Reader((_, _) => { queries++; return new WmiProcessRow(CommandLine, Dmtf(StartUtc)); });

        Assert.Null(reader.ReadCommandLine(new GameProcessInfo(100, @"C:\x\bin\longshot.exe", default)));
        Assert.Equal(0, queries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void Non_positive_pid_returns_null_without_querying(int pid)
    {
        var queries = 0;
        var reader = Reader((_, _) => { queries++; return null; });
        Assert.Null(reader.ReadCommandLine(Proc(pid)));
        Assert.Equal(0, queries);
    }

    [Theory]
    [InlineData(2)]        // just outside the tolerance
    [InlineData(-5)]
    [InlineData(3600)]     // e.g. a different instance, or an hour-off conversion
    public void Start_time_mismatch_returns_null(int offsetSeconds)
    {
        // PID reuse: the id now belongs to a process created at another time.
        var reader = Reader((_, _) => new WmiProcessRow(CommandLine, Dmtf(StartUtc.AddSeconds(offsetSeconds))));
        Assert.Null(reader.ReadCommandLine(Proc()));
    }

    [Fact]
    public void Query_exception_returns_null()
        => Assert.Null(Reader((_, _) => throw new ManagementException("access denied")).ReadCommandLine(Proc()));

    [Fact]
    public void Null_command_line_with_matching_identity_returns_null()
        => Assert.Null(Reader((_, _) => new WmiProcessRow(null, Dmtf(StartUtc))).ReadCommandLine(Proc()));

    // ---------------- bounded execution ----------------

    [Fact]
    public void Hung_query_times_out_returns_null_and_starts_no_further_queries_until_it_finishes()
    {
        using var release = new ManualResetEventSlim(false);
        var queries = 0;
        var reader = Reader((_, _) =>
        {
            var n = Interlocked.Increment(ref queries);
            if (n == 1)
            {
                release.Wait(TimeSpan.FromSeconds(30)); // simulates WMI blocked in connect/query
                return new WmiProcessRow("LATE-RESULT", Dmtf(StartUtc));
            }
            return new WmiProcessRow(CommandLine, Dmtf(StartUtc));
        }, deadline: TimeSpan.FromMilliseconds(150));

        // 1) The first read is bounded by the deadline and reports unreadable.
        var sw = Stopwatch.StartNew();
        Assert.Null(reader.ReadCommandLine(Proc()));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "read was not bounded by its deadline");
        Assert.True(reader.IsReadOutstanding);

        // 2) While it is still blocked, reads return at once and start no new WMI operation.
        sw.Restart();
        for (var i = 0; i < 5; i++)
            Assert.Null(reader.ReadCommandLine(Proc()));
        sw.Stop();
        Assert.Equal(1, Volatile.Read(ref queries));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), "reads blocked while an earlier read was outstanding");

        // 3) Once the blocked operation ends, its late result is discarded and reads resume.
        release.Set();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (reader.IsReadOutstanding && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.False(reader.IsReadOutstanding);

        Assert.Equal(CommandLine, reader.ReadCommandLine(Proc()));
        Assert.Equal(2, Volatile.Read(ref queries));
    }

    [Fact]
    public void Fast_query_within_deadline_is_not_affected_by_the_bound()
    {
        var reader = Reader((_, _) => new WmiProcessRow(CommandLine, Dmtf(StartUtc)), TimeSpan.FromMilliseconds(500));
        for (var i = 0; i < 3; i++)
            Assert.Equal(CommandLine, reader.ReadCommandLine(Proc()));
        Assert.False(reader.IsReadOutstanding);
    }

    [Fact]
    public void Query_receives_the_configured_deadline_for_enumeration()
    {
        TimeSpan? seen = null;
        var reader = Reader((_, t) => { seen = t; return null; }, TimeSpan.FromMilliseconds(750));
        reader.ReadCommandLine(Proc());
        Assert.Equal(TimeSpan.FromMilliseconds(750), seen);
    }

    // ---------------- real WMI smoke tests (this test process; nothing is printed) ----------------

    private static GameProcessInfo Self(TimeSpan shift = default)
    {
        using var self = Process.GetCurrentProcess();
        return new GameProcessInfo(self.Id, string.Empty, self.StartTime.ToUniversalTime() + shift);
    }

    [Fact]
    public void Real_wmi_reads_this_process_when_its_identity_matches()
    {
        var commandLine = new WmiCommandLineReader().ReadCommandLine(Self());
        Assert.True(commandLine is { Length: > 0 }, "expected the test process's own command line to be readable");
    }

    [Fact]
    public void Real_wmi_rejects_a_start_time_that_does_not_match()
        => Assert.Null(new WmiCommandLineReader().ReadCommandLine(Self(TimeSpan.FromHours(1))));
}

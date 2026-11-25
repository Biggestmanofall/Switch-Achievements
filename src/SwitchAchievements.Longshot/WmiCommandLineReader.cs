using System;
using System.Globalization;
using System.Management;
using System.Threading;
using SwitchAchievements.Core;

namespace SwitchAchievements.Longshot;

/// <summary>
/// <see cref="ICommandLineReader"/> backed by the documented WMI class
/// <c>Win32_Process</c> (<c>CommandLine</c>, <c>CreationDate</c>) through
/// <see cref="System.Management"/>.
///
/// Conservative by design: every failure (access denied, the process exited,
/// a WMI error, a null command line, a missed deadline) yields null, never an
/// exception. To guard against process-exit / PID-reuse races, the WMI row is
/// accepted only when its creation time matches the expected process start time;
/// if the start time is unknown, the identity cannot be confirmed and null is returned.
///
/// <para><b>Bounded execution (R1.1).</b> System.Management offers no way to
/// cancel a synchronous query, and its <c>Timeout</c> only bounds enumerating the
/// results, not connecting to WMI (<c>IWbemLocator::ConnectServer</c>), which can
/// block indefinitely if the WMI service hangs. Each read therefore runs on its own
/// background thread and the caller waits at most an overall deadline. A blocked
/// COM call cannot be terminated, so it is <i>contained</i> instead:</para>
/// <list type="bullet">
/// <item>Single flight: while an earlier read is still blocked inside WMI, no new
/// WMI operation is started; reads return null (unreadable) immediately. At most
/// one WMI operation per reader is ever outstanding, so a hung provider cannot
/// accumulate threads or operations.</item>
/// <item>The worker only returns the raw row; identity matching happens on the
/// caller. A row that arrives after the caller gave up is discarded unused.</item>
/// <item>The worker is a background thread that nothing waits on, so it never
/// blocks monitoring shutdown, disposal, or process exit.</item>
/// </list>
///
/// Nothing read here is logged or stored (see <see cref="ICommandLineReader"/>).
/// </summary>
public sealed class WmiCommandLineReader : ICommandLineReader
{
    /// <summary>Default overall deadline for one read (connect + query + enumerate).</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(3);

    /// <summary>Largest accepted difference between WMI's creation time and the expected start time.</summary>
    internal static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _deadline;
    private readonly Func<int, TimeSpan, WmiProcessRow?> _query;
    private readonly object _gate = new();
    private PendingRead? _pending;

    public WmiCommandLineReader(TimeSpan? deadline = null)
        : this(QueryWmi, deadline)
    {
    }

    /// <summary>Test seam: <paramref name="query"/> replaces the WMI round-trip.</summary>
    internal WmiCommandLineReader(Func<int, TimeSpan, WmiProcessRow?> query, TimeSpan? deadline = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _deadline = deadline is { } d && d > TimeSpan.Zero ? d : DefaultDeadline;
    }

    /// <summary>True while an earlier read is still blocked inside WMI (diagnostics/tests).</summary>
    internal bool IsReadOutstanding
    {
        get { lock (_gate) return _pending is { IsCompleted: false }; }
    }

    public string? ReadCommandLine(GameProcessInfo process)
    {
        if (process.ProcessId <= 0 || process.ProcessStartUtc == default)
            return null;

        PendingRead read;
        lock (_gate)
        {
            if (_pending is { IsCompleted: false })
                return null; // a previous WMI operation is still blocked: do not start another

            read = new PendingRead();
            _pending = read;
        }

        var pid = process.ProcessId;
        var deadline = _deadline;
        var query = _query;
        try
        {
            var worker = new Thread(() => read.Run(() => query(pid, deadline)))
            {
                IsBackground = true,
                Name = "SwitchAchievements.WmiCommandLineRead",
            };
            worker.Start();
        }
        catch
        {
            read.Run(() => null); // could not start: release the gate, report unreadable
            return null;
        }

        if (!read.TryTake(deadline, out var row))
            return null; // deadline passed: abandoned (its late result is discarded)

        return MatchIdentity(process, row);
    }

    /// <summary>
    /// Accepts the row's command line only when the row is the expected process
    /// instance (creation time within <see cref="StartTimeTolerance"/>).
    /// </summary>
    internal static string? MatchIdentity(GameProcessInfo process, WmiProcessRow? row)
    {
        if (row?.CreationDate is not { Length: > 0 } creation)
            return null; // missing row (process gone) or no creation time

        DateTime createdUtc;
        try
        {
            createdUtc = ManagementDateTimeConverter.ToDateTime(creation).ToUniversalTime();
        }
        catch
        {
            return null; // malformed CIM datetime
        }

        if ((createdUtc - process.ProcessStartUtc).Duration() > StartTimeTolerance)
            return null; // a different process instance now has this id

        return row.CommandLine;
    }

    private static WmiProcessRow? QueryWmi(int processId, TimeSpan timeout)
    {
        var query = new ObjectQuery(
            "SELECT CommandLine, CreationDate FROM Win32_Process WHERE ProcessId = "
            + processId.ToString(CultureInfo.InvariantCulture));
        var options = new System.Management.EnumerationOptions
        {
            Timeout = timeout, // bounds enumeration only; the overall deadline bounds the rest
            ReturnImmediately = true,
            Rewindable = false,
        };

        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), query, options);
        using var results = searcher.Get();

        foreach (ManagementBaseObject row in results)
        {
            using (row)
                return new WmiProcessRow(row["CommandLine"] as string, row["CreationDate"] as string);
        }

        return null; // no such process
    }

    /// <summary>One in-flight read: completed by the worker, awaited (bounded) by the caller.</summary>
    private sealed class PendingRead
    {
        private readonly object _lock = new();
        private bool _completed;
        private bool _abandoned;
        private WmiProcessRow? _row;

        public bool IsCompleted
        {
            get { lock (_lock) return _completed; }
        }

        public void Run(Func<WmiProcessRow?> query)
        {
            WmiProcessRow? row;
            try
            {
                row = query();
            }
            catch
            {
                row = null; // access denied, WMI unavailable, enumeration timeout, ...
            }

            lock (_lock)
            {
                _completed = true;
                if (!_abandoned)
                    _row = row; // otherwise drop it: nobody will read it
                Monitor.PulseAll(_lock);
            }
        }

        public bool TryTake(TimeSpan deadline, out WmiProcessRow? row)
        {
            var deadlineAt = Environment.TickCount64 + (long)Math.Ceiling(deadline.TotalMilliseconds);
            lock (_lock)
            {
                while (!_completed)
                {
                    var remaining = deadlineAt - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        _abandoned = true;
                        row = null;
                        return false;
                    }

                    Monitor.Wait(_lock, (int)Math.Min(remaining, int.MaxValue));
                }

                row = _row;
                _row = null; // the caller takes the only reference
                return true;
            }
        }
    }
}

/// <summary>
/// A raw <c>Win32_Process</c> row. Deliberately a plain class (no record
/// <c>ToString</c>) so the command line can never be printed by accident.
/// </summary>
internal sealed class WmiProcessRow
{
    public WmiProcessRow(string? commandLine, string? creationDate)
    {
        CommandLine = commandLine;
        CreationDate = creationDate;
    }

    public string? CommandLine { get; }

    public string? CreationDate { get; }
}

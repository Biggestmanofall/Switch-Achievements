using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Fakes;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.BridgeAvailability;

/// <summary>
/// M5.8.1: MonitoringService skips connection attempts while the integration's endpoint
/// is absent, and reports <see cref="MonitoringDiagnostic.LiveTrackingDisabled"/> for the
/// selected process once its startup grace has passed. The loop runs for real with a short
/// poll interval; "now" comes from a <see cref="TestClock"/>; every wait is a bounded wait
/// for a condition, never a fixed sleep.
/// </summary>
public class MonitoringServiceBridgeAvailabilityTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private static readonly DateTimeOffset Now0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime OldStart = Now0.UtcDateTime.AddMinutes(-5);   // well past the grace
    private static readonly DateTime FreshStart = Now0.UtcDateTime;                  // just started
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private static GameProcessInfo Game(int pid, DateTime start) => new(pid, GamePath, start);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly object _lock = new();
        private readonly List<MonitoringDiagnostic> _events = new();
        private int _availability = (int)EndpointAvailability.Absent;

        public FakeGameIntegration Integration { get; } = new();
        public FakeProcessProvider Provider { get; } = new();
        public CapturingLogger Logger { get; } = new();
        public TestClock Clock { get; } = new(Now0);
        public MonitoringService Service { get; }
        public StateTransitionObserver Observer { get; }

        public Rig(bool scriptAvailability = true, int requiredStableObservations = 2, TimeSpan? pollInterval = null,
            GameIdentifier? identifier = null)
        {
            if (scriptAvailability)
                Integration.EndpointAvailabilityOverride = _ => Availability;
            Service = new MonitoringService(Integration, Provider, Logger,
                requiredStableObservations: requiredStableObservations,
                pollInterval: pollInterval ?? TimeSpan.FromMilliseconds(20),
                identifier: identifier,
                timeProvider: Clock);
            Service.DiagnosticChanged += () => { lock (_lock) _events.Add(Service.Diagnostic); };
            Observer = new StateTransitionObserver(Service);
        }

        public EndpointAvailability Availability
        {
            get => (EndpointAvailability)Volatile.Read(ref _availability);
            set => Volatile.Write(ref _availability, (int)value);
        }

        public IReadOnlyList<MonitoringDiagnostic> DiagnosticEvents
        {
            get { lock (_lock) return _events.ToArray(); }
        }

        public async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + WaitLimit;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail($"Timed out waiting for {what}; state={Service.State}, diagnostic={Service.Diagnostic}, " +
                                $"checks={Integration.CheckEndpointCount}, connects={Integration.ConnectCount}, " +
                                $"states: {string.Join(" -> ", Observer.States)}");
                await Task.Delay(5);
            }
        }

        /// <summary>Waits for <paramref name="count"/> more endpoint checks than now (each is one pass of the loop).</summary>
        public Task WaitForMoreChecksAsync(int count)
        {
            var target = Integration.CheckEndpointCount + count;
            return WaitUntilAsync(() => Integration.CheckEndpointCount >= target, $"{count} more endpoint checks");
        }

        public Task WaitForDiagnosticAsync(MonitoringDiagnostic diagnostic)
            => WaitUntilAsync(() => Service.Diagnostic == diagnostic, "diagnostic " + diagnostic);

        public int LogCount(string fragment) => Logger.Count(fragment);

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync().WaitAsync(WaitLimit);
            Observer.Dispose();
        }
    }

    // ---------------- absent bridge ----------------

    [Fact]
    public async Task Absent_past_the_grace_reports_disabled_and_never_connects()
    {
        await using var rig = new Rig();
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();

        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled);
        await rig.WaitForMoreChecksAsync(5);

        Assert.Equal(MonitoringState.Connecting, rig.Service.State);
        Assert.Equal(0, rig.Integration.ConnectCount);
        Assert.Equal(new[] { MonitoringDiagnostic.LiveTrackingDisabled }, rig.DiagnosticEvents); // raised once
        Assert.Equal(1, rig.LogCount("live tracking disabled"));
        Assert.Equal(1, rig.LogCount("pid=100"));
        Assert.DoesNotContain(rig.Logger.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) || l.StartsWith("ERROR", StringComparison.Ordinal));
        Assert.DoesNotContain(MonitoringState.Error, rig.Observer.States);
        Assert.DoesNotContain(MonitoringState.Reconnecting, rig.Observer.States); // no connect/timeout cycle
    }

    [Fact]
    public async Task Absent_within_the_grace_waits_without_a_diagnostic_then_reports_once_it_passes()
    {
        await using var rig = new Rig();
        rig.Provider.Processes = new[] { Game(100, FreshStart) };
        await rig.Service.StartAsync();

        await rig.WaitUntilAsync(() => rig.Service.State == MonitoringState.Connecting, "Connecting");
        await rig.WaitForMoreChecksAsync(5);
        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic);
        Assert.Empty(rig.DiagnosticEvents);
        Assert.Equal(0, rig.Integration.ConnectCount);

        rig.Clock.Advance(TimeSpan.FromSeconds(9));
        await rig.WaitForMoreChecksAsync(3);
        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic); // 9 s: still within the grace

        rig.Clock.Advance(TimeSpan.FromSeconds(1)); // exactly the 10 s grace
        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled);
        Assert.Equal(0, rig.Integration.ConnectCount);
        Assert.Equal(MonitoringState.Connecting, rig.Service.State);
    }

    [Fact]
    public async Task An_unknown_start_time_measures_the_grace_from_the_first_absent_check()
    {
        await using var rig = new Rig();
        rig.Provider.Processes = new[] { Game(100, default) };
        await rig.Service.StartAsync();

        await rig.WaitUntilAsync(() => rig.Integration.CheckEndpointCount > 0, "a first check");
        await rig.WaitForMoreChecksAsync(3);
        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic);

        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled);
    }

    [Fact]
    public async Task When_the_pipe_appears_the_diagnostic_clears_and_monitoring_connects()
    {
        await using var rig = new Rig();
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();
        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled);

        rig.Availability = EndpointAvailability.Available;
        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: 0);

        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic);
        Assert.Equal(1, rig.Integration.ConnectCount);
        Assert.Equal(new[] { MonitoringDiagnostic.LiveTrackingDisabled, MonitoringDiagnostic.None }, rig.DiagnosticEvents);
        Assert.Equal(1, rig.LogCount("live tracking diagnostic cleared"));
    }

    // ---------------- unchanged behaviour ----------------

    [Fact]
    public async Task An_available_pipe_connects_exactly_as_before()
    {
        await using var rig = new Rig { Availability = EndpointAvailability.Available };
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();

        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: 0);

        Assert.Equal(new[] { MonitoringState.Searching, MonitoringState.Connecting, MonitoringState.Monitoring }, rig.Observer.States);
        Assert.Equal(1, rig.Integration.ConnectCount);
        Assert.Equal(1, rig.Integration.CheckEndpointCount);
        Assert.Empty(rig.DiagnosticEvents);
        Assert.Equal(0, rig.LogCount("live tracking"));
    }

    [Fact]
    public async Task Unknown_availability_connects_exactly_as_before()
    {
        await using var rig = new Rig(scriptAvailability: false); // the fake's default: Unknown
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();

        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: 0);

        Assert.Equal(new[] { MonitoringState.Searching, MonitoringState.Connecting, MonitoringState.Monitoring }, rig.Observer.States);
        Assert.Equal(1, rig.Integration.ConnectCount);
        Assert.Empty(rig.DiagnosticEvents);
        Assert.Equal(0, rig.LogCount("live tracking"));
    }

    [Fact]
    public async Task A_failing_endpoint_check_is_treated_as_unknown()
    {
        await using var rig = new Rig();
        rig.Integration.EndpointAvailabilityOverride = _ => throw new InvalidOperationException("check broke");
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();

        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: 0);

        Assert.Equal(1, rig.Integration.ConnectCount);
        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic);
        Assert.Equal(1, rig.LogCount("endpoint availability check failed"));
    }

    [Fact]
    public async Task A_real_connection_timeout_still_reconnects_as_before()
    {
        await using var rig = new Rig { Availability = EndpointAvailability.Available };
        var attempts = 0;
        rig.Integration.OnConnect = (_, _) => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException(new TimeoutException("bridge slow"))
            : Task.CompletedTask;
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();

        var reconnecting = await rig.Observer.WaitForAsync(MonitoringState.Reconnecting, after: 0);
        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: reconnecting.Index);

        Assert.Equal(2, rig.Integration.ConnectCount);
        Assert.Equal(1, rig.LogCount("connect/subscribe timed out"));
        Assert.Empty(rig.DiagnosticEvents);
    }

    // ---------------- a bridge that already worked ----------------

    [Fact]
    public async Task A_process_whose_bridge_worked_is_rechecked_but_never_reported_disabled_and_reconnects_when_it_returns()
    {
        await using var rig = new Rig { Availability = EndpointAvailability.Available };
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();
        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: 0);

        // The pipe disappears (normally: the game is exiting) and the connection drops.
        rig.Availability = EndpointAvailability.Absent;
        var before = rig.Observer.Count;
        rig.Integration.RaiseDisconnected();
        await rig.Observer.WaitForAsync(MonitoringState.Reconnecting, after: before);
        var connecting = await rig.Observer.WaitForAsync(MonitoringState.Connecting, after: before);

        await rig.WaitForMoreChecksAsync(5); // still checked on every pass
        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic);
        Assert.Empty(rig.DiagnosticEvents);
        Assert.Equal(1, rig.Integration.ConnectCount); // no attempts while absent

        // The pipe returns: normal connection resumes.
        rig.Availability = EndpointAvailability.Available;
        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: connecting.Index);
        Assert.Equal(2, rig.Integration.ConnectCount);
        Assert.Empty(rig.DiagnosticEvents);
    }

    [Fact]
    public async Task A_replacement_process_has_its_own_grace_and_bridge_history()
    {
        await using var rig = new Rig { Availability = EndpointAvailability.Available };
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();
        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: 0);

        // Process 100 (whose bridge worked) is replaced by a freshly started process 200 without a bridge.
        rig.Availability = EndpointAvailability.Absent;
        rig.Provider.Processes = new[] { Game(200, FreshStart) };
        var before = rig.Observer.Count;
        rig.Integration.RaiseDisconnected();
        await rig.Observer.WaitForAsync(MonitoringState.Connecting, after: before);
        await rig.WaitForMoreChecksAsync(5);
        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic); // within 200's own grace

        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled); // 100's history does not suppress it
        Assert.Equal(1, rig.LogCount("pid=200"));
        Assert.Equal(1, rig.Integration.ConnectCount);
    }

    // ---------------- ownership and clearing ----------------

    [Fact]
    public async Task Replacing_the_process_while_absent_clears_the_old_diagnostic_before_the_new_one_is_reported()
    {
        await using var rig = new Rig();
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();
        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled);

        rig.Provider.Processes = new[] { Game(200, OldStart) };
        await rig.WaitUntilAsync(() => rig.DiagnosticEvents.Count == 3, "the old diagnostic cleared and the new one reported");

        Assert.Equal(
            new[] { MonitoringDiagnostic.LiveTrackingDisabled, MonitoringDiagnostic.None, MonitoringDiagnostic.LiveTrackingDisabled },
            rig.DiagnosticEvents);
        Assert.Equal(1, rig.LogCount("pid=100"));
        Assert.Equal(1, rig.LogCount("pid=200"));
        Assert.Equal(0, rig.Integration.ConnectCount);
    }

    [Fact]
    public async Task The_process_exiting_while_absent_clears_the_diagnostic()
    {
        await using var rig = new Rig();
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();
        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled);

        var before = rig.Observer.Count;
        rig.Provider.Processes = Array.Empty<GameProcessInfo>();
        await rig.Observer.WaitForAsync(MonitoringState.Searching, after: before);

        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic);
        Assert.Equal(new[] { MonitoringDiagnostic.LiveTrackingDisabled, MonitoringDiagnostic.None }, rig.DiagnosticEvents);
    }

    [Fact]
    public async Task Ambiguity_while_absent_clears_the_diagnostic()
    {
        await using var rig = new Rig();
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();
        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled);

        var before = rig.Observer.Count;
        rig.Provider.Processes = new[] { Game(100, OldStart), Game(200, OldStart) };
        await rig.Observer.WaitForAsync(MonitoringState.Ambiguous, after: before);

        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic);
    }

    [Fact]
    public async Task Ambiguous_selection_is_never_checked_or_diagnosed()
    {
        await using var rig = new Rig();
        rig.Provider.Processes = new[] { Game(100, OldStart), Game(200, OldStart) };
        await rig.Service.StartAsync();

        await rig.Observer.WaitForAsync(MonitoringState.Ambiguous, after: 0);
        var passes = rig.Integration.SelectCandidatesCount + 5;
        await rig.WaitUntilAsync(() => rig.Integration.SelectCandidatesCount >= passes, "more discovery passes");

        Assert.Equal(0, rig.Integration.CheckEndpointCount);
        Assert.Empty(rig.DiagnosticEvents);
    }

    [Fact]
    public async Task No_game_process_is_never_checked_or_diagnosed()
    {
        await using var rig = new Rig();
        rig.Provider.Processes = new[] { new GameProcessInfo(300, @"C:\Other\longshot.exe", OldStart) }; // not a game binary
        await rig.Service.StartAsync();

        await rig.Observer.WaitForAsync(MonitoringState.Searching, after: 0);
        var passes = rig.Integration.SelectCandidatesCount + 5;
        await rig.WaitUntilAsync(() => rig.Integration.SelectCandidatesCount >= passes, "more discovery passes");

        Assert.Equal(0, rig.Integration.CheckEndpointCount);
        Assert.Empty(rig.DiagnosticEvents);
    }

    [Fact]
    public async Task Stopping_while_waiting_for_the_pipe_is_prompt_and_clears_the_diagnostic()
    {
        // A long poll interval: the loop sits in its (cancellable) wait after reporting.
        var rig = new Rig(requiredStableObservations: 1, pollInterval: TimeSpan.FromSeconds(30));
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();
        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled);

        await rig.Service.StopAsync().WaitAsync(WaitLimit);

        Assert.Equal(MonitoringState.Stopped, rig.Service.State);
        Assert.Equal(MonitoringDiagnostic.None, rig.Service.Diagnostic);
        Assert.Equal(new[] { MonitoringDiagnostic.LiveTrackingDisabled, MonitoringDiagnostic.None }, rig.DiagnosticEvents);
        rig.Observer.Dispose();
    }

    [Fact]
    public async Task Identification_is_still_published_while_the_bridge_is_absent()
    {
        var identifier = new GameIdentifier(new FakeIdentityProvider(), IdentityTestData.OotCatalog());
        await using var rig = new Rig(identifier: identifier);
        rig.Provider.Processes = new[] { Game(100, OldStart) };
        await rig.Service.StartAsync();

        await rig.WaitForDiagnosticAsync(MonitoringDiagnostic.LiveTrackingDisabled);

        var identification = rig.Service.CurrentIdentification;
        Assert.NotNull(identification);
        Assert.True(identification.IsIdentified);
        Assert.Equal(new ProcessFingerprint(100, OldStart), identification.Evidence.Process);
    }
}

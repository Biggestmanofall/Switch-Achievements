using System.Collections.Concurrent;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Fakes;
using SwitchAchievements.Tests.Identity;

namespace SwitchAchievements.Tests.MonitoringIdentity;

/// <summary>
/// M5.6.5: identification inside the monitoring lifecycle. Waits only on stable
/// conditions (Monitoring, Searching, Error, counters, session values) or on recorded
/// transitions — never by sampling the transient Reconnecting state.
/// </summary>
public class MonitoringServiceIdentificationTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private const string Zelda = "zelda-ocarina-of-time";

    private static GameProcessInfo Game(int pid, DateTime? start = null) => new(pid, GamePath, start ?? T0);
    private static ProcessFingerprint Fp(GameProcessInfo p) => new(p.ProcessId, p.ProcessStartUtc);

    private sealed record Rig(FakeGameIntegration Integration, FakeProcessProvider Processes, FakeIdentityProvider Provider,
        CapturingLogger Logger, MonitoringService Service, ConcurrentQueue<MonitoringState> Transitions);

    private static Rig Create(FakeIdentityProvider? provider = null, bool withCatalog = true, bool withIdentifier = true)
    {
        var integration = new FakeGameIntegration();
        var processes = new FakeProcessProvider();
        provider ??= new FakeIdentityProvider();
        var logger = new CapturingLogger();
        var service = new MonitoringService(integration, processes, logger,
            requiredStableObservations: 2, pollInterval: TimeSpan.FromMilliseconds(20),
            identifier: withIdentifier ? new GameIdentifier(provider, withCatalog ? IdentityTestData.OotCatalog() : null) : null);
        var transitions = new ConcurrentQueue<MonitoringState>();
        service.StateChanged += transitions.Enqueue; // subscribed before start: no transition can be missed
        return new Rig(integration, processes, provider, logger, service, transitions);
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    private static Task WaitForState(Rig rig, MonitoringState state)
        => WaitFor(() => rig.Service.State == state, $"{state} (actual {rig.Service.State})");

    private static async Task<SessionSnapshot> SendSnapshot(Rig rig, GameProcessInfo process, long frame)
    {
        rig.Integration.RaiseSnapshot(TestSnapshot.Create(Fp(process), rupees: 1, gameFrame: frame));
        await WaitFor(() => rig.Service.Session.Latest?.GameFrame == frame && rig.Service.Session.Latest.SourceProcessId == process.ProcessId,
            $"snapshot frame {frame} from pid {process.ProcessId}");
        return rig.Service.Session;
    }

    private static int ConnectCount(Rig rig) { lock (rig.Integration.ConnectedProcesses) return rig.Integration.ConnectedProcesses.Count; }

    // ---------------- outcomes ----------------

    [Fact]
    public async Task Identified_process_snapshots_and_session_carry_the_catalog_game_id()
    {
        var rig = Create();
        var a = Game(100);
        rig.Processes.Processes = new[] { a };

        await rig.Service.StartAsync();
        await WaitForState(rig, MonitoringState.Monitoring);
        var session = await SendSnapshot(rig, a, frame: 1);

        Assert.Equal(Zelda, session.Latest!.GameId.Value);
        Assert.NotNull(session.Identification);
        Assert.True(session.Identification!.IsIdentified);
        Assert.Equal(Fp(a), session.Identification.Game!.Process);
        Assert.Equal(new[] { 100 }, rig.Provider.Calls.Select(c => c.ProcessId));
        Assert.Contains(rig.Logger.Lines, l => l.StartsWith("INFO game identification: Identified 'zelda-ocarina-of-time'", StringComparison.Ordinal));
        await rig.Service.StopAsync();
    }

    [Theory]
    [InlineData("unknown", IdentificationStatus.Unknown)]
    [InlineData("unsupported", IdentificationStatus.Unsupported)]
    [InlineData("conflict", IdentificationStatus.Conflict)]
    public async Task Non_identified_processes_still_connect_but_are_stamped_unknown(string kind, IdentificationStatus status)
    {
        var rig = Create(new FakeIdentityProvider { Respond = p => IdentityEvidence.For(kind, p) });
        var a = Game(100);
        rig.Processes.Processes = new[] { a };

        await rig.Service.StartAsync();
        await WaitForState(rig, MonitoringState.Monitoring);
        var session = await SendSnapshot(rig, a, frame: 1);

        Assert.Equal(1, ConnectCount(rig));                      // policy (a): monitoring continues
        Assert.Equal(GameId.Unknown, session.Latest!.GameId);    // never presented as a supported game
        Assert.Equal(status, session.Identification!.Status);
        Assert.Null(session.Identification.Game);
        Assert.False(session.Identification.IsIdentified);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Unavailable_identification_never_calls_the_provider_and_is_attempted_once()
    {
        var rig = Create(withCatalog: false);
        var a = Game(100);
        rig.Processes.Processes = new[] { a };

        await rig.Service.StartAsync();
        await WaitForState(rig, MonitoringState.Monitoring);
        var first = await SendSnapshot(rig, a, frame: 1);

        rig.Integration.RaiseDisconnected();                     // reconnect to the same process
        await WaitFor(() => ConnectCount(rig) == 2 && rig.Service.State == MonitoringState.Monitoring, "reconnect");
        var second = await SendSnapshot(rig, a, frame: 2);

        Assert.Empty(rig.Provider.Calls);
        Assert.Equal(GameId.Unknown, first.Latest!.GameId);
        Assert.Null(first.Identification);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, rig.Logger.Count("game identification unavailable")); // the cached "no result" is not re-attempted
        await rig.Service.StopAsync();
    }

    // ---------------- caching and fingerprints ----------------

    [Theory]
    [InlineData("identified")]
    [InlineData("unknown")]
    public async Task Identification_happens_once_per_fingerprint_and_reconnect_reuses_it(string kind)
    {
        var rig = Create(new FakeIdentityProvider { Respond = p => IdentityEvidence.For(kind, p) });
        var a = Game(100);
        rig.Processes.Processes = new[] { a };

        await rig.Service.StartAsync();
        await WaitForState(rig, MonitoringState.Monitoring);
        var before = await SendSnapshot(rig, a, frame: 1);

        rig.Integration.RaiseDisconnected();
        await WaitFor(() => ConnectCount(rig) == 2 && rig.Service.State == MonitoringState.Monitoring, "reconnect");
        var after = await SendSnapshot(rig, a, frame: 2);

        Assert.Single(rig.Provider.Calls);                       // not retried for the same fingerprint
        Assert.Equal(before.Id, after.Id);                       // session continuity
        Assert.Equal(before.Latest!.GameId, after.Latest!.GameId);
        Assert.Same(before.Identification, after.Identification);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Restart_with_a_new_fingerprint_is_identified_independently_and_gets_a_new_session()
    {
        var rig = Create(new FakeIdentityProvider { Respond = p => p.ProcessId == 100 ? IdentityEvidence.Oot(p) : IdentityEvidence.UnsupportedBuild(p) });
        var a = Game(100);
        var b = Game(200, T0.AddMinutes(5));
        rig.Processes.Processes = new[] { a };

        await rig.Service.StartAsync();
        await WaitForState(rig, MonitoringState.Monitoring);
        var first = await SendSnapshot(rig, a, frame: 1);
        Assert.Equal(Zelda, first.Latest!.GameId.Value);

        rig.Processes.Processes = new[] { b };
        rig.Integration.RaiseDisconnected();
        await WaitFor(() => rig.Integration.LastConnectedProcess?.ProcessId == 200 && rig.Service.State == MonitoringState.Monitoring, "connected to the new process");
        var second = await SendSnapshot(rig, b, frame: 1);

        Assert.Equal(new[] { 100, 200 }, rig.Provider.Calls.Select(c => c.ProcessId));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(GameId.Unknown, second.Latest!.GameId);      // no leakage of the old process's identity
        Assert.Equal(IdentificationStatus.Unsupported, second.Identification!.Status);
        await rig.Service.StopAsync();
    }

    [Theory]
    [InlineData(999, 0)]     // a different process id
    [InlineData(100, 10)]    // the same pid reused by a later process (different start time)
    public async Task Snapshots_from_another_fingerprint_never_inherit_the_identity(int pid, int startOffsetMinutes)
    {
        var rig = Create();
        var a = Game(100);
        rig.Processes.Processes = new[] { a };

        await rig.Service.StartAsync();
        await WaitForState(rig, MonitoringState.Monitoring);
        await SendSnapshot(rig, a, frame: 1);

        var stranger = new ProcessFingerprint(pid, T0.AddMinutes(startOffsetMinutes));
        rig.Integration.RaiseSnapshot(TestSnapshot.Create(stranger, gameFrame: 7));
        await WaitFor(() => rig.Service.Session.Latest?.GameFrame == 7, "stranger snapshot");

        Assert.Equal(GameId.Unknown, rig.Service.Session.Latest!.GameId);
        Assert.Null(rig.Service.Session.Identification);
        await rig.Service.StopAsync();
    }

    // ---------------- races ----------------

    [Fact]
    public async Task Process_exit_during_identification_does_not_connect()
    {
        var provider = new FakeIdentityProvider { FirstCallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var rig = Create(provider);
        rig.Processes.Processes = new[] { Game(100) };

        await rig.Service.StartAsync();
        await provider.FirstCallStarted.Task.WaitAsync(WaitLimit);
        rig.Processes.Processes = Array.Empty<GameProcessInfo>();   // the game exits while it is being identified
        provider.FirstCallGate.SetResult();

        await WaitForState(rig, MonitoringState.Searching);
        Assert.Equal(0, ConnectCount(rig));
        Assert.Contains(MonitoringState.Reconnecting, rig.Transitions);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Restart_while_identification_is_pending_never_connects_to_the_old_process()
    {
        var provider = new FakeIdentityProvider { FirstCallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var rig = Create(provider);
        var a = Game(100);
        var b = Game(200, T0.AddMinutes(5));
        rig.Processes.Processes = new[] { a };

        await rig.Service.StartAsync();
        await provider.FirstCallStarted.Task.WaitAsync(WaitLimit);
        rig.Processes.Processes = new[] { b };                      // restarted while A is being identified
        provider.FirstCallGate.SetResult();

        await WaitFor(() => rig.Integration.LastConnectedProcess?.ProcessId == 200 && rig.Service.State == MonitoringState.Monitoring, "connected to the new process");
        lock (rig.Integration.ConnectedProcesses)
            Assert.DoesNotContain(rig.Integration.ConnectedProcesses, p => p.ProcessId == 100);
        Assert.Equal(new[] { 100, 200 }, rig.Provider.Calls.Select(c => c.ProcessId));

        var session = await SendSnapshot(rig, b, frame: 1);
        Assert.Equal(Fp(b), session.Identification!.Game!.Process);   // B's own identification, not A's
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Stop_while_identification_is_pending_cancels_and_stops_without_error()
    {
        var provider = new FakeIdentityProvider { FirstCallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var rig = Create(provider);
        rig.Processes.Processes = new[] { Game(100) };

        await rig.Service.StartAsync();
        await provider.FirstCallStarted.Task.WaitAsync(WaitLimit);
        await rig.Service.StopAsync().WaitAsync(WaitLimit);

        Assert.True(provider.ObservedCancellation);
        Assert.Equal(MonitoringState.Stopped, rig.Service.State);
        Assert.DoesNotContain(MonitoringState.Error, rig.Transitions);
        Assert.Equal(MonitoringState.Stopped, rig.Transitions.Last());
        Assert.Equal(0, ConnectCount(rig));
        Assert.DoesNotContain(rig.Logger.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connection_failure_after_identification_reconnects_and_reuses_the_result()
    {
        var rig = Create();
        var attempts = 0;
        rig.Integration.OnConnect = (_, _) =>
            Interlocked.Increment(ref attempts) == 1 ? throw new TimeoutException("pipe busy") : Task.CompletedTask;
        rig.Processes.Processes = new[] { Game(100) };

        await rig.Service.StartAsync();
        await WaitFor(() => ConnectCount(rig) == 2 && rig.Service.State == MonitoringState.Monitoring, "second connection");
        var session = await SendSnapshot(rig, Game(100), frame: 1);

        Assert.Single(rig.Provider.Calls);
        Assert.Equal(Zelda, session.Latest!.GameId.Value);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Helpers_are_never_identified()
    {
        var rig = Create();
        rig.Integration.SelectCandidatesOverride = processes => processes.Where(p => p.ProcessId != 101).ToList(); // 101 = helper
        rig.Processes.Processes = new[] { Game(100), Game(101, T0.AddMilliseconds(30)) };

        await rig.Service.StartAsync();
        await WaitForState(rig, MonitoringState.Monitoring);

        Assert.Equal(new[] { 100 }, rig.Provider.Calls.Select(c => c.ProcessId));
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Unexpected_provider_exceptions_propagate_to_the_error_state()
    {
        var rig = Create(new FakeIdentityProvider { Throw = new InvalidOperationException("bug") });
        rig.Processes.Processes = new[] { Game(100) };

        await rig.Service.StartAsync();
        await WaitForState(rig, MonitoringState.Error);

        Assert.Equal(0, ConnectCount(rig));
        Assert.Contains(rig.Logger.Lines, l => l.StartsWith("ERROR monitoring loop error", StringComparison.Ordinal));
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Without_an_identifier_behaviour_is_unchanged()
    {
        var rig = Create(withIdentifier: false);
        var a = Game(100);
        rig.Processes.Processes = new[] { a };

        await rig.Service.StartAsync();
        await WaitForState(rig, MonitoringState.Monitoring);
        rig.Integration.RaiseSnapshot(TestSnapshot.Create(Fp(a), gameFrame: 3).WithGameId(new GameId("integration-label")));
        await WaitFor(() => rig.Service.Session.Latest?.GameFrame == 3, "snapshot");

        Assert.Equal("integration-label", rig.Service.Session.Latest!.GameId.Value); // not re-stamped
        Assert.Null(rig.Service.Session.Identification);
        Assert.Empty(rig.Provider.Calls);
        await rig.Service.StopAsync();
    }
}

using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Dashboard;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.LiveTrackingSetup;

/// <summary>M5.8.2: consent, single-flight, re-checks and cancellation around the setup.</summary>
public class LiveTrackingSetupCoordinatorTests
{
    private static readonly GameProcessInfo Process100 = new(100, @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe",
        new DateTime(2026, 11, 24, 5, 0, 0, DateTimeKind.Utc));

    private static IdentifiedGame Oot() => SetupRig.Identified(Process100);

    private sealed class Rig
    {
        public int Confirmations { get; private set; }
        public int Enables { get; private set; }
        public bool Answer { get; set; } = true;
        public Func<IdentifiedGame, bool> StillEligible { get; set; } = _ => true;
        public LiveTrackingSetupResult Result { get; set; } = LiveTrackingSetupResult.Enabled;
        public Action? DuringConfirm { get; set; }
        public CancellationToken SeenToken { get; private set; }
        public TaskCompletionSource<LiveTrackingSetupResult>? BackgroundGate { get; set; }
        public CapturingLogger Logger { get; } = new();
        public List<bool> Changes { get; } = new();
        public LiveTrackingSetupCoordinator Coordinator { get; }

        public Rig()
        {
            Coordinator = new LiveTrackingSetupCoordinator(
                (_, ct) => { Enables++; SeenToken = ct; return Result; },
                _ => { Confirmations++; DuringConfirm?.Invoke(); return Answer; },
                game => StillEligible(game),
                Logger,
                work => BackgroundGate is { } gate ? gate.Task.ContinueWith(_ => work(), TaskScheduler.Default) : Task.FromResult(work()));
            Coordinator.Changed += () => Changes.Add(Coordinator.IsRunning);
        }
    }

    [Fact]
    public async Task Confirming_runs_the_setup_once_and_returns_its_result()
    {
        var rig = new Rig();

        Assert.Equal(LiveTrackingSetupResult.Enabled, await rig.Coordinator.RequestAsync(Oot()));

        Assert.Equal(1, rig.Confirmations);
        Assert.Equal(1, rig.Enables);
        Assert.False(rig.Coordinator.IsRunning);
        Assert.Equal(new[] { true, false }, rig.Changes);
        Assert.Equal(1, rig.Logger.Count("live tracking setup: Enabled"));
    }

    [Fact]
    public async Task Declining_never_runs_the_setup()
    {
        var rig = new Rig { Answer = false };

        Assert.Null(await rig.Coordinator.RequestAsync(Oot()));

        Assert.Equal(1, rig.Confirmations);
        Assert.Equal(0, rig.Enables);
        Assert.Equal(1, rig.Logger.Count("declined"));
    }

    [Fact]
    public async Task A_stale_request_is_rejected_before_asking()
    {
        var rig = new Rig { StillEligible = _ => false };

        Assert.Equal(LiveTrackingSetupResult.ProcessChanged, await rig.Coordinator.RequestAsync(Oot()));
        Assert.Equal(0, rig.Confirmations);
        Assert.Equal(0, rig.Enables);
    }

    [Fact]
    public async Task A_process_change_while_the_dialog_is_open_rejects_the_write()
    {
        var eligible = true;
        var rig = new Rig { DuringConfirm = () => eligible = false };
        rig.StillEligible = _ => eligible;

        Assert.Equal(LiveTrackingSetupResult.ProcessChanged, await rig.Coordinator.RequestAsync(Oot()));
        Assert.Equal(1, rig.Confirmations);
        Assert.Equal(0, rig.Enables);
    }

    [Fact]
    public async Task A_second_request_while_one_runs_is_ignored()
    {
        var rig = new Rig { BackgroundGate = new TaskCompletionSource<LiveTrackingSetupResult>(TaskCreationOptions.RunContinuationsAsynchronously) };

        var first = rig.Coordinator.RequestAsync(Oot());
        Assert.True(rig.Coordinator.IsRunning);
        Assert.Null(await rig.Coordinator.RequestAsync(Oot())); // a duplicate click

        rig.BackgroundGate.SetResult(default);
        Assert.Equal(LiveTrackingSetupResult.Enabled, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, rig.Confirmations);
        Assert.Equal(1, rig.Enables);
    }

    [Fact]
    public async Task Disposal_during_the_dialog_cancels_without_writing()
    {
        var rig = new Rig();
        rig.DuringConfirm = () => rig.Coordinator.Dispose();

        Assert.Equal(LiveTrackingSetupResult.Cancelled, await rig.Coordinator.RequestAsync(Oot()));
        Assert.Equal(0, rig.Enables);
    }

    [Fact]
    public async Task Disposal_while_the_setup_waits_to_run_cancels_its_token()
    {
        var rig = new Rig { BackgroundGate = new TaskCompletionSource<LiveTrackingSetupResult>(TaskCreationOptions.RunContinuationsAsynchronously) };

        var pending = rig.Coordinator.RequestAsync(Oot());
        rig.Coordinator.Dispose();
        rig.BackgroundGate.SetResult(default);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(rig.SeenToken.IsCancellationRequested); // the setup saw shutdown before writing
    }

    [Fact]
    public async Task Requests_after_disposal_are_ignored()
    {
        var rig = new Rig();
        rig.Coordinator.Dispose();
        rig.Coordinator.Dispose(); // idempotent

        Assert.Null(await rig.Coordinator.RequestAsync(Oot()));
        Assert.Equal(0, rig.Confirmations);
    }

    [Fact]
    public async Task An_unexpected_setup_exception_is_never_reported_as_safe()
    {
        var logger = new CapturingLogger();
        var coordinator = new LiveTrackingSetupCoordinator(
            (_, _) => throw new InvalidOperationException("boom"), _ => true, _ => true, logger, work => Task.Run(work));

        Assert.Equal(LiveTrackingSetupResult.RecoveryFailed, await coordinator.RequestAsync(Oot()));
        Assert.Equal(1, logger.Count("failed unexpectedly: InvalidOperationException"));
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task A_recovery_failure_is_logged_as_an_error_without_a_path()
    {
        var rig = new Rig { Result = LiveTrackingSetupResult.RecoveryFailed };

        await rig.Coordinator.RequestAsync(Oot());

        var line = Assert.Single(rig.Logger.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
        Assert.Contains("may be partially changed", line, StringComparison.Ordinal);
        Assert.DoesNotContain(@":\", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_setup_runs_off_the_calling_thread_by_default()
    {
        // The setup blocks until released. Run inline, RequestAsync could not return before
        // the release; run in the background, it returns an unfinished task at once.
        // (Thread ids are not compared: an awaiting test's pool thread may run the work.)
        using var release = new ManualResetEventSlim(false);
        var coordinator = new LiveTrackingSetupCoordinator(
            (_, _) => { release.Wait(TimeSpan.FromSeconds(5)); return LiveTrackingSetupResult.Enabled; },
            _ => true, _ => true, new CapturingLogger());

        var pending = coordinator.RequestAsync(Oot());
        Assert.False(pending.IsCompleted);

        release.Set();
        Assert.Equal(LiveTrackingSetupResult.Enabled, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ---------------- end to end with the real setup ----------------

    [Fact]
    public async Task Only_an_explicit_yes_writes_longshot_env_txt()
    {
        using var setupRig = new SetupRig();
        var setup = setupRig.Create();
        var answer = false;
        var coordinator = new LiveTrackingSetupCoordinator(setup.Enable, _ => answer, _ => true, new CapturingLogger());

        Assert.Null(await coordinator.RequestAsync(setupRig.Oot));
        Assert.False(setupRig.EnvFileExists); // declined: untouched

        answer = true;
        Assert.Equal(LiveTrackingSetupResult.Enabled, await coordinator.RequestAsync(setupRig.Oot));
        Assert.True(setupRig.EnvFileExists);
    }

    // ---------------- consent text ----------------

    [Fact]
    public void The_consent_dialog_explains_the_change_and_its_consequences()
    {
        Assert.Equal("Enable live tracking?", LiveTrackingConsent.Title);
        var q = LiveTrackingConsent.Question;
        Assert.Contains("HOS_CONTROL=longshot_control", q, StringComparison.Ordinal);
        Assert.Contains("longshot_env.txt", q, StringComparison.Ordinal);
        Assert.Contains("On future launches", q, StringComparison.Ordinal);
        Assert.Contains("button presses", q, StringComparison.Ordinal);
        Assert.Contains("quitting", q, StringComparison.Ordinal);
        Assert.Contains("changing settings", q, StringComparison.Ordinal);
        Assert.Contains("disabled until you explicitly enable it", q, StringComparison.Ordinal);
        Assert.Contains("Game-memory access remains disabled", q, StringComparison.Ordinal);
        Assert.Contains("will not affect the game currently running", q, StringComparison.Ordinal);
        Assert.Contains("in-game Restart option also works", q, StringComparison.Ordinal);
    }

    // ---------------- eligibility ----------------

    private static GameIdentification Identify(string kind)
        => GameIdentityResolver.Resolve(IdentityTestData.OotCatalog(), IdentityEvidence.For(kind, Process100));

    [Fact]
    public void Only_a_disabled_identified_longshot_game_while_connecting_is_eligible()
    {
        var eligible = LiveTrackingEligibility.EligibleGame(MonitoringState.Connecting, MonitoringDiagnostic.LiveTrackingDisabled, Identify("identified"));
        Assert.NotNull(eligible);
        Assert.Equal(new ProcessFingerprint(100, Process100.ProcessStartUtc), eligible.Process);
        Assert.Equal("zelda-ocarina-of-time", eligible.GameId.Value);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("conflict")]
    public void Unidentified_results_are_not_eligible(string kind)
        => Assert.Null(LiveTrackingEligibility.EligibleGame(MonitoringState.Connecting, MonitoringDiagnostic.LiveTrackingDisabled, Identify(kind)));

    [Fact]
    public void Unavailable_identification_is_not_eligible()
        => Assert.Null(LiveTrackingEligibility.EligibleGame(MonitoringState.Connecting, MonitoringDiagnostic.LiveTrackingDisabled, null));

    [Fact]
    public void No_diagnostic_is_not_eligible()
        => Assert.Null(LiveTrackingEligibility.EligibleGame(MonitoringState.Connecting, MonitoringDiagnostic.None, Identify("identified")));

    [Theory]
    [InlineData(MonitoringState.Stopped)]
    [InlineData(MonitoringState.Searching)]
    [InlineData(MonitoringState.Monitoring)]
    [InlineData(MonitoringState.Reconnecting)]
    [InlineData(MonitoringState.Ambiguous)]
    [InlineData(MonitoringState.Error)]
    public void Other_states_are_not_eligible(MonitoringState state)
        => Assert.Null(LiveTrackingEligibility.EligibleGame(state, MonitoringDiagnostic.LiveTrackingDisabled, Identify("identified")));

    [Fact]
    public async Task Still_eligible_follows_the_hosts_current_service_and_process()
    {
        var services = new List<ScriptedMonitoringService>();
        var host = new MonitoringHost(new CapturingLogger(),
            serviceFactory: _ => { var s = new ScriptedMonitoringService(); services.Add(s); return s; },
            catalogLoader: () => IdentityTestData.OotCatalog());
        var game = Oot();
        Assert.False(LiveTrackingEligibility.IsStillEligible(host, game)); // no service

        await host.StartAsync();
        var service = services[^1];
        service.State = MonitoringState.Connecting;
        service.Diagnostic = MonitoringDiagnostic.LiveTrackingDisabled;
        service.CurrentIdentification = Identify("identified");
        Assert.True(LiveTrackingEligibility.IsStillEligible(host, game));

        // Another process (same game) is not the same request.
        service.CurrentIdentification = GameIdentityResolver.Resolve(IdentityTestData.OotCatalog(),
            IdentityEvidence.For("identified", Process100 with { ProcessId = 200 }));
        Assert.False(LiveTrackingEligibility.IsStillEligible(host, game));

        // A replaced service starts without the diagnostic.
        service.CurrentIdentification = Identify("identified");
        Assert.True(await host.RestartAsync());
        Assert.False(LiveTrackingEligibility.IsStillEligible(host, game));

        // Stopped monitoring.
        Assert.True(await host.StopAsync());
        Assert.False(LiveTrackingEligibility.IsStillEligible(host, game));
    }
}

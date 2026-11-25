using SwitchAchievements.App.Services;
using SwitchAchievements.App.ViewModels;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.LongshotIdentity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.Dashboard;

/// <summary>
/// M5.7 Task 2: DashboardViewModel. Fully deterministic: a scripted service, a manual
/// clock and timer, and either an immediate or a queued UI poster.
/// </summary>
public class DashboardViewModelTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private const string OotName = "The Legend of Zelda: Ocarina of Time";
    private static readonly DateTime ProcessStart = new(2026, 11, 24, 5, 0, 0, DateTimeKind.Utc);
    private static readonly GameProcessInfo Process100 = new(100, GamePath, ProcessStart);
    private static readonly ProcessFingerprint Fp100 = new(100, ProcessStart);

    private sealed class Rig : IDisposable
    {
        public ManualTimeProvider Clock { get; } = new();
        public QueuedPoster Queue { get; } = new();
        public List<ScriptedMonitoringService> Services { get; } = new();
        public MonitoringHost Host { get; }
        public DashboardViewModel Vm { get; private set; } = null!;
        public List<string?> Changed { get; } = new();

        public Rig(bool catalog = true)
        {
            Host = new MonitoringHost(new CapturingLogger(),
                serviceFactory: _ => { var s = new ScriptedMonitoringService(); Services.Add(s); return s; },
                catalogLoader: () => catalog ? IdentityTestData.OotCatalog() : null);
        }

        public ScriptedMonitoringService Service => Services[^1];
        public DateTime Now => Clock.UtcNow.UtcDateTime;

        public async Task<Rig> StartAsync()
        {
            await Host.StartAsync();
            return this;
        }

        public Rig Build(bool queued = false, bool visible = true)
        {
            Vm = new DashboardViewModel(Host, queued ? Queue.Post : a => a(), Clock);
            Vm.PropertyChanged += (_, e) => Changed.Add(e.PropertyName);
            if (visible)
                Vm.SetViewVisible(true);
            return this;
        }

        public void Advance(TimeSpan by) => Clock.UtcNow += by;

        /// <summary>One freshness tick.</summary>
        public void Tick() => Clock.FireAll();

        public void Dispose() => Vm?.Dispose();
    }

    private static GameIdentification Identify(string kind, GameProcessInfo? process = null)
        => GameIdentityResolver.Resolve(IdentityTestData.OotCatalog(), IdentityEvidence.For(kind, process ?? Process100));

    private static NormalizedSnapshot Snapshot(DateTime received, ProcessFingerprint? fp = null, long frame = 1,
        int? life = 11, int? maxLife = 12, int? rupees = 42, string? map = "KokiriForest_Child", string? age = "child",
        bool? eventPlaying = false, bool? loading = false)
        => TestSnapshot.Create(fp ?? Fp100, rupees: rupees, life: life, maxLife: maxLife, map: map, age: age,
            eventPlaying: eventPlaying, loading: loading, gameFrame: frame, receivedUtc: received)
            .WithGameId(new GameId("zelda-ocarina-of-time"));

    // ---------------- attachment and monitoring state ----------------

    [Fact]
    public async Task Attaching_shows_the_current_state_immediately_and_subscribes()
    {
        var rig = await new Rig().StartAsync();
        rig.Service.State = MonitoringState.Monitoring; // no event raised
        using var _ = rig.Build(visible: false);

        Assert.Equal(MonitoringState.Monitoring, rig.Vm.MonitoringState);
        Assert.Equal(ConnectionStatus.Connected, rig.Vm.Connection);
        Assert.Equal(1, rig.Service.Subscribers);
    }

    [Fact]
    public void Without_a_service_the_dashboard_shows_stopped()
    {
        using var rig = new Rig().Build();

        Assert.Equal(MonitoringState.Stopped, rig.Vm.MonitoringState);
        Assert.Equal("Monitoring stopped", rig.Vm.StatusSummary);
        Assert.Equal(GameplayDataStatus.None, rig.Vm.GameplayData);
    }

    [Theory]
    [InlineData(MonitoringState.Stopped, ConnectionStatus.NotConnected, "Stopped", "Monitoring stopped")]
    [InlineData(MonitoringState.Searching, ConnectionStatus.NotConnected, "Searching for game", "Searching for Longshot")]
    [InlineData(MonitoringState.Connecting, ConnectionStatus.Connecting, "Connecting", "Connecting to Longshot")]
    [InlineData(MonitoringState.Monitoring, ConnectionStatus.Connected, "Monitoring", "Monitoring Longshot")]
    [InlineData(MonitoringState.Reconnecting, ConnectionStatus.Connecting, "Reconnecting", "Reconnecting to Longshot")]
    [InlineData(MonitoringState.Ambiguous, ConnectionStatus.NotConnected, "Multiple game processes found", "Multiple game processes found")]
    [InlineData(MonitoringState.Error, ConnectionStatus.NotConnected, "Error", "Monitoring error")]
    public async Task Every_state_is_presented(MonitoringState state, ConnectionStatus connection, string text, string summary)
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.Set(state == MonitoringState.Stopped ? MonitoringState.Searching : MonitoringState.Stopped);

        rig.Service.Set(state);

        Assert.Equal(state, rig.Vm.MonitoringState);
        Assert.Equal(text, rig.Vm.MonitoringStatusText);
        Assert.Equal(connection, rig.Vm.Connection);
        Assert.Equal(summary, rig.Vm.StatusSummary);
    }

    [Fact]
    public async Task Refresh_reads_the_service_not_the_event_argument()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.State = MonitoringState.Monitoring;

        rig.Service.RaiseStateChanged(MonitoringState.Searching); // stale argument

        Assert.Equal(MonitoringState.Monitoring, rig.Vm.MonitoringState);
    }

    [Fact]
    public async Task State_changes_still_update_while_the_window_is_hidden()
    {
        using var rig = (await new Rig().StartAsync()).Build(visible: false);

        rig.Service.Set(MonitoringState.Connecting);

        Assert.Equal("Connecting to Longshot", rig.Vm.StatusSummary);
    }

    // ---------------- identification ----------------

    [Fact]
    public async Task Identified_before_the_first_gameplay_snapshot()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.CurrentIdentification = Identify("identified");
        rig.Service.Set(MonitoringState.Monitoring);

        Assert.Equal(IdentificationDisplayStatus.Identified, rig.Vm.Identification);
        Assert.Equal(OotName, rig.Vm.GameDisplayName);
        Assert.Equal("zelda-ocarina-of-time", rig.Vm.GameId);
        Assert.Equal(OotName, rig.Vm.IdentificationText);
        Assert.Equal("Monitoring — " + OotName, rig.Vm.StatusSummary);
        Assert.Equal(GameplayDataStatus.Waiting, rig.Vm.GameplayData);
        Assert.Equal(ProcessStart, rig.Vm.ProcessStartedUtc); // from the identification evidence
    }

    [Theory]
    [InlineData("unknown", IdentificationDisplayStatus.Unknown, "Unrecognized game")]
    [InlineData("unsupported", IdentificationDisplayStatus.Unsupported, "Unsupported game or version")]
    [InlineData("conflict", IdentificationDisplayStatus.Conflict, "Game could not be identified")]
    public async Task Non_identified_results_never_expose_a_game(string kind, IdentificationDisplayStatus expected, string text)
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.CurrentIdentification = Identify(kind);
        rig.Service.Set(MonitoringState.Monitoring);

        Assert.Equal(expected, rig.Vm.Identification);
        Assert.Equal(text, rig.Vm.IdentificationText);
        Assert.Null(rig.Vm.GameDisplayName);
        Assert.Null(rig.Vm.GameId);
        Assert.DoesNotContain("Ocarina", rig.Vm.StatusSummary, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(rig.Vm.IdentificationDetail)); // diagnostics for a tooltip
    }

    [Fact]
    public async Task Unavailable_when_the_catalog_failed_to_load()
    {
        using var rig = (await new Rig(catalog: false).StartAsync()).Build();
        rig.Service.Set(MonitoringState.Monitoring);

        Assert.False(rig.Vm.CatalogAvailable);
        Assert.Equal(IdentificationDisplayStatus.Unavailable, rig.Vm.Identification);
        Assert.Equal("Game identification unavailable", rig.Vm.IdentificationText);
        Assert.Equal("The supported-game catalog could not be loaded.", rig.Vm.IdentificationDetail);
        Assert.Null(rig.Vm.GameDisplayName);
    }

    [Fact]
    public async Task Identifying_while_connecting_without_a_result()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.Set(MonitoringState.Connecting);

        Assert.Equal(IdentificationDisplayStatus.Identifying, rig.Vm.Identification);
        Assert.Equal("Identifying game…", rig.Vm.IdentificationText);
    }

    [Theory]
    [InlineData(MonitoringState.Searching)]
    [InlineData(MonitoringState.Stopped)]
    [InlineData(MonitoringState.Error)]
    [InlineData(MonitoringState.Ambiguous)]
    public async Task None_when_no_process_is_tracked(MonitoringState state)
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.Set(state);

        Assert.Equal(IdentificationDisplayStatus.None, rig.Vm.Identification);
        Assert.Equal("No game detected", rig.Vm.IdentificationText);
        Assert.Null(rig.Vm.IdentificationDetail);
    }

    // ---------------- session ----------------

    [Fact]
    public async Task Snapshot_fields_are_exposed_with_their_units()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        var id = SessionId.NewId();
        rig.Service.Session = new SessionSnapshot(id, Snapshot(rig.Now, frame: 4218), Identify("identified"), rig.Now);
        rig.Service.Set(MonitoringState.Monitoring);

        Assert.Equal(id.ToString(), rig.Vm.SessionId);
        Assert.Equal("zelda-ocarina-of-time", rig.Vm.SnapshotGameId);
        Assert.Equal("KokiriForest_Child", rig.Vm.Map);
        Assert.Equal("child", rig.Vm.Age);
        Assert.False(rig.Vm.EventPlaying);
        Assert.False(rig.Vm.Loading);
        Assert.Equal(42, rig.Vm.Rupees);
        Assert.Equal(4218, rig.Vm.GameFrame);
        Assert.Equal(11, rig.Vm.Life);       // raw quarter-hearts preserved
        Assert.Equal(12, rig.Vm.MaxLife);
        Assert.Equal("2¾ / 3 hearts", rig.Vm.HeartsText);
        Assert.Equal(ProcessStart, rig.Vm.ProcessStartedUtc);
    }

    [Fact]
    public async Task Missing_values_stay_null_rather_than_zero()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.Session = new SessionSnapshot(SessionId.NewId(),
            Snapshot(rig.Now, life: null, maxLife: null, rupees: null, map: null, age: null, eventPlaying: null, loading: null), null, rig.Now);
        rig.Service.Set(MonitoringState.Monitoring);

        Assert.Null(rig.Vm.Life);
        Assert.Null(rig.Vm.MaxLife);
        Assert.Null(rig.Vm.Rupees);
        Assert.Null(rig.Vm.Map);
        Assert.Null(rig.Vm.Age);
        Assert.Null(rig.Vm.EventPlaying);
        Assert.Null(rig.Vm.Loading);
        Assert.Equal("—", rig.Vm.HeartsText);
    }

    [Fact]
    public async Task Session_creation_replacement_and_ending()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.Set(MonitoringState.Monitoring);
        Assert.Null(rig.Vm.SessionId);
        Assert.Null(rig.Vm.SessionActiveSinceUtc);

        // Created.
        var start1 = rig.Now;
        var first = SessionId.NewId();
        rig.Service.Session = new SessionSnapshot(first, Snapshot(start1), null, start1);
        rig.Advance(TimeSpan.FromSeconds(65));
        rig.Tick();
        Assert.Equal(first.ToString(), rig.Vm.SessionId);
        Assert.Equal(start1, rig.Vm.SessionActiveSinceUtc);
        Assert.Equal("0:01:05", rig.Vm.SessionDurationText);

        // Replaced by a new process: new id, new start, new process start time.
        var laterStart = ProcessStart.AddMinutes(10);
        var start2 = rig.Now;
        var second = SessionId.NewId();
        rig.Service.Session = new SessionSnapshot(second, Snapshot(start2, new ProcessFingerprint(200, laterStart)), null, start2);
        rig.Tick();
        Assert.Equal(second.ToString(), rig.Vm.SessionId);
        Assert.Equal(start2, rig.Vm.SessionActiveSinceUtc);
        Assert.Equal("0:00:00", rig.Vm.SessionDurationText);
        Assert.Equal(laterStart, rig.Vm.ProcessStartedUtc);

        // Ended (the service returns to Searching with no session).
        rig.Service.Session = SessionSnapshot.None;
        rig.Service.Set(MonitoringState.Searching);
        Assert.Null(rig.Vm.SessionId);
        Assert.Null(rig.Vm.SessionActiveSinceUtc);
        Assert.Null(rig.Vm.SessionDurationText);
        Assert.Null(rig.Vm.Map);
        Assert.Equal(GameplayDataStatus.None, rig.Vm.GameplayData);
        Assert.Null(rig.Vm.ProcessStartedUtc);
    }

    [Fact]
    public async Task Same_process_reconnect_keeps_session_identification_and_data()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        var id = SessionId.NewId();
        var start = rig.Now;
        var identification = Identify("identified");
        rig.Service.CurrentIdentification = identification;
        rig.Service.Session = new SessionSnapshot(id, Snapshot(start), identification, start);
        rig.Service.Set(MonitoringState.Monitoring);

        rig.Service.Set(MonitoringState.Reconnecting);
        Assert.Equal(ConnectionStatus.Connecting, rig.Vm.Connection);
        Assert.Equal(id.ToString(), rig.Vm.SessionId);
        Assert.Equal(OotName, rig.Vm.GameDisplayName);
        Assert.Equal("KokiriForest_Child", rig.Vm.Map);
        Assert.Equal(GameplayDataStatus.Fresh, rig.Vm.GameplayData);

        rig.Service.Set(MonitoringState.Monitoring);
        Assert.Equal(id.ToString(), rig.Vm.SessionId);
        Assert.Equal(start, rig.Vm.SessionActiveSinceUtc);
    }

    [Fact]
    public async Task Ambiguous_retains_the_last_snapshot_and_identification()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        var identification = Identify("identified");
        rig.Service.CurrentIdentification = identification;
        rig.Service.Session = new SessionSnapshot(SessionId.NewId(), Snapshot(rig.Now), identification, rig.Now);
        rig.Service.Set(MonitoringState.Monitoring);

        rig.Service.Set(MonitoringState.Ambiguous);

        Assert.Equal(ConnectionStatus.NotConnected, rig.Vm.Connection);
        Assert.Equal(IdentificationDisplayStatus.Identified, rig.Vm.Identification);
        Assert.Equal(42, rig.Vm.Rupees);
        Assert.Equal(GameplayDataStatus.Fresh, rig.Vm.GameplayData);
    }

    [Fact]
    public async Task A_new_process_fingerprint_replaces_the_identification()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.CurrentIdentification = Identify("identified");
        rig.Service.Set(MonitoringState.Monitoring);
        Assert.Equal(OotName, rig.Vm.GameDisplayName);

        var other = new GameProcessInfo(200, GamePath, ProcessStart.AddMinutes(5));
        rig.Service.CurrentIdentification = Identify("unsupported", other);
        rig.Service.Set(MonitoringState.Connecting);

        Assert.Equal(IdentificationDisplayStatus.Unsupported, rig.Vm.Identification);
        Assert.Null(rig.Vm.GameDisplayName);
        Assert.Equal(ProcessStart.AddMinutes(5), rig.Vm.ProcessStartedUtc);
    }

    // ---------------- freshness ----------------

    [Fact]
    public async Task Waiting_when_connected_without_a_snapshot_and_none_otherwise()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.Set(MonitoringState.Connecting);
        Assert.Equal(GameplayDataStatus.None, rig.Vm.GameplayData);

        rig.Service.Set(MonitoringState.Monitoring);
        Assert.Equal(GameplayDataStatus.Waiting, rig.Vm.GameplayData);
        Assert.Equal("Waiting for gameplay data…", rig.Vm.FreshnessText);
        Assert.Equal(ConnectionStatus.Connected, rig.Vm.Connection); // waiting is not disconnected
        Assert.False(rig.Vm.IsStale);
        Assert.Null(rig.Vm.LastSnapshotUtc);
    }

    [Fact]
    public async Task Freshness_advances_with_the_timer_and_turns_stale_at_exactly_two_minutes()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        var received = rig.Now;
        rig.Service.Session = new SessionSnapshot(SessionId.NewId(), Snapshot(received), null, received);
        rig.Service.Set(MonitoringState.Monitoring);
        Assert.Equal(GameplayDataStatus.Fresh, rig.Vm.GameplayData);
        Assert.Equal(received, rig.Vm.LastSnapshotUtc);
        Assert.Equal("Last gameplay update just now", rig.Vm.FreshnessText);

        rig.Advance(TimeSpan.FromSeconds(12));
        rig.Tick();
        Assert.Equal("Last gameplay update 12 seconds ago", rig.Vm.FreshnessText);

        rig.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(12) - TimeSpan.FromTicks(1));
        rig.Tick();
        Assert.False(rig.Vm.IsStale);
        Assert.Null(rig.Vm.StaleHint);

        rig.Advance(TimeSpan.FromTicks(1)); // exactly two minutes
        rig.Tick();
        Assert.True(rig.Vm.IsStale);
        Assert.Equal(GameplayDataStatus.Stale, rig.Vm.GameplayData);
        Assert.Equal(DashboardFormat.StaleHint, rig.Vm.StaleHint);
        Assert.Equal("Last gameplay update 2 minutes ago", rig.Vm.FreshnessText);
        Assert.Equal(ConnectionStatus.Connected, rig.Vm.Connection); // stale is not a disconnection
    }

    [Fact]
    public async Task A_new_snapshot_clears_stale()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        var id = SessionId.NewId();
        var start = rig.Now;
        rig.Service.Session = new SessionSnapshot(id, Snapshot(start), null, start);
        rig.Service.Set(MonitoringState.Monitoring);
        rig.Advance(TimeSpan.FromMinutes(5));
        rig.Tick();
        Assert.True(rig.Vm.IsStale);

        rig.Service.Session = new SessionSnapshot(id, Snapshot(rig.Now, frame: 2), null, start);
        rig.Tick();

        Assert.False(rig.Vm.IsStale);
        Assert.Null(rig.Vm.StaleHint);
        Assert.Equal(GameplayDataStatus.Fresh, rig.Vm.GameplayData);
        Assert.Equal(2, rig.Vm.GameFrame);
    }

    // ---------------- service replacement ----------------

    [Fact]
    public async Task Service_replacement_moves_the_subscription_and_ignores_the_old_service()
    {
        using var rig = (await new Rig().StartAsync()).Build(queued: true);
        rig.Queue.RunAll();
        var old = rig.Service;
        old.Set(MonitoringState.Error);
        rig.Queue.RunAll();
        Assert.Equal(MonitoringState.Error, rig.Vm.MonitoringState);

        Assert.True(await rig.Host.RestartAsync());
        rig.Queue.RunAll();
        var replacement = rig.Service;
        Assert.NotSame(old, replacement);
        Assert.Equal(0, old.Subscribers);
        Assert.Equal(1, replacement.Subscribers);

        replacement.Set(MonitoringState.Searching);
        rig.Queue.RunAll();
        Assert.Equal(MonitoringState.Searching, rig.Vm.MonitoringState);

        old.Set(MonitoringState.Monitoring); // late event from the old service
        Assert.Equal(0, rig.Queue.Pending);
        Assert.Equal(MonitoringState.Searching, rig.Vm.MonitoringState);
    }

    [Fact]
    public async Task Stopping_the_host_shows_stopped()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.Set(MonitoringState.Monitoring);
        var old = rig.Service;

        Assert.True(await rig.Host.StopAsync());

        Assert.Equal(MonitoringState.Stopped, rig.Vm.MonitoringState);
        Assert.Equal(0, old.Subscribers);
    }

    // ---------------- posting, coalescing, disposal ----------------

    [Fact]
    public async Task Bursts_of_events_are_coalesced_into_one_ui_callback()
    {
        using var rig = (await new Rig().StartAsync()).Build(queued: true, visible: false);
        rig.Queue.RunAll();

        rig.Service.Set(MonitoringState.Searching);
        rig.Service.Set(MonitoringState.Connecting);
        rig.Service.Set(MonitoringState.Monitoring);
        rig.Service.Set(MonitoringState.Reconnecting);
        Assert.Equal(1, rig.Queue.Pending);
        Assert.Equal(MonitoringState.Stopped, rig.Vm.MonitoringState); // nothing applied off the UI thread

        Assert.Equal(1, rig.Queue.RunAll());
        Assert.Equal(MonitoringState.Reconnecting, rig.Vm.MonitoringState);

        rig.Service.Set(MonitoringState.Monitoring); // after the callback ran, a new one is queued
        Assert.Equal(1, rig.Queue.Pending);
        rig.Queue.RunAll();
        Assert.Equal(MonitoringState.Monitoring, rig.Vm.MonitoringState);
    }

    [Fact]
    public async Task A_queued_callback_does_nothing_after_disposal()
    {
        var rig = (await new Rig().StartAsync()).Build(queued: true, visible: false);
        rig.Queue.RunAll();
        rig.Service.Set(MonitoringState.Monitoring);
        Assert.Equal(1, rig.Queue.Pending);

        rig.Vm.Dispose();
        rig.Changed.Clear();
        rig.Queue.RunAll();

        Assert.Empty(rig.Changed);
        Assert.Equal(MonitoringState.Stopped, rig.Vm.MonitoringState);
    }

    [Fact]
    public async Task Disposal_unsubscribes_disposes_the_timer_and_is_idempotent()
    {
        var rig = (await new Rig().StartAsync()).Build();
        var service = rig.Service;
        Assert.Equal(1, service.Subscribers);

        rig.Vm.Dispose();
        rig.Vm.Dispose();
        rig.Changed.Clear();

        Assert.Equal(0, service.Subscribers);
        Assert.Empty(rig.Clock.ActiveDueTimes); // timer disposed
        service.Set(MonitoringState.Monitoring);
        Assert.True(await rig.Host.RestartAsync()); // no ServiceChanged handling either
        rig.Vm.SetViewVisible(true);
        Assert.Empty(rig.Changed);
        Assert.Equal(0, rig.Service.Subscribers);
    }

    // ---------------- timer and visibility ----------------

    [Fact]
    public async Task The_timer_is_paused_while_hidden_and_refreshes_immediately_when_shown()
    {
        using var rig = (await new Rig().StartAsync()).Build(visible: false);
        Assert.Equal(new[] { Timeout.InfiniteTimeSpan }, rig.Clock.ActiveDueTimes); // created paused

        rig.Vm.SetViewVisible(true);
        Assert.True(rig.Vm.IsViewVisible);
        Assert.Equal(new[] { DashboardViewModel.TickInterval }, rig.Clock.ActiveDueTimes);

        rig.Vm.SetViewVisible(false);
        Assert.Equal(new[] { Timeout.InfiniteTimeSpan }, rig.Clock.ActiveDueTimes);

        // While hidden, a new snapshot (no state event) is not picked up by ticks...
        rig.Service.State = MonitoringState.Monitoring;
        rig.Service.Session = new SessionSnapshot(SessionId.NewId(), Snapshot(rig.Now, rupees: 7), null, rig.Now);
        rig.Tick();
        Assert.Null(rig.Vm.Rupees);

        // ...but showing the window refreshes at once.
        rig.Vm.SetViewVisible(true);
        Assert.Equal(7, rig.Vm.Rupees);
        Assert.Equal(MonitoringState.Monitoring, rig.Vm.MonitoringState);
    }

    [Fact]
    public async Task Ticks_raise_property_changes_only_for_values_that_changed()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        rig.Service.Session = new SessionSnapshot(SessionId.NewId(), Snapshot(rig.Now), null, rig.Now);
        rig.Service.Set(MonitoringState.Monitoring);
        rig.Advance(TimeSpan.FromSeconds(5));
        rig.Changed.Clear();

        rig.Advance(TimeSpan.FromSeconds(1));
        rig.Tick();

        Assert.Equal(new[] { nameof(DashboardViewModel.SessionDurationText), nameof(DashboardViewModel.FreshnessText) }.Order(),
            rig.Changed.Order());
    }

    [Fact]
    public async Task MainViewModel_exposes_the_dashboard()
    {
        using var rig = (await new Rig().StartAsync()).Build();
        var dir = Path.Combine(Path.GetTempPath(), "sa-dashboard-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var main = new MainViewModel(new App.Infrastructure.Settings.SettingsService(dir), rig.Vm);
            Assert.Same(rig.Vm, main.Dashboard);
            Assert.Null(new MainViewModel(new App.Infrastructure.Settings.SettingsService(dir)).Dashboard);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

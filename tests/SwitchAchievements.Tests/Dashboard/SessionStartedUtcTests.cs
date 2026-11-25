using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Fakes;

namespace SwitchAchievements.Tests.Dashboard;

/// <summary>
/// M5.7 Task 2: SessionSnapshot.StartedUtc on the real MonitoringService. Waits are for
/// stable conditions only (a stable state via the transition observer, or a specific
/// snapshot frame becoming the latest), following the existing session-test convention.
/// </summary>
public class SessionStartedUtcTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private static readonly DateTime StartUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ProcessFingerprint Fp100 = new(100, StartUtc);

    private sealed class Rig : IDisposable
    {
        public FakeGameIntegration Integration { get; } = new();
        public FakeProcessProvider Processes { get; } = new();
        public MonitoringService Service { get; }
        public StateTransitionObserver Observer { get; }

        public Rig()
        {
            Service = new MonitoringService(Integration, Processes, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(20));
            Observer = new StateTransitionObserver(Service);
        }

        public async Task StartMonitoringAsync(params GameProcessInfo[] processes)
        {
            Processes.Processes = processes;
            await Service.StartAsync();
            await Observer.WaitForAsync(MonitoringState.Monitoring, after: 0);
        }

        /// <summary>Raises a snapshot and waits until it is the session's latest (identified by frame).</summary>
        public async Task<SessionSnapshot> SnapshotAsync(ProcessFingerprint fp, long frame, DateTime received)
        {
            Integration.RaiseSnapshot(TestSnapshot.Create(fp, gameFrame: frame, receivedUtc: received));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Service.Session.Latest?.GameFrame != frame || Service.Session.Latest.Fingerprint != fp)
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail($"snapshot frame {frame} was not processed");
                await Task.Delay(10);
            }
            return Service.Session;
        }

        public void Dispose() => Observer.Dispose();
    }

    private static GameProcessInfo Game(int pid, DateTime start) => new(pid, GamePath, start);

    [Fact]
    public async Task A_new_session_starts_at_its_first_snapshot()
    {
        using var rig = new Rig();
        await rig.StartMonitoringAsync(Game(100, StartUtc));
        Assert.Null(rig.Service.Session.StartedUtc); // no session before the first snapshot

        var session = await rig.SnapshotAsync(Fp100, frame: 1, received: T);

        Assert.NotNull(session.Id);
        Assert.Equal(T, session.StartedUtc);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task The_start_time_is_kept_for_the_whole_session()
    {
        using var rig = new Rig();
        await rig.StartMonitoringAsync(Game(100, StartUtc));
        var first = await rig.SnapshotAsync(Fp100, 1, T);

        var later = await rig.SnapshotAsync(Fp100, 2, T.AddMinutes(10));

        Assert.Equal(first.Id, later.Id);
        Assert.Equal(T, later.StartedUtc);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task A_same_process_reconnect_keeps_the_session_and_its_start_time()
    {
        using var rig = new Rig();
        await rig.StartMonitoringAsync(Game(100, StartUtc));
        var first = await rig.SnapshotAsync(Fp100, 1, T);

        var mark = rig.Observer.Count;
        rig.Integration.RaiseDisconnected();
        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: mark);
        Assert.Equal(T, rig.Service.Session.StartedUtc); // kept while reconnecting and after

        var resumed = await rig.SnapshotAsync(Fp100, 2, T.AddMinutes(1));
        Assert.Equal(first.Id, resumed.Id);
        Assert.Equal(T, resumed.StartedUtc);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task A_new_process_gets_a_new_session_and_start_time()
    {
        using var rig = new Rig();
        var laterStart = StartUtc.AddMinutes(5);
        await rig.StartMonitoringAsync(Game(100, StartUtc));
        var first = await rig.SnapshotAsync(Fp100, 1, T);

        rig.Processes.Processes = new[] { Game(200, laterStart) }; // arranged before the disconnect
        var mark = rig.Observer.Count;
        rig.Integration.RaiseDisconnected();
        await rig.Observer.WaitForAsync(MonitoringState.Monitoring, after: mark);
        var replaced = await rig.SnapshotAsync(new ProcessFingerprint(200, laterStart), 1, T.AddMinutes(3));

        Assert.NotEqual(first.Id, replaced.Id);
        Assert.Equal(T.AddMinutes(3), replaced.StartedUtc);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Ending_the_session_clears_the_start_time()
    {
        using var rig = new Rig();
        await rig.StartMonitoringAsync(Game(100, StartUtc));
        await rig.SnapshotAsync(Fp100, 1, T);

        rig.Processes.Processes = Array.Empty<GameProcessInfo>(); // gone, arranged before the disconnect
        var mark = rig.Observer.Count;
        rig.Integration.RaiseDisconnected();
        await rig.Observer.WaitForAsync(MonitoringState.Searching, after: mark);

        Assert.Same(SessionSnapshot.None, rig.Service.Session);
        Assert.Null(rig.Service.Session.StartedUtc);
        await rig.Service.StopAsync();
    }

    [Fact]
    public void Existing_constructions_default_to_no_start_time()
    {
        Assert.Null(SessionSnapshot.None.StartedUtc);
        Assert.Null(new SessionSnapshot(SessionId.NewId(), null).StartedUtc);
    }
}

using System.Collections.Concurrent;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Fakes;
using SwitchAchievements.Tests.Identity;

namespace SwitchAchievements.Tests.MonitoringIdentity;

/// <summary>
/// M5.7 Task 1: MonitoringService.CurrentIdentification. Values are captured inside the
/// StateChanged handler, so each is exactly what a consumer sees at that transition.
/// </summary>
public class CurrentIdentificationTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private static GameProcessInfo Game(int pid, DateTime? start = null) => new(pid, GamePath, start ?? T0);

    private sealed class Rig
    {
        public FakeGameIntegration Integration { get; } = new();
        public FakeProcessProvider Processes { get; } = new();
        public FakeIdentityProvider Provider { get; init; } = new();
        public MonitoringService Service { get; private set; } = null!;
        public ConcurrentQueue<(MonitoringState State, GameIdentification? Identification, NormalizedSnapshot? Latest)> Seen { get; } = new();
        private readonly ConcurrentDictionary<(MonitoringState, int), TaskCompletionSource<GameIdentification?>> _waiters = new();
        private int _count;

        public Rig Build(bool withIdentifier = true, bool withCatalog = true)
        {
            Service = new MonitoringService(Integration, Processes, new CapturingLogger(),
                requiredStableObservations: 2, pollInterval: TimeSpan.FromMilliseconds(20),
                identifier: withIdentifier ? new GameIdentifier(Provider, withCatalog ? IdentityTestData.OotCatalog() : null) : null);
            Service.StateChanged += state =>
            {
                var identification = Service.CurrentIdentification;
                Seen.Enqueue((state, identification, Service.Session.Latest));
                var occurrence = Interlocked.Increment(ref _count);
                foreach (var ((target, after), tcs) in _waiters)
                    if (target == state && occurrence > after)
                        tcs.TrySetResult(identification);
            };
            return this;
        }

        /// <summary>The identification seen at the next transition to <paramref name="state"/> (after those already recorded).</summary>
        public Task<GameIdentification?> Next(MonitoringState state)
        {
            var tcs = new TaskCompletionSource<GameIdentification?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters[(state, Volatile.Read(ref _count))] = tcs;
            return tcs.Task.WaitAsync(WaitLimit);
        }
    }

    [Fact]
    public async Task Identified_result_is_available_before_Monitoring_is_announced()
    {
        var rig = new Rig().Build();
        rig.Processes.Processes = new[] { Game(100) };
        var monitoring = rig.Next(MonitoringState.Monitoring);

        await rig.Service.StartAsync();
        var seen = await monitoring;

        Assert.NotNull(seen);
        Assert.True(seen.IsIdentified);
        Assert.Equal("zelda-ocarina-of-time", seen.Game!.GameId.Value);
        Assert.Equal(new ProcessFingerprint(100, T0), seen.Game.Process);
        var atMonitoring = rig.Seen.Single(s => s.State == MonitoringState.Monitoring);
        Assert.Null(atMonitoring.Latest);                       // before any gameplay snapshot
        Assert.Same(seen, rig.Service.CurrentIdentification);
        await rig.Service.StopAsync();
    }

    [Theory]
    [InlineData("unknown", IdentificationStatus.Unknown)]
    [InlineData("unsupported", IdentificationStatus.Unsupported)]
    [InlineData("conflict", IdentificationStatus.Conflict)]
    public async Task Non_identified_results_are_exposed_without_a_game(string kind, IdentificationStatus status)
    {
        var rig = new Rig { Provider = new FakeIdentityProvider { Respond = p => IdentityEvidence.For(kind, p) } }.Build();
        rig.Processes.Processes = new[] { Game(100) };
        var monitoring = rig.Next(MonitoringState.Monitoring);

        await rig.Service.StartAsync();
        var seen = await monitoring;

        Assert.Equal(status, seen!.Status);
        Assert.Null(seen.Game);
        Assert.False(seen.IsIdentified);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Cleared_on_Searching_when_the_process_is_gone()
    {
        var rig = new Rig().Build();
        rig.Processes.Processes = new[] { Game(100) };
        var monitoring = rig.Next(MonitoringState.Monitoring);
        await rig.Service.StartAsync();
        Assert.NotNull(await monitoring);

        rig.Processes.Processes = Array.Empty<GameProcessInfo>(); // gone, arranged before the disconnect
        var reconnecting = rig.Next(MonitoringState.Reconnecting);
        var searching = rig.Next(MonitoringState.Searching);
        rig.Integration.RaiseDisconnected();

        Assert.NotNull(await reconnecting);                      // still tracked while reconnecting
        Assert.Null(await searching);
        Assert.Null(rig.Service.CurrentIdentification);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Cleared_on_Error()
    {
        var rig = new Rig().Build();
        rig.Integration.OnConnect = (_, _) => throw new ApplicationException("connect bug"); // after identification
        rig.Processes.Processes = new[] { Game(100) };
        var error = rig.Next(MonitoringState.Error);

        await rig.Service.StartAsync();

        Assert.Null(await error);
        Assert.Single(rig.Provider.Calls);                       // it had been identified
        Assert.Null(rig.Service.CurrentIdentification);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Cleared_on_Stopped()
    {
        var rig = new Rig().Build();
        rig.Processes.Processes = new[] { Game(100) };
        var monitoring = rig.Next(MonitoringState.Monitoring);
        await rig.Service.StartAsync();
        Assert.NotNull(await monitoring);

        var stopped = rig.Next(MonitoringState.Stopped);
        await rig.Service.StopAsync();

        Assert.Null(await stopped);
        Assert.Null(rig.Service.CurrentIdentification);
    }

    [Fact]
    public async Task Same_process_reconnect_preserves_the_identification()
    {
        var rig = new Rig().Build();
        rig.Processes.Processes = new[] { Game(100) };
        var first = rig.Next(MonitoringState.Monitoring);
        await rig.Service.StartAsync();
        var original = await first;

        var reconnecting = rig.Next(MonitoringState.Reconnecting);
        var again = rig.Next(MonitoringState.Monitoring);
        rig.Integration.RaiseDisconnected();

        Assert.Same(original, await reconnecting);
        Assert.Same(original, await again);
        Assert.Single(rig.Provider.Calls);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task A_different_process_replaces_the_identification()
    {
        var rig = new Rig { Provider = new FakeIdentityProvider { Respond = p => p.ProcessId == 100 ? IdentityEvidence.Oot(p) : IdentityEvidence.UnsupportedBuild(p) } }.Build();
        rig.Processes.Processes = new[] { Game(100) };
        var first = rig.Next(MonitoringState.Monitoring);
        await rig.Service.StartAsync();
        Assert.True((await first)!.IsIdentified);

        rig.Processes.Processes = new[] { Game(200, T0.AddMinutes(5)) }; // restarted, arranged before the disconnect
        var second = rig.Next(MonitoringState.Monitoring);
        rig.Integration.RaiseDisconnected();
        var replaced = await second;

        Assert.Equal(IdentificationStatus.Unsupported, replaced!.Status);
        Assert.Equal(new ProcessFingerprint(200, T0.AddMinutes(5)), replaced.Evidence.Process);
        Assert.Null(replaced.Game);
        await rig.Service.StopAsync();
    }

    [Theory]
    [InlineData(false, true)]   // no identifier
    [InlineData(true, false)]   // identifier without a catalog (unavailable)
    public async Task Null_when_identification_is_not_performed(bool withIdentifier, bool withCatalog)
    {
        var rig = new Rig().Build(withIdentifier, withCatalog);
        rig.Processes.Processes = new[] { Game(100) };
        var monitoring = rig.Next(MonitoringState.Monitoring);

        await rig.Service.StartAsync();

        Assert.Null(await monitoring);
        Assert.Null(rig.Service.CurrentIdentification);
        await rig.Service.StopAsync();
    }

    [Fact]
    public async Task Snapshot_and_session_identification_are_unchanged()
    {
        var rig = new Rig().Build();
        var process = Game(100);
        rig.Processes.Processes = new[] { process };
        var monitoring = rig.Next(MonitoringState.Monitoring);
        await rig.Service.StartAsync();
        var current = await monitoring;

        rig.Integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, T0), gameFrame: 1));
        var deadline = DateTime.UtcNow + WaitLimit;
        while (rig.Service.Session.Latest is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Same(current, rig.Service.Session.Identification);
        Assert.Equal("zelda-ocarina-of-time", rig.Service.Session.Latest!.GameId.Value);
        await rig.Service.StopAsync();
    }
}

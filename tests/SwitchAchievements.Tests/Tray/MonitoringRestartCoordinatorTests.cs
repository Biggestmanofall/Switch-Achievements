using SwitchAchievements.App.Services;
using SwitchAchievements.Tests.AppLifecycle;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.Tray;

/// <summary>M5.7 Task 4: tray restart through MonitoringHost.RestartAsync and failed-restart recovery.</summary>
public class MonitoringRestartCoordinatorTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private sealed class Rig
    {
        public CapturingLogger Logger { get; } = new();
        public RunningCounter Running { get; } = new();
        public List<FakeMonitoringService> Created { get; } = new();

        /// <summary>Configures each service by creation index (0 = the first).</summary>
        public Action<FakeMonitoringService, int>? Configure { get; set; }
        public bool ShuttingDown { get; set; }
        public int ChangedCount { get; private set; }
        public MonitoringHost Host { get; }
        public MonitoringRestartCoordinator Restart { get; }

        public Rig()
        {
            Host = new MonitoringHost(Logger,
                serviceFactory: catalog =>
                {
                    var s = new FakeMonitoringService(catalog, Running);
                    lock (Created)
                    {
                        Configure?.Invoke(s, Created.Count);
                        Created.Add(s);
                    }
                    return s;
                },
                catalogLoader: () => IdentityTestData.OotCatalog(),
                stopTimeout: TimeSpan.FromSeconds(2));
            Restart = new MonitoringRestartCoordinator(Host, () => ShuttingDown, Logger);
            Restart.Changed += () => ChangedCount++;
        }
    }

    [Fact]
    public async Task Successful_restart_replaces_the_service()
    {
        var rig = new Rig();
        await rig.Host.StartAsync();
        var old = rig.Host.Service;

        var outcome = await rig.Restart.RestartAsync();

        Assert.Equal(RestartOutcome.Succeeded, outcome);
        Assert.NotNull(rig.Host.Service);
        Assert.NotSame(old, rig.Host.Service);
        Assert.True(rig.Created[0].IsStopped);
        Assert.False(rig.Restart.IsRunning);
        Assert.False(rig.Restart.RecoveryRequired);
        Assert.Equal(2, rig.ChangedCount); // running, then finished
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Restart_refused_because_the_old_service_could_not_stop_keeps_it_and_allows_another_attempt()
    {
        var rig = new Rig { Configure = (s, i) => { if (i == 0) s.StopException = new InvalidOperationException("stuck"); } };
        await rig.Host.StartAsync();
        var old = rig.Host.Service;

        Assert.Equal(RestartOutcome.Refused, await rig.Restart.RestartAsync());
        Assert.Same(old, rig.Host.Service); // kept (still in Error in production)
        Assert.False(rig.Restart.RecoveryRequired);
        Assert.False(rig.Restart.IsRunning);

        // Another attempt is possible (and refused again here, since the fake stays stuck).
        Assert.Equal(RestartOutcome.Refused, await rig.Restart.RestartAsync());
        Assert.Single(rig.Created);
    }

    [Fact]
    public async Task Replacement_start_failure_enters_recovery_and_a_successful_retry_clears_it()
    {
        var rig = new Rig { Configure = (s, i) => { if (i == 1) s.StartException = new InvalidOperationException("compose failed"); } };
        await rig.Host.StartAsync();

        Assert.Equal(RestartOutcome.Failed, await rig.Restart.RestartAsync());
        Assert.Null(rig.Host.Service); // the dashboard truthfully shows Stopped
        Assert.True(rig.Restart.RecoveryRequired);
        Assert.False(rig.Restart.IsRunning);
        Assert.Equal(1, rig.Logger.Count("monitoring restart failed"));

        var view = TrayStatus.Compute(MonitoringState.Stopped, "Monitoring stopped", rig.Restart.IsRunning, rig.Restart.RecoveryRequired);
        Assert.Equal(TrayStatus.RestartFailedSummary, view.StatusText);
        Assert.True(view.RestartEnabled);

        Assert.Equal(RestartOutcome.Succeeded, await rig.Restart.RestartAsync());
        Assert.NotNull(rig.Host.Service);
        Assert.False(rig.Restart.RecoveryRequired);
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Factory_failure_enters_recovery()
    {
        var calls = 0;
        var logger = new CapturingLogger();
        var host = new MonitoringHost(logger,
            serviceFactory: catalog => ++calls == 2
                ? throw new InvalidOperationException("factory failed")
                : new FakeMonitoringService(catalog, new RunningCounter()),
            catalogLoader: () => IdentityTestData.OotCatalog());
        var restart = new MonitoringRestartCoordinator(host, () => false, logger);
        await host.StartAsync();

        Assert.Equal(RestartOutcome.Failed, await restart.RestartAsync());
        Assert.True(restart.RecoveryRequired);
        Assert.Null(host.Service);

        Assert.Equal(RestartOutcome.Succeeded, await restart.RestartAsync());
        Assert.False(restart.RecoveryRequired);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Duplicate_requests_while_running_start_only_one_attempt()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new Rig { Configure = (s, i) => { if (i == 0) s.StopGate = gate; } };
        await rig.Host.StartAsync();

        var first = rig.Restart.RestartAsync();
        Assert.True(rig.Restart.IsRunning);
        var duplicate = await rig.Restart.RestartAsync();
        Assert.Equal(RestartOutcome.Ignored, duplicate);

        var view = TrayStatus.Compute(MonitoringState.Error, "Monitoring error", rig.Restart.IsRunning, rig.Restart.RecoveryRequired);
        Assert.True(view.RestartVisible);
        Assert.False(view.RestartEnabled);

        await rig.Created[0].StopStarted.Task.WaitAsync(WaitLimit);
        gate.SetResult();
        Assert.Equal(RestartOutcome.Succeeded, await first.WaitAsync(WaitLimit));
        Assert.Equal(2, rig.Created.Count); // exactly one replacement
        Assert.Equal(1, rig.Created[0].StopCalls);
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Restart_during_shutdown_is_ignored_without_touching_monitoring()
    {
        var rig = new Rig();
        await rig.Host.StartAsync();
        var service = rig.Host.Service;
        rig.ShuttingDown = true;

        Assert.Equal(RestartOutcome.Ignored, await rig.Restart.RestartAsync());
        Assert.Same(service, rig.Host.Service);
        Assert.Equal(0, rig.ChangedCount);
        Assert.Single(rig.Created);
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Host_disposed_by_an_exit_in_progress_is_ignored_safely()
    {
        var rig = new Rig();
        await rig.Host.StartAsync();

        // The exit disposes the host after the restart was requested but before it ran.
        await rig.Host.DisposeAsync();
        var checks = 0;
        var restart = new MonitoringRestartCoordinator(rig.Host, () => checks++ > 0, rig.Logger);

        Assert.Equal(RestartOutcome.Ignored, await restart.RestartAsync()); // ObjectDisposedException swallowed
        Assert.False(restart.RecoveryRequired);
        Assert.False(restart.IsRunning);
        Assert.Equal(0, rig.Logger.Count("monitoring restart failed"));
    }

    [Fact]
    public async Task A_throwing_changed_handler_does_not_break_the_restart()
    {
        var rig = new Rig();
        await rig.Host.StartAsync();
        rig.Restart.Changed += () => throw new InvalidOperationException("ui gone");

        Assert.Equal(RestartOutcome.Succeeded, await rig.Restart.RestartAsync());
        Assert.False(rig.Restart.IsRunning);
        await rig.Host.DisposeAsync();
    }
}

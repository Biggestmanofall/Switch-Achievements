using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Fakes;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.LongshotIdentity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.AppLifecycle;

/// <summary>M5.7 Task 1: MonitoringHost ownership, start/stop/restart semantics, and bounds.</summary>
public class MonitoringHostTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private sealed class Rig
    {
        public CapturingLogger Logger { get; } = new();
        public RunningCounter Running { get; } = new();
        public List<FakeMonitoringService> Created { get; } = new();
        public List<IMonitoringService?> Changes { get; } = new();
        public Action<FakeMonitoringService, int>? Configure { get; set; }
        public int CatalogLoads { get; private set; }
        public MonitoringHost Host { get; private set; } = null!;

        public Rig Build(bool catalogLoads = true, TimeProvider? time = null, TimeSpan? stopTimeout = null)
        {
            Host = new MonitoringHost(
                Logger,
                serviceFactory: catalog =>
                {
                    var service = new FakeMonitoringService(catalog, Running);
                    lock (Created)
                    {
                        Configure?.Invoke(service, Created.Count);
                        Created.Add(service);
                    }
                    return service;
                },
                catalogLoader: () => { CatalogLoads++; return catalogLoads ? IdentityTestData.OotCatalog() : null; },
                stopTimeout: stopTimeout,
                timeProvider: time);
            Host.ServiceChanged += s => { lock (Changes) Changes.Add(s); };
            return this;
        }
    }

    // ---------------- catalog ----------------

    [Fact]
    public async Task Catalog_is_loaded_once_and_reused_by_every_service()
    {
        var rig = new Rig().Build();
        Assert.Equal(1, rig.CatalogLoads);
        Assert.True(rig.Host.CatalogAvailable);

        await rig.Host.StartAsync();
        Assert.True(await rig.Host.RestartAsync());
        Assert.True(await rig.Host.RestartAsync());

        Assert.Equal(1, rig.CatalogLoads);
        Assert.Equal(3, rig.Created.Count);
        Assert.All(rig.Created, s => Assert.Same(rig.Host.Catalog, s.Catalog));
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Catalog_failure_still_starts_monitoring_with_identification_unavailable()
    {
        var rig = new Rig().Build(catalogLoads: false);

        Assert.False(rig.Host.CatalogAvailable);
        Assert.Null(rig.Host.Catalog);
        Assert.Contains(rig.Logger.Lines, l => l.Contains("game identification unavailable", StringComparison.Ordinal));

        await rig.Host.StartAsync();
        var service = Assert.Single(rig.Created);
        Assert.True(service.IsRunning);
        Assert.Null(service.Catalog);
        await rig.Host.DisposeAsync();
    }

    // ---------------- start ----------------

    [Fact]
    public async Task Start_composes_and_starts_exactly_one_service()
    {
        var rig = new Rig().Build();

        await rig.Host.StartAsync();
        await rig.Host.StartAsync(); // no-op: already running

        var service = Assert.Single(rig.Created);
        Assert.Equal(1, service.StartCalls);
        Assert.Same(service, rig.Host.Service);
        Assert.Equal(new IMonitoringService?[] { service }, rig.Changes);
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Start_failure_propagates_and_leaves_no_service()
    {
        var rig = new Rig { Configure = (s, _) => s.StartException = new InvalidOperationException("boom") }.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Host.StartAsync());

        Assert.Null(rig.Host.Service);
        Assert.Equal(1, Assert.Single(rig.Created).DisposeCalls);
        Assert.Empty(rig.Changes);
    }

    // ---------------- restart ----------------

    [Fact]
    public async Task Restart_disposes_the_previous_service_and_starts_a_fresh_one()
    {
        var rig = new Rig().Build();
        await rig.Host.StartAsync();

        Assert.True(await rig.Host.RestartAsync());

        Assert.Equal(2, rig.Created.Count);
        Assert.True(rig.Created[0].IsStopped);
        Assert.Equal(1, rig.Created[0].DisposeCalls);
        Assert.True(rig.Created[1].IsRunning);
        Assert.Same(rig.Created[1], rig.Host.Service);
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Restart_publishes_ServiceChanged_in_order()
    {
        var rig = new Rig().Build();
        await rig.Host.StartAsync();
        await rig.Host.RestartAsync();

        Assert.Equal(new IMonitoringService?[] { rig.Created[0], null, rig.Created[1] }, rig.Changes);
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Overlapping_restarts_never_run_two_services_at_once()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new Rig { Configure = (s, i) => { if (i == 0) s.StopGate = gate; } }.Build();
        await rig.Host.StartAsync();

        var first = rig.Host.RestartAsync();
        var second = rig.Host.RestartAsync();
        await rig.Created[0].StopStarted.Task.WaitAsync(WaitLimit);
        Assert.Single(rig.Created); // nothing new while the old service is still stopping
        gate.SetResult();

        Assert.True(await first.WaitAsync(WaitLimit));
        Assert.True(await second.WaitAsync(WaitLimit));

        Assert.Equal(3, rig.Created.Count);
        Assert.Equal(1, rig.Running.Max);
        Assert.Equal(1, rig.Running.Current);
        Assert.Same(rig.Created[2], rig.Host.Service);
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Restart_is_refused_when_the_previous_service_does_not_stop_in_time()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new ManualTimeProvider();
        var rig = new Rig { Configure = (s, i) => { if (i == 0) s.StopGate = gate; } }.Build(time: time);
        await rig.Host.StartAsync();

        var restart = rig.Host.RestartAsync();
        await rig.Created[0].StopStarted.Task.WaitAsync(WaitLimit);
        time.FireAll(); // the 5 s stop bound elapses

        Assert.False(await restart.WaitAsync(WaitLimit));
        Assert.Single(rig.Created);                       // no second service started
        Assert.Same(rig.Created[0], rig.Host.Service);    // the possibly-active service is kept
        Assert.Contains(rig.Logger.Lines, l => l.Contains("did not stop within 5 s", StringComparison.Ordinal));
        Assert.Contains(rig.Logger.Lines, l => l.Contains("monitoring restart refused", StringComparison.Ordinal));

        // Once the old service finally stops, a restart succeeds.
        gate.SetResult();
        Assert.True(await rig.Host.RestartAsync().WaitAsync(WaitLimit));
        Assert.Equal(2, rig.Created.Count);
        Assert.Equal(1, rig.Running.Max);
        await rig.Host.DisposeAsync();
    }

    [Fact]
    public async Task Restart_is_refused_when_the_previous_service_fails_to_stop()
    {
        var rig = new Rig { Configure = (s, i) => { if (i == 0) s.StopException = new InvalidOperationException("stuck"); } }.Build();
        await rig.Host.StartAsync();

        Assert.False(await rig.Host.RestartAsync());
        Assert.Single(rig.Created);
        Assert.Same(rig.Created[0], rig.Host.Service);
        Assert.Contains(rig.Logger.Lines, l => l.Contains("monitoring failed to stop: InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_failure_of_the_new_service_propagates_and_the_host_can_start_again()
    {
        var rig = new Rig { Configure = (s, i) => { if (i == 1) s.StartException = new InvalidOperationException("compose failed"); } }.Build();
        await rig.Host.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Host.RestartAsync());
        Assert.Null(rig.Host.Service);
        Assert.Equal(1, rig.Created[1].DisposeCalls);

        await rig.Host.StartAsync();
        Assert.Same(rig.Created[2], rig.Host.Service);
        Assert.Equal(1, rig.Running.Current);
        await rig.Host.DisposeAsync();
    }

    // ---------------- stop and dispose ----------------

    [Fact]
    public async Task Stop_when_never_started_is_safe()
    {
        var rig = new Rig().Build();
        Assert.True(await rig.Host.StopAsync());
        Assert.True(await rig.Host.StopAsync());
        Assert.Empty(rig.Created);
        Assert.Empty(rig.Changes);
    }

    [Fact]
    public async Task Repeated_stops_stop_the_service_once()
    {
        var rig = new Rig().Build();
        await rig.Host.StartAsync();

        Assert.True(await rig.Host.StopAsync());
        Assert.True(await rig.Host.StopAsync());

        var service = Assert.Single(rig.Created);
        Assert.Equal(1, service.StopCalls);
        Assert.True(service.IsStopped);
        Assert.Null(rig.Host.Service);
    }

    [Fact]
    public async Task Stop_is_bounded_and_keeps_a_service_that_did_not_stop()
    {
        var time = new ManualTimeProvider();
        var rig = new Rig { Configure = (s, _) => s.StopGate = new TaskCompletionSource() }.Build(time: time);
        await rig.Host.StartAsync();

        var stop = rig.Host.StopAsync();
        await rig.Created[0].StopStarted.Task.WaitAsync(WaitLimit);
        Assert.Contains(MonitoringHost.DefaultStopTimeout, time.ActiveDueTimes);
        time.FireAll();

        Assert.False(await stop.WaitAsync(WaitLimit));
        Assert.Same(rig.Created[0], rig.Host.Service);
        await rig.Host.StartAsync(); // never a second service next to one that may be active
        Assert.Single(rig.Created);
    }

    [Fact]
    public async Task Dispose_stops_monitoring_and_refuses_further_starts()
    {
        var rig = new Rig().Build();
        await rig.Host.StartAsync();

        await rig.Host.DisposeAsync();
        await rig.Host.DisposeAsync(); // idempotent

        Assert.True(rig.Created[0].IsStopped);
        Assert.Null(rig.Host.Service);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Host.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Host.RestartAsync());
    }

    [Fact]
    public async Task Twenty_start_stop_cycles_leak_no_services()
    {
        var rig = new Rig().Build();
        for (var i = 0; i < 20; i++)
        {
            await rig.Host.StartAsync();
            Assert.True(await rig.Host.StopAsync());
        }

        Assert.Equal(20, rig.Created.Count);
        Assert.All(rig.Created, s => { Assert.Equal(1, s.StartCalls); Assert.True(s.IsStopped); Assert.Equal(1, s.DisposeCalls); });
        Assert.Equal(1, rig.Running.Max);
        Assert.Equal(0, rig.Running.Current);
        Assert.Null(rig.Host.Service);
        Assert.Equal(1, rig.CatalogLoads);
    }

    [Fact]
    public async Task ServiceChanged_handler_exceptions_are_isolated()
    {
        var rig = new Rig().Build();
        rig.Host.ServiceChanged += _ => throw new InvalidOperationException("handler bug");

        await rig.Host.StartAsync();

        Assert.NotNull(rig.Host.Service);
        Assert.Contains(rig.Logger.Lines, l => l.Contains("ServiceChanged handler failed", StringComparison.Ordinal));
        await rig.Host.DisposeAsync();
    }

    // ---------------- with the real MonitoringService ----------------

    [Fact]
    public async Task Hosts_a_real_monitoring_service_and_stops_it_within_the_bound()
    {
        var logger = new CapturingLogger();
        var processes = new FakeProcessProvider();
        var host = new MonitoringHost(logger,
            serviceFactory: catalog => MonitoringComposition.CreateLongshotMonitoring(logger, catalog,
                new FakeGameIntegration(), processes, new FakeIdentityProvider(), pollInterval: TimeSpan.FromMilliseconds(20)),
            catalogLoader: () => IdentityTestData.OotCatalog());

        await host.StartAsync();
        var service = host.Service!;
        using (var observer = new StateTransitionObserver((MonitoringService)service))
        {
            Assert.True(await host.StopAsync().WaitAsync(WaitLimit));
            await observer.WaitForAsync(MonitoringState.Stopped, after: 0);
            Assert.DoesNotContain(MonitoringState.Error, observer.States);
        }

        Assert.Equal(MonitoringState.Stopped, service.State);
        Assert.Null(host.Service);
    }
}

using System.Diagnostics;
using SwitchAchievements.App.Services;
using SwitchAchievements.Tests.Fakes;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.LongshotIdentity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.AppLifecycle;

/// <summary>M5.7 Task 1: coordinated, bounded, idempotent application shutdown.</summary>
public class ShutdownCoordinatorTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private static (MonitoringHost Host, List<FakeMonitoringService> Created, CapturingLogger Logger) Host(
        Action<FakeMonitoringService>? configure = null, TimeProvider? time = null, TimeSpan? stopTimeout = null)
    {
        var logger = new CapturingLogger();
        var created = new List<FakeMonitoringService>();
        var running = new RunningCounter();
        var host = new MonitoringHost(logger,
            serviceFactory: catalog =>
            {
                var s = new FakeMonitoringService(catalog, running);
                configure?.Invoke(s);
                created.Add(s);
                return s;
            },
            catalogLoader: () => IdentityTestData.OotCatalog(),
            stopTimeout: stopTimeout,
            timeProvider: time);
        return (host, created, logger);
    }

    [Fact]
    public async Task Explicit_exit_hides_then_awaits_the_stop_then_shuts_down()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, created, logger) = Host(s => s.StopGate = gate);
        await host.StartAsync();
        var coordinator = new ShutdownCoordinator(host, logger);
        var steps = new List<string>();

        var exit = coordinator.RequestExitAsync(() => steps.Add("hide"), () => steps.Add("shutdown"));
        await created[0].StopStarted.Task.WaitAsync(WaitLimit);

        Assert.Equal(new[] { "hide" }, steps);           // still waiting for monitoring to stop
        Assert.False(exit.IsCompleted);
        gate.SetResult();
        await exit.WaitAsync(WaitLimit);

        Assert.Equal(new[] { "hide", "shutdown" }, steps);
        Assert.True(created[0].IsStopped);
        Assert.True(coordinator.ExitRequested);
    }

    [Fact]
    public async Task Exit_requests_are_idempotent()
    {
        var (host, created, logger) = Host();
        await host.StartAsync();
        var coordinator = new ShutdownCoordinator(host, logger);
        var hides = 0;
        var shutdowns = 0;

        var first = coordinator.RequestExitAsync(() => hides++, () => shutdowns++);
        var second = coordinator.RequestExitAsync(() => hides++, () => shutdowns++);
        await Task.WhenAll(first, second).WaitAsync(WaitLimit);

        Assert.Same(first, second);
        Assert.Equal(1, hides);
        Assert.Equal(1, shutdowns);
        Assert.Equal(1, created[0].StopCalls);
    }

    [Fact]
    public async Task Exit_still_shuts_down_when_monitoring_does_not_stop_in_time()
    {
        var time = new ManualTimeProvider();
        var (host, created, logger) = Host(s => s.StopGate = new TaskCompletionSource(), time: time);
        await host.StartAsync();
        var coordinator = new ShutdownCoordinator(host, logger);
        var shutdown = false;

        var exit = coordinator.RequestExitAsync(() => { }, () => shutdown = true);
        await created[0].StopStarted.Task.WaitAsync(WaitLimit);
        time.FireAll();
        await exit.WaitAsync(WaitLimit);

        Assert.True(shutdown);
        Assert.Contains(logger.Lines, l => l.Contains("did not stop within 5 s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fallback_after_an_explicit_exit_does_not_stop_again()
    {
        var (host, created, logger) = Host();
        await host.StartAsync();
        var coordinator = new ShutdownCoordinator(host, logger);
        await coordinator.RequestExitAsync(() => { }, () => { }).WaitAsync(WaitLimit);

        coordinator.ShutdownFallback();
        coordinator.ShutdownFallback();

        Assert.Equal(1, created[0].StopCalls);
        Assert.Equal(1, created[0].DisposeCalls);
        Assert.DoesNotContain(logger.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fallback_alone_stops_monitoring()
    {
        var (host, created, logger) = Host();
        await host.StartAsync();

        new ShutdownCoordinator(host, logger).ShutdownFallback();

        Assert.True(created[0].IsStopped);
        Assert.Null(host.Service);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Fallback_is_bounded_even_if_monitoring_never_stops()
    {
        // The host's own bound is long; the coordinator's absolute bound must still hold.
        var (host, _, logger) = Host(s => s.StopGate = new TaskCompletionSource(), stopTimeout: TimeSpan.FromHours(1));
        await host.StartAsync();
        var coordinator = new ShutdownCoordinator(host, logger, fallbackBound: TimeSpan.FromMilliseconds(200));

        var watch = Stopwatch.StartNew();
        coordinator.ShutdownFallback();
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"fallback took {watch.Elapsed}");
        Assert.Contains(logger.Lines, l => l.Contains("did not stop within 0.2 s during application exit; exiting anyway", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fallback_and_exit_without_a_host_are_safe()
    {
        var coordinator = new ShutdownCoordinator(null, new CapturingLogger());
        coordinator.ShutdownFallback();

        var shutdown = false;
        await coordinator.RequestExitAsync(() => { }, () => shutdown = true).WaitAsync(WaitLimit);
        Assert.True(shutdown);
    }

    [Fact]
    public void Fallback_on_a_blocked_ui_like_thread_stops_a_real_service_without_deadlock()
    {
        // A thread with a synchronization context that never pumps (like a WPF UI thread
        // blocked in App.OnExit). Anything posted to it would never run.
        var logger = new CapturingLogger();
        var host = new MonitoringHost(logger,
            serviceFactory: catalog => MonitoringComposition.CreateLongshotMonitoring(logger, catalog,
                new FakeGameIntegration(), new FakeProcessProvider(), new FakeIdentityProvider(), pollInterval: TimeSpan.FromMilliseconds(20)),
            catalogLoader: () => IdentityTestData.OotCatalog());
        var context = new NonPumpingSynchronizationContext();
        Exception? failure = null;
        var elapsed = TimeSpan.Zero;

        var uiThread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                host.StartAsync().GetAwaiter().GetResult(); // as App.OnStartup does
                var service = host.Service!;
                var watch = Stopwatch.StartNew();
                new ShutdownCoordinator(host, logger).ShutdownFallback();
                elapsed = watch.Elapsed;
                Assert.Equal(MonitoringState.Stopped, service.State);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        uiThread.Start();

        Assert.True(uiThread.Join(TimeSpan.FromSeconds(15)), "the UI-like thread deadlocked");
        Assert.Null(failure);
        Assert.Equal(0, context.Posts);                          // nothing needed the blocked thread
        Assert.True(elapsed < MonitoringHost.DefaultStopTimeout, $"stop took {elapsed}");
        Assert.DoesNotContain(logger.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
        Assert.Null(host.Service);
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posts);
        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posts);
    }
}

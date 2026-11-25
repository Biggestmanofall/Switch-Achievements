using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Fakes;

namespace SwitchAchievements.Tests;

public class MonitoringServicePipelineTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private static readonly DateTime StartUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static GameProcessInfo Game(int pid) => new(pid, GamePath, StartUtc);

    private static (FakeGameIntegration Integration, FakeProcessProvider Provider, MonitoringService Service) Create(
        TimeSpan? pollInterval = null)
    {
        var integration = new FakeGameIntegration();
        var provider = new FakeProcessProvider();
        var service = new MonitoringService(
            integration,
            provider,
            NullLogger.Instance,
            pollInterval: pollInterval ?? TimeSpan.FromMilliseconds(20));
        return (integration, provider, service);
    }

    private static async Task WaitForStateAsync(MonitoringService service, MonitoringState target)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (service.State == target)
                return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {target}; actual state is {service.State}");
    }

    private static async Task WaitForSessionAsync(MonitoringService service, Func<SessionSnapshot, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(service.Session))
                return;
            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for a session condition");
    }

    [Fact]
    public async Task Connect_timeout_goes_reconnecting()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        integration.OnConnect = (_, _) => throw new TimeoutException("pipe connect timed out");

        // Reconnecting lasts only until rediscovery retries the connect, so observe the
        // transition itself (subscribed before start) instead of sampling the state.
        using var observer = new StateTransitionObserver(service);
        try
        {
            await service.StartAsync();
            var connecting = await observer.WaitForAsync(MonitoringState.Connecting, after: 0);
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: connecting.Index + 1);

            Assert.Equal(connecting.Index + 1, reconnecting.Index); // the timed-out connect led straight to Reconnecting
            Assert.True(integration.ConnectCount >= 1);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Subscribe_failure_goes_error()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        integration.SubscribeException = new InvalidOperationException("subscribe failed: busy");

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Error);
        Assert.Equal(1, integration.ConnectCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Unexpected_connect_exception_goes_error()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        integration.OnConnect = (_, _) => throw new ApplicationException("boom");

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Error);
        await service.StopAsync();
    }

    [Fact]
    public async Task Snapshot_establishes_session_and_updates_latest()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc), rupees: 5));
        await WaitForSessionAsync(service, s => s.Latest?.Rupees == 5);

        Assert.NotNull(service.Session.Id);
        Assert.Equal(5, service.Session.Latest!.Rupees);
        await service.StopAsync();
    }

    [Fact]
    public async Task Backpressure_leaves_newest_snapshot_in_session()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        var fingerprint = new ProcessFingerprint(100, StartUtc);
        integration.RaiseSnapshot(TestSnapshot.Create(fingerprint, gameFrame: 1));
        integration.RaiseSnapshot(TestSnapshot.Create(fingerprint, gameFrame: 2));
        integration.RaiseSnapshot(TestSnapshot.Create(fingerprint, gameFrame: 3));

        await WaitForSessionAsync(service, s => s.Latest?.GameFrame == 3);
        Assert.Equal(3, service.Session.Latest!.GameFrame);

        integration.RaiseSnapshot(TestSnapshot.Create(fingerprint, gameFrame: 4));
        await WaitForSessionAsync(service, s => s.Latest?.GameFrame == 4);
        Assert.Equal(4, service.Session.Latest!.GameFrame);

        await service.StopAsync();
    }

    [Fact]
    public async Task Disconnect_then_reconnect_keeps_monitoring()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        using var observer = new StateTransitionObserver(service); // before the disconnect: nothing can be missed
        try
        {
            var mark = observer.Count;
            integration.RaiseDisconnected();
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
            Assert.Equal(mark, reconnecting.Index); // the disconnect's own transition comes first
            await observer.WaitForAsync(MonitoringState.Monitoring, after: reconnecting.Index + 1);

            Assert.Equal(2, integration.ConnectCount);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Disconnect_signal_rearms_across_two_reconnects()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        using var observer = new StateTransitionObserver(service);
        try
        {
            for (var cycle = 1; cycle <= 2; cycle++)
            {
                // Disconnect only once the previous reconnect has re-armed the signal (Monitoring observed).
                var mark = observer.Count;
                integration.RaiseDisconnected();
                var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
                Assert.Equal(mark, reconnecting.Index);
                await observer.WaitForAsync(MonitoringState.Monitoring, after: reconnecting.Index + 1);
            }

            Assert.Equal(3, integration.ConnectCount);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Stop_during_connect_cancels_cleanly()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        integration.OnConnect = async (_, ct) => await Task.Delay(Timeout.Infinite, ct);

        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Connecting);
        await Task.Delay(50); // let ConnectAsync begin and block on ct

        await service.StopAsync();
        Assert.Equal(MonitoringState.Stopped, service.State);
        Assert.Equal(1, integration.DisposeCount);
    }

    [Fact]
    public async Task Two_simultaneous_stops_share_one_shutdown_task()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        var first = service.StopAsync();
        var second = service.StopAsync();
        await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.Equal(1, integration.DisposeCount);
        Assert.Equal(MonitoringState.Stopped, service.State);
    }

    [Fact]
    public async Task Concurrent_stop_and_dispose_dispose_once()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        var stop = service.StopAsync();
        await service.DisposeAsync();
        await stop;

        Assert.Equal(1, integration.DisposeCount);
        Assert.Equal(MonitoringState.Stopped, service.State);
    }

    [Fact]
    public async Task Dispose_before_start_disposes_integration_without_a_loop()
    {
        var (integration, _, service) = Create();

        await service.DisposeAsync();

        Assert.Equal(1, integration.DisposeCount);
        Assert.Equal(0, integration.ConnectCount);
        Assert.Equal(MonitoringState.Stopped, service.State);

        await service.StartAsync(); // single-use: no-op
        Assert.Equal(0, integration.ConnectCount);
    }

    [Fact]
    public async Task Repeated_stop_and_dispose_dispose_once()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        await service.StopAsync();
        await service.StopAsync();
        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(1, integration.DisposeCount);
    }

    [Fact]
    public async Task Stop_wins_before_start()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };

        await service.StopAsync();   // shutdown established first
        await service.StartAsync();  // no-op

        Assert.Equal(0, integration.ConnectCount);
        Assert.Equal(1, integration.DisposeCount);
        Assert.Equal(MonitoringState.Stopped, service.State);
    }

    [Fact]
    public async Task Concurrent_start_and_stop_are_safe()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };

        var start = Task.Run(() => service.StartAsync());
        var stop = Task.Run(() => service.StopAsync());
        await Task.WhenAll(start, stop);

        Assert.Equal(1, integration.DisposeCount);
        Assert.Equal(MonitoringState.Stopped, service.State);
    }

    [Fact]
    public async Task StateChanged_subscriber_exception_is_isolated()
    {
        var (_, provider, service) = Create();
        provider.Processes = new[] { Game(100) };

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StateChanged += _ => throw new InvalidOperationException("subscriber boom");
        service.StateChanged += s =>
        {
            if (s == MonitoringState.Monitoring)
                reached.TrySetResult();
        };

        await service.StartAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync();
    }

    [Fact]
    public async Task Snapshot_after_stop_is_ignored()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        var fp = new ProcessFingerprint(100, StartUtc);
        integration.RaiseSnapshot(TestSnapshot.Create(fp, gameFrame: 1));
        await WaitForSessionAsync(service, s => s.Id is not null);

        await service.StopAsync();
        Assert.Null(service.Session.Id); // session ended on stop

        integration.RaiseSnapshot(TestSnapshot.Create(fp, gameFrame: 99)); // ignored
        Assert.Null(service.Session.Id);
    }

    [Fact]
    public async Task End_to_end_discover_connect_snapshot_disconnect_reconnect()
    {
        var (integration, provider, service) = Create();
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Searching);

        provider.Processes = new[] { Game(100) };
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        var fingerprint = new ProcessFingerprint(100, StartUtc);
        integration.RaiseSnapshot(TestSnapshot.Create(fingerprint, rupees: 5));
        await WaitForSessionAsync(service, s => s.Latest?.Rupees == 5);
        var sessionId = service.Session.Id;

        using var observer = new StateTransitionObserver(service);
        try
        {
            var mark = observer.Count;
            integration.RaiseDisconnected();
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
            Assert.Equal(mark, reconnecting.Index);
            Assert.NotNull(reconnecting.Session.Id); // disconnect does not end the session (as of the transition)
            Assert.Equal(sessionId, reconnecting.Session.Id);

            await observer.WaitForAsync(MonitoringState.Monitoring, after: reconnecting.Index + 1); // same-fingerprint reconnect

            integration.RaiseSnapshot(TestSnapshot.Create(fingerprint, rupees: 6));
            await WaitForSessionAsync(service, s => s.Latest?.Rupees == 6);
            Assert.Equal(sessionId, service.Session.Id); // reconnect preserved the session

            await service.StopAsync();
            Assert.Equal(MonitoringState.Stopped, service.State);
        }
        finally
        {
            await service.StopAsync();
        }
    }
}
using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Fakes;

namespace SwitchAchievements.Tests;

public class MonitoringServiceSessionTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private static readonly DateTime StartUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LaterStartUtc = new(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc);

    private static GameProcessInfo Game(int pid, DateTime? start = null)
        => new(pid, GamePath, start ?? StartUtc);

    private static (FakeGameIntegration Integration, FakeProcessProvider Provider, MonitoringService Service) Create()
    {
        var integration = new FakeGameIntegration();
        var provider = new FakeProcessProvider();
        var service = new MonitoringService(
            integration,
            provider,
            NullLogger.Instance,
            pollInterval: TimeSpan.FromMilliseconds(20));
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
    public async Task First_snapshot_creates_session_with_baseline()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc), rupees: 5));
        await WaitForSessionAsync(service, s => s.Id is not null);

        Assert.Equal(5, service.Session.Latest!.Rupees);
        await service.StopAsync();
    }

    [Fact]
    public async Task Subsequent_snapshot_updates_latest()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        var fp = new ProcessFingerprint(100, StartUtc);
        integration.RaiseSnapshot(TestSnapshot.Create(fp, rupees: 1));
        await WaitForSessionAsync(service, s => s.Latest?.Rupees == 1);
        var id = service.Session.Id;

        integration.RaiseSnapshot(TestSnapshot.Create(fp, rupees: 2));
        await WaitForSessionAsync(service, s => s.Latest?.Rupees == 2);

        Assert.Equal(id, service.Session.Id);
        await service.StopAsync();
    }

    [Fact]
    public async Task Null_fields_are_preserved()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc))); // all null
        await WaitForSessionAsync(service, s => s.Id is not null);

        Assert.Null(service.Session.Latest!.Map);
        Assert.Null(service.Session.Latest!.Rupees);
        await service.StopAsync();
    }

    [Fact]
    public async Task Same_fingerprint_reconnect_preserves_session_id()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        var fp = new ProcessFingerprint(100, StartUtc);
        integration.RaiseSnapshot(TestSnapshot.Create(fp, rupees: 5));
        await WaitForSessionAsync(service, s => s.Id is not null);
        var id = service.Session.Id;

        // Observe the transient Reconnecting transition (subscribed before the disconnect).
        using var observer = new StateTransitionObserver(service);
        try
        {
            var mark = observer.Count;
            integration.RaiseDisconnected();
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
            Assert.Equal(mark, reconnecting.Index);
            await observer.WaitForAsync(MonitoringState.Monitoring, after: reconnecting.Index + 1);

            integration.RaiseSnapshot(TestSnapshot.Create(fp, rupees: 6));
            await WaitForSessionAsync(service, s => s.Latest?.Rupees == 6);

            Assert.Equal(id, service.Session.Id);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Different_fingerprint_creates_new_session()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc), rupees: 5));
        await WaitForSessionAsync(service, s => s.Id is not null);
        var oldId = service.Session.Id;

        // Arrange the restarted process before the disconnect: discovery does not run
        // while Monitoring, so it is seen only after the disconnect — never too late for
        // rediscovery, which would otherwise reconnect to the old process first.
        provider.Processes = new[] { Game(200) };
        using var observer = new StateTransitionObserver(service);
        try
        {
            var mark = observer.Count;
            integration.RaiseDisconnected();
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
            Assert.Equal(mark, reconnecting.Index);
            await observer.WaitForAsync(MonitoringState.Monitoring, after: reconnecting.Index + 1);

            integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(200, StartUtc), rupees: 0));
            await WaitForSessionAsync(service, s => s.Id is not null && s.Id != oldId);

            Assert.NotEqual(oldId, service.Session.Id);
            Assert.Equal(200, service.Session.Latest!.SourceProcessId);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Pid_reuse_with_different_start_creates_new_session()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100, StartUtc) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc), rupees: 5));
        await WaitForSessionAsync(service, s => s.Id is not null);
        var oldId = service.Session.Id;

        provider.Processes = new[] { Game(100, LaterStartUtc) }; // same PID, new start — arranged before the disconnect
        using var observer = new StateTransitionObserver(service);
        try
        {
            var mark = observer.Count;
            integration.RaiseDisconnected();
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
            Assert.Equal(mark, reconnecting.Index);
            await observer.WaitForAsync(MonitoringState.Monitoring, after: reconnecting.Index + 1);

            integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, LaterStartUtc), rupees: 0));
            await WaitForSessionAsync(service, s => s.Id is not null && s.Id != oldId);

            Assert.NotEqual(oldId, service.Session.Id);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Disconnect_does_not_end_session()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc)));
        await WaitForSessionAsync(service, s => s.Id is not null);
        var sessionBeforeDisconnect = service.Session.Id;

        // Observe the transition itself rather than sampling the current state:
        // with the process still present, Reconnecting lasts only until the next
        // discovery passes (~20–40 ms), so a polling waiter can miss it entirely.
        // StateChanged is raised on the monitoring loop after the session has been
        // reconciled for the new state, so the session read in the handler is the
        // one in effect at the moment of the transition.
        var transitions = new System.Collections.Concurrent.ConcurrentQueue<MonitoringState>();
        var reconnecting = new TaskCompletionSource<SessionId?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(MonitoringState state)
        {
            transitions.Enqueue(state);
            if (state == MonitoringState.Reconnecting)
                reconnecting.TrySetResult(service.Session.Id);
        }

        // Subscribed before the disconnect, so the transition cannot be missed.
        service.StateChanged += OnStateChanged;
        try
        {
            integration.RaiseDisconnected();
            var sessionAtReconnecting = await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(MonitoringState.Reconnecting, transitions.First()); // the disconnect's own transition
            Assert.NotNull(sessionAtReconnecting);                           // session retained across disconnect
            Assert.Equal(sessionBeforeDisconnect, sessionAtReconnecting);    // ...and it is the same session
        }
        finally
        {
            service.StateChanged -= OnStateChanged;
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Process_disappearance_ends_session()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc)));
        await WaitForSessionAsync(service, s => s.Id is not null);

        provider.Processes = Array.Empty<GameProcessInfo>(); // the process is gone — arranged before the disconnect
        using var observer = new StateTransitionObserver(service);
        try
        {
            var mark = observer.Count;
            integration.RaiseDisconnected();
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
            Assert.Equal(mark, reconnecting.Index);
            Assert.NotNull(reconnecting.Session.Id); // still alive while reconnecting
            var searching = await observer.WaitForAsync(MonitoringState.Searching, after: reconnecting.Index + 1);

            Assert.Null(searching.Session.Id); // confirmed gone -> ended
            Assert.Null(service.Session.Id);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Ambiguous_selection_retains_disconnected_session()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc)));
        await WaitForSessionAsync(service, s => s.Id is not null);

        provider.Processes = new[] { Game(100), Game(200) }; // arranged before the disconnect
        using var observer = new StateTransitionObserver(service);
        try
        {
            var mark = observer.Count;
            integration.RaiseDisconnected();
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
            Assert.Equal(mark, reconnecting.Index);
            await observer.WaitForAsync(MonitoringState.Ambiguous, after: reconnecting.Index + 1);

            Assert.NotNull(service.Session.Id); // ambiguity does not end the session
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Fatal_error_ends_session()
    {
        var (integration, provider, service) = Create();
        var failConnect = false;
        integration.OnConnect = (_, _) =>
        {
            if (failConnect) throw new ApplicationException("boom");
            return Task.CompletedTask;
        };
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc)));
        await WaitForSessionAsync(service, s => s.Id is not null);

        failConnect = true; // armed before the disconnect: no connect runs while Monitoring
        using var observer = new StateTransitionObserver(service);
        try
        {
            var mark = observer.Count;
            integration.RaiseDisconnected();
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
            Assert.Equal(mark, reconnecting.Index);
            Assert.NotNull(reconnecting.Session.Id); // disconnected, not yet ended
            var error = await observer.WaitForAsync(MonitoringState.Error, after: reconnecting.Index + 1);

            Assert.Null(error.Session.Id); // fatal error ended the disconnected session
            Assert.Null(service.Session.Id);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task Stop_ends_session()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, StartUtc)));
        await WaitForSessionAsync(service, s => s.Id is not null);

        await service.StopAsync();
        Assert.Null(service.Session.Id);
    }

    [Fact]
    public async Task Dispose_before_start_has_no_session()
    {
        var (_, _, service) = Create();

        await service.DisposeAsync();

        Assert.Null(service.Session.Id);
        Assert.Null(service.Session.Latest);
    }

    [Fact]
    public async Task Invalid_fingerprint_is_ignored()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(0, default), rupees: 5));
        await Task.Delay(150); // bounded window for the loop to read and discard

        Assert.Null(service.Session.Id); // PID 0 never establishes a session
        await service.StopAsync();
    }

    [Fact]
    public async Task Unexpected_cancellation_is_fatal()
    {
        var (integration, provider, service) = Create();
        var failOce = false;
        integration.OnConnect = (_, _) =>
        {
            if (failOce) throw new OperationCanceledException("random");
            return Task.CompletedTask;
        };
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        failOce = true; // armed before the disconnect: no connect runs while Monitoring
        using var observer = new StateTransitionObserver(service);
        try
        {
            var mark = observer.Count;
            integration.RaiseDisconnected();
            var reconnecting = await observer.WaitForAsync(MonitoringState.Reconnecting, after: mark);
            Assert.Equal(mark, reconnecting.Index);
            await observer.WaitForAsync(MonitoringState.Error, after: reconnecting.Index + 1);

            Assert.Equal(MonitoringState.Error, service.State);
        }
        finally
        {
            await service.StopAsync();
        }
    }
}
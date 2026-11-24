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

        integration.RaiseDisconnected();
        await WaitForStateAsync(service, MonitoringState.Reconnecting);
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(fp, rupees: 6));
        await WaitForSessionAsync(service, s => s.Latest?.Rupees == 6);

        Assert.Equal(id, service.Session.Id);
        await service.StopAsync();
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

        integration.RaiseDisconnected();
        await WaitForStateAsync(service, MonitoringState.Reconnecting);
        provider.Processes = new[] { Game(200) };
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(200, StartUtc), rupees: 0));
        await WaitForSessionAsync(service, s => s.Id is not null && s.Id != oldId);

        Assert.NotEqual(oldId, service.Session.Id);
        Assert.Equal(200, service.Session.Latest!.SourceProcessId);
        await service.StopAsync();
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

        integration.RaiseDisconnected();
        await WaitForStateAsync(service, MonitoringState.Reconnecting);
        provider.Processes = new[] { Game(100, LaterStartUtc) }; // same PID, new start
        await WaitForStateAsync(service, MonitoringState.Monitoring);

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(100, LaterStartUtc), rupees: 0));
        await WaitForSessionAsync(service, s => s.Id is not null && s.Id != oldId);

        Assert.NotEqual(oldId, service.Session.Id);
        await service.StopAsync();
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

        integration.RaiseDisconnected();
        await WaitForStateAsync(service, MonitoringState.Reconnecting);

        Assert.NotNull(service.Session.Id); // session retained across disconnect
        await service.StopAsync();
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

        integration.RaiseDisconnected();
        await WaitForStateAsync(service, MonitoringState.Reconnecting);
        provider.Processes = Array.Empty<GameProcessInfo>();
        await WaitForStateAsync(service, MonitoringState.Searching);

        Assert.Null(service.Session.Id); // confirmed gone -> ended
        await service.StopAsync();
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

        integration.RaiseDisconnected();
        await WaitForStateAsync(service, MonitoringState.Reconnecting);
        provider.Processes = new[] { Game(100), Game(200) };
        await WaitForStateAsync(service, MonitoringState.Ambiguous);

        Assert.NotNull(service.Session.Id); // ambiguity does not end the session
        await service.StopAsync();
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

        integration.RaiseDisconnected();
        await WaitForStateAsync(service, MonitoringState.Reconnecting);
        failConnect = true;
        await WaitForStateAsync(service, MonitoringState.Error);

        Assert.Null(service.Session.Id); // fatal error ended the disconnected session
        await service.StopAsync();
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

        integration.RaiseDisconnected();
        await WaitForStateAsync(service, MonitoringState.Reconnecting);
        failOce = true;
        await WaitForStateAsync(service, MonitoringState.Error);

        Assert.Equal(MonitoringState.Error, service.State);
        await service.StopAsync();
    }
}
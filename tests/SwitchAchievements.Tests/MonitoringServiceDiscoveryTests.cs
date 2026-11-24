using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Fakes;

namespace SwitchAchievements.Tests;

public class MonitoringServiceDiscoveryTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private static readonly DateTime StartUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LaterStartUtc = new(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc);

    private static GameProcessInfo Game(int pid, DateTime? start = null)
        => new(pid, GamePath, start ?? StartUtc);

    private static (FakeGameIntegration Integration, FakeProcessProvider Provider, MonitoringService Service) Create(
        int stableObservations = 2,
        TimeSpan? pollInterval = null)
    {
        var integration = new FakeGameIntegration();
        var provider = new FakeProcessProvider();
        var service = new MonitoringService(
            integration,
            provider,
            NullLogger.Instance,
            requiredStableObservations: stableObservations,
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

    [Fact]
    public async Task No_candidates_stays_searching()
    {
        var (_, _, service) = Create();
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Searching);
        await Task.Delay(60);
        Assert.Equal(MonitoringState.Searching, service.State);
        await service.StopAsync();
    }

    [Fact]
    public async Task One_candidate_connects()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Monitoring);
        Assert.Equal(1, integration.ConnectCount);
        Assert.Equal(1, integration.SubscribeCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Multiple_candidates_are_ambiguous_and_never_connect()
    {
        var (integration, provider, service) = Create();
        provider.Processes = new[] { Game(100), Game(200) };
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Ambiguous);
        await Task.Delay(60);
        Assert.Equal(0, integration.ConnectCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Game_appearing_after_start_is_monitored()
    {
        var (integration, provider, service) = Create();
        await service.StartAsync();
        await WaitForStateAsync(service, MonitoringState.Searching);
        provider.Processes = new[] { Game(100) };
        await WaitForStateAsync(service, MonitoringState.Monitoring);
        Assert.Equal(1, integration.ConnectCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Revalidation_rejects_process_that_exited_before_connect()
    {
        var (integration, provider, service) = Create();
        var calls = 0;
        provider.OnGetProcesses = _ =>
        {
            calls++;
            return calls switch
            {
                1 or 2 => new GameProcessInfo[] { Game(100) },
                3 => Array.Empty<GameProcessInfo>(), // revalidation: process gone
                _ => Array.Empty<GameProcessInfo>(),
            };
        };

        await service.StartAsync();
        await Task.Delay(300);
        Assert.Equal(0, integration.ConnectCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Revalidation_rejects_pid_reuse_with_different_start_time()
    {
        var (integration, provider, service) = Create();
        var calls = 0;
        provider.OnGetProcesses = _ =>
        {
            calls++;
            return calls switch
            {
                1 or 2 => new GameProcessInfo[] { Game(100, StartUtc) },
                3 => new GameProcessInfo[] { Game(100, LaterStartUtc) }, // fingerprint changed
                _ => Array.Empty<GameProcessInfo>(),
            };
        };

        await service.StartAsync();
        await Task.Delay(300);
        Assert.Equal(0, integration.ConnectCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task Revalidation_rejects_second_candidate_appearing_before_connect()
    {
        var (integration, provider, service) = Create();
        var calls = 0;
        provider.OnGetProcesses = _ =>
        {
            calls++;
            return calls switch
            {
                1 or 2 => new GameProcessInfo[] { Game(100) },
                3 => new GameProcessInfo[] { Game(100), Game(200) }, // now ambiguous
                _ => Array.Empty<GameProcessInfo>(),
            };
        };

        await service.StartAsync();
        await Task.Delay(300);
        Assert.Equal(0, integration.ConnectCount);
        await service.StopAsync();
    }
}
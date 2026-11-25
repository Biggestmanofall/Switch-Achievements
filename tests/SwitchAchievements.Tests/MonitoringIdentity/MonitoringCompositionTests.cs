using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;
using SwitchAchievements.Tests.Catalog;
using SwitchAchievements.Tests.Fakes;

namespace SwitchAchievements.Tests.MonitoringIdentity;

/// <summary>M5.6.5 (option B): the composition factory — composes, never starts monitoring.</summary>
public class MonitoringCompositionTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Registers_the_longshot_identity_format_only()
        => Assert.IsType<LongshotIdentityFormat>(Assert.Single(MonitoringComposition.IdentityFormats));

    [Fact]
    public void LoadCatalog_loads_the_embedded_catalog_once_and_logs_a_summary()
    {
        var logger = new CapturingLogger();
        var catalog = MonitoringComposition.LoadCatalog(logger);

        Assert.NotNull(catalog);
        Assert.Equal("zelda-ocarina-of-time", Assert.Single(catalog.Games).Id.Value);
        Assert.Contains("INFO supported-game catalog loaded: 1 game(s)", logger.Lines);
    }

    [Fact]
    public void LoadCatalog_failure_returns_null_and_logs_every_error()
    {
        var logger = new CapturingLogger();
        var broken = new InMemoryCatalogResourceSource(("catalog/catalog.json", "{ not json"));

        Assert.Null(MonitoringComposition.LoadCatalog(logger, broken));
        Assert.Contains(logger.Lines, l => l.StartsWith("ERROR supported-game catalog could not be loaded", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l => l.StartsWith("ERROR catalog: catalog/catalog.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Default_composition_is_an_unstarted_service()
    {
        var service = MonitoringComposition.CreateLongshotMonitoring(new CapturingLogger(), MonitoringComposition.LoadCatalog(new CapturingLogger()));
        Assert.Equal(MonitoringState.Stopped, service.State);
        Assert.Same(SessionSnapshot.None, service.Session);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Composed_service_identifies_with_the_embedded_catalog()
    {
        var (service, integration, process) = Compose(MonitoringComposition.LoadCatalog(new CapturingLogger()));

        await service.StartAsync();
        var session = await RunToFirstSnapshot(service, integration, process);

        Assert.Equal("zelda-ocarina-of-time", session.Latest!.GameId.Value);
        Assert.True(session.Identification!.IsIdentified);
        await service.StopAsync();
    }

    [Fact]
    public async Task Composed_service_without_a_catalog_monitors_with_unavailable_identification()
    {
        var (service, integration, process) = Compose(catalog: null);

        await service.StartAsync();
        var session = await RunToFirstSnapshot(service, integration, process);

        Assert.Equal(GameId.Unknown, session.Latest!.GameId);
        Assert.Null(session.Identification);
        await service.StopAsync();
    }

    private static (MonitoringService Service, FakeGameIntegration Integration, GameProcessInfo Process) Compose(GameCatalog? catalog)
    {
        var integration = new FakeGameIntegration();
        var process = new GameProcessInfo(100, @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe", T0);
        var processes = new FakeProcessProvider { Processes = new[] { process } };
        var service = MonitoringComposition.CreateLongshotMonitoring(new CapturingLogger(), catalog,
            integration, processes, new FakeIdentityProvider(), pollInterval: TimeSpan.FromMilliseconds(20));
        return (service, integration, process);
    }

    private static async Task<SessionSnapshot> RunToFirstSnapshot(MonitoringService service, FakeGameIntegration integration, GameProcessInfo process)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (service.State != MonitoringState.Monitoring)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for Monitoring");
            await Task.Delay(10);
        }

        integration.RaiseSnapshot(TestSnapshot.Create(new ProcessFingerprint(process.ProcessId, process.ProcessStartUtc), gameFrame: 1));
        while (service.Session.Latest is null)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for a snapshot");
            await Task.Delay(10);
        }

        return service.Session;
    }
}

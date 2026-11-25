using SwitchAchievements.App.Catalog;
using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.App.Services;

/// <summary>
/// Manual composition of the monitoring stack with game identification (M5.6.5),
/// following the application's existing hand-built composition-root pattern (no
/// dependency-injection container).
///
/// <para>This factory only composes. It does not start monitoring: the application
/// startup sequence, UI, and tray integration call it in M5.7. The returned
/// <see cref="MonitoringService"/> is owned by the caller, which must stop/dispose it.</para>
/// </summary>
public static class MonitoringComposition
{
    /// <summary>The identity-fact formats of the supported integrations (currently Longshot only).</summary>
    public static IReadOnlyList<IIntegrationIdentityFormat> IdentityFormats { get; } =
        Array.AsReadOnly(new IIntegrationIdentityFormat[] { new LongshotIdentityFormat() });

    /// <summary>
    /// Loads the embedded supported-game catalog once. On failure every error is
    /// logged and null is returned: game identification is then unavailable, but
    /// monitoring can still be composed and run (snapshots get <see cref="GameId.Unknown"/>).
    /// </summary>
    /// <param name="logger">Receives a summary, or every load error.</param>
    /// <param name="source">Catalog resources; defaults to those embedded in the application (tests may supply others).</param>
    public static GameCatalog? LoadCatalog(IAppLogger logger, ICatalogResourceSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var result = CatalogLoader.Load(source ?? new EmbeddedCatalogResourceSource(), IdentityFormats);
        if (result.Succeeded)
        {
            logger.Info($"supported-game catalog loaded: {result.Catalog!.Games.Count} game(s)");
            return result.Catalog;
        }

        logger.Error($"supported-game catalog could not be loaded ({result.Errors.Count} error(s)); game identification is unavailable");
        foreach (var error in result.Errors)
            logger.Error("catalog: " + error);
        return null;
    }

    /// <summary>
    /// Composes an unstarted Longshot <see cref="MonitoringService"/> with game
    /// identification against <paramref name="catalog"/> (null: identification
    /// unavailable). The optional parameters exist for tests and default to the real
    /// Longshot integration, Windows process provider, and Longshot identity provider.
    /// </summary>
    public static MonitoringService CreateLongshotMonitoring(
        IAppLogger logger,
        GameCatalog? catalog,
        IGameIntegration? integration = null,
        IProcessProvider? processProvider = null,
        IGameIdentityProvider? identityProvider = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return new MonitoringService(
            integration ?? new LongshotGameIntegration(),
            processProvider ?? new WindowsProcessProvider(),
            logger,
            pollInterval: pollInterval,
            identifier: new GameIdentifier(identityProvider ?? new LongshotGameIdentityProvider(), catalog));
    }
}

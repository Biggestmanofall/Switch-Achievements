using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.Core;

namespace SwitchAchievements.App.Services;

/// <summary>
/// Application-level owner of game monitoring (M5.7). Owned by <c>App</c> for the
/// application's lifetime — never by a window or view model.
///
/// <para>The host loads the supported-game catalog once (at construction), composes
/// the production monitoring service with <see cref="MonitoringComposition"/>, and owns
/// at most one <see cref="IMonitoringService"/> at a time. Because a monitoring
/// service is single-use and its Error state is terminal, recovering from an error
/// means replacing the service: <see cref="RestartAsync"/>.</para>
///
/// <para>Lifecycle semantics (all operations are serialized; none overlap):</para>
/// <list type="bullet">
/// <item><see cref="StartAsync"/>: composes and starts a service. A no-op when a service
/// is already held (running, or still stopping after a timed-out stop).</item>
/// <item><see cref="StopAsync"/>: stops and disposes the current service within the stop
/// timeout. Returns true when no service remains. Returns false when the stop timed out
/// or failed: the host then keeps that service (it may still be active), so no second
/// service can be started next to it. Safe when never started; repeatable.</item>
/// <item><see cref="RestartAsync"/>: stops the current service (as <see cref="StopAsync"/>)
/// and, only if that succeeded, composes and starts a fresh one with the already-loaded
/// catalog. Returns false — starting nothing — when the old service could not be stopped.</item>
/// <item><see cref="DisposeAsync"/>: stops (bounded) and refuses further starts. Idempotent.</item>
/// </list>
///
/// <para>A catalog that fails to load does not prevent monitoring: the failure is logged,
/// <see cref="CatalogAvailable"/> is false, and monitoring runs with identification
/// unavailable.</para>
///
/// <para>Threading: the host never needs the UI thread (all awaits use
/// <c>ConfigureAwait(false)</c>). <see cref="ServiceChanged"/> is raised on the thread
/// performing the operation; handlers must not block on the UI thread.</para>
/// </summary>
public sealed class MonitoringHost : IAsyncDisposable
{
    /// <summary>Default upper bound for stopping a monitoring service.</summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    private readonly IAppLogger _logger;
    private readonly Func<GameCatalog?, IMonitoringService> _serviceFactory;
    private readonly TimeSpan _stopTimeout;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile IMonitoringService? _service;
    private bool _disposed;

    /// <param name="logger">Application logger.</param>
    /// <param name="serviceFactory">Composes a service for the loaded catalog; defaults to the production Longshot composition.</param>
    /// <param name="catalogLoader">Loads the catalog once; defaults to the embedded catalog (<see cref="MonitoringComposition.LoadCatalog"/>).</param>
    /// <param name="stopTimeout">Upper bound for stopping a service; defaults to <see cref="DefaultStopTimeout"/>.</param>
    /// <param name="timeProvider">Time source for the stop timeout; defaults to the system clock.</param>
    public MonitoringHost(
        IAppLogger logger,
        Func<GameCatalog?, IMonitoringService>? serviceFactory = null,
        Func<GameCatalog?>? catalogLoader = null,
        TimeSpan? stopTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceFactory = serviceFactory ?? (catalog => MonitoringComposition.CreateLongshotMonitoring(_logger, catalog));
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        if (_stopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stopTimeout), _stopTimeout, "The stop timeout must be positive.");
        _time = timeProvider ?? TimeProvider.System;

        // Loaded exactly once for the application's lifetime; restarts reuse it.
        Catalog = (catalogLoader ?? (() => MonitoringComposition.LoadCatalog(_logger)))();
        if (Catalog is null)
            _logger.Warn("monitoring will run with game identification unavailable (no supported-game catalog)");
    }

    /// <summary>The supported-game catalog loaded at construction, or null when it failed to load.</summary>
    public GameCatalog? Catalog { get; }

    /// <summary>True when the catalog loaded; false means identification is unavailable (not Unknown/Unsupported).</summary>
    public bool CatalogAvailable => Catalog is not null;

    /// <summary>The current monitoring service, or null when none is held.</summary>
    public IMonitoringService? Service => _service;

    /// <summary>
    /// Raised whenever <see cref="Service"/> changes to a different instance (or to null):
    /// on start, on stop, and when a restart replaces the service. Exceptions thrown by
    /// handlers are logged and do not affect the host.
    /// </summary>
    public event Action<IMonitoringService?>? ServiceChanged;

    /// <summary>Composes and starts monitoring; a no-op when a service is already held.</summary>
    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StartCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops and disposes the current service within the stop timeout. True when no
    /// service remains; false when it could not be stopped in time (it is kept).
    /// </summary>
    public async Task<bool> StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the current service with a fresh one (reusing the loaded catalog).
    /// Returns false, starting nothing, when the current service could not be stopped.
    /// Composition or start failures of the new service propagate.
    /// </summary>
    public async Task<bool> RestartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!await StopCoreAsync().ConfigureAwait(false))
            {
                _logger.Error("monitoring restart refused: the previous monitoring service could not be stopped");
                return false;
            }

            _logger.Info("restarting monitoring");
            await StartCoreAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops monitoring (bounded) and refuses further starts. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate.
    private async Task StartCoreAsync()
    {
        if (_service is not null)
            return; // already running (or still stopping): never a second service

        var service = _serviceFactory(Catalog);
        try
        {
            await service.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await DisposeQuietlyAsync(service).ConfigureAwait(false);
            throw;
        }

        SetService(service);
        _logger.Info("monitoring started");
    }

    // Caller holds _gate.
    private async Task<bool> StopCoreAsync()
    {
        var service = _service;
        if (service is null)
            return true;

        try
        {
            await service.StopAsync().WaitAsync(_stopTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.Error($"monitoring did not stop within {_stopTimeout.TotalSeconds:0.#} s; keeping it until it stops (no replacement is started)");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error("monitoring failed to stop: " + ex.GetType().Name + "; keeping it (no replacement is started)");
            return false;
        }

        await DisposeQuietlyAsync(service).ConfigureAwait(false);
        SetService(null);
        _logger.Info("monitoring stopped");
        return true;
    }

    private async Task DisposeQuietlyAsync(IMonitoringService service)
    {
        try
        {
            await service.DisposeAsync().AsTask().WaitAsync(_stopTimeout, _time).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn("monitoring service disposal did not complete cleanly: " + ex.GetType().Name);
        }
    }

    private void SetService(IMonitoringService? service)
    {
        if (ReferenceEquals(_service, service))
            return;

        _service = service;
        var handlers = ServiceChanged;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<IMonitoringService?>)handler)(service);
            }
            catch (Exception ex)
            {
                _logger.Error("ServiceChanged handler failed: " + ex.GetType().Name);
            }
        }
    }
}

using SwitchAchievements.App.Infrastructure.Logging;

namespace SwitchAchievements.App.Services;

/// <summary>
/// Coordinates stopping monitoring when the application exits (M5.7), for both exit
/// paths, so they share one bounded, idempotent shutdown:
/// <list type="number">
/// <item><see cref="RequestExitAsync"/> — the explicit exit (tray Exit, or closing the
/// window when close-to-tray is off): hides the window, awaits the bounded monitoring
/// shutdown without blocking the UI thread, then shuts the application down. Repeated
/// requests return the same task.</item>
/// <item><see cref="ShutdownFallback"/> — the safety net for any other exit (for example
/// Windows log-off), called from <c>App.OnExit</c> on the UI thread. It blocks for at
/// most a fixed bound and never needs the UI thread to make progress.</item>
/// </list>
///
/// <para>Deadlock analysis: the monitoring host and service never use the UI thread —
/// the service runs its loop and its shutdown worker on the thread pool, and every await
/// on those paths uses <c>ConfigureAwait(false)</c>. The fallback additionally starts the
/// stop with <see cref="Task.Run(Func{Task})"/> so no continuation can be posted to the
/// (blocked) UI thread, and waits with an absolute bound. A <see cref="IMonitoringService.StateChanged"/>
/// handler that blocked on the UI thread would stall the loop; the bound still applies.
/// If the bound expires the timeout is logged and the application exits anyway; the
/// monitoring threads are background threads and end with the process.</para>
/// </summary>
public sealed class ShutdownCoordinator
{
    /// <summary>Extra time the synchronous fallback allows beyond the host's own stop timeout.</summary>
    public static readonly TimeSpan FallbackMargin = TimeSpan.FromSeconds(1);

    private readonly MonitoringHost? _host;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _fallbackBound;
    private readonly object _lock = new();
    private Task? _exitTask;
    private Task? _stopTask;

    /// <param name="host">The monitoring host, or null when none was created.</param>
    /// <param name="logger">Application logger.</param>
    /// <param name="fallbackBound">Absolute bound for <see cref="ShutdownFallback"/>; defaults to the host stop timeout plus <see cref="FallbackMargin"/>.</param>
    public ShutdownCoordinator(MonitoringHost? host, IAppLogger logger, TimeSpan? fallbackBound = null)
    {
        _host = host;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fallbackBound = fallbackBound ?? MonitoringHost.DefaultStopTimeout + FallbackMargin;
    }

    /// <summary>True once an explicit exit has been requested.</summary>
    public bool ExitRequested
    {
        get { lock (_lock) return _exitTask is not null; }
    }

    /// <summary>
    /// The explicit exit: hides the window, awaits the bounded monitoring shutdown, then
    /// shuts the application down. Idempotent: later calls return the same task and
    /// invoke neither callback again.
    /// </summary>
    public Task RequestExitAsync(Action hideWindow, Action shutdownApplication)
    {
        ArgumentNullException.ThrowIfNull(hideWindow);
        ArgumentNullException.ThrowIfNull(shutdownApplication);

        lock (_lock)
            return _exitTask ??= RunExitAsync(hideWindow, shutdownApplication);
    }

    /// <summary>
    /// Bounded synchronous fallback for <c>App.OnExit</c>: ensures monitoring is stopped
    /// (sharing the stop already in progress, if any) and waits at most the fallback bound.
    /// </summary>
    public void ShutdownFallback()
    {
        var stop = GetOrStartStop();
        bool completed;
        try
        {
            completed = stop.Wait(_fallbackBound);
        }
        catch (AggregateException ex)
        {
            _logger.Error("monitoring shutdown failed during application exit: " + ex.InnerException?.GetType().Name);
            return;
        }

        if (!completed)
            _logger.Error($"monitoring did not stop within {_fallbackBound.TotalSeconds:0.#} s during application exit; exiting anyway");
    }

    private async Task RunExitAsync(Action hideWindow, Action shutdownApplication)
    {
        _logger.Info("exit requested");
        try
        {
            hideWindow();
        }
        catch (Exception ex)
        {
            _logger.Warn("could not hide the main window: " + ex.GetType().Name);
        }

        try
        {
            // Awaited without blocking the UI thread; the host bounds the stop itself.
            await GetOrStartStop().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("monitoring shutdown failed: " + ex.GetType().Name);
        }

        shutdownApplication();
    }

    /// <summary>The single stop operation shared by both exit paths (started on the thread pool).</summary>
    private Task GetOrStartStop()
    {
        lock (_lock)
        {
            return _stopTask ??= _host is null
                ? Task.CompletedTask
                : Task.Run(async () => await _host.DisposeAsync().ConfigureAwait(false));
        }
    }
}

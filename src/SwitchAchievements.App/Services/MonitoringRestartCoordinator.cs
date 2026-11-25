using SwitchAchievements.App.Infrastructure.Logging;

namespace SwitchAchievements.App.Services;

/// <summary>The result of one restart request.</summary>
public enum RestartOutcome
{
    /// <summary>Not attempted: one is already running, or the application is shutting down.</summary>
    Ignored,

    /// <summary>A fresh monitoring service replaced the old one.</summary>
    Succeeded,

    /// <summary>The old service could not be stopped and was kept (still in Error); nothing was started.</summary>
    Refused,

    /// <summary>The replacement could not be composed or started; no monitoring service remains.</summary>
    Failed,
}

/// <summary>
/// Runs tray-initiated monitoring restarts through <see cref="MonitoringHost.RestartAsync"/>
/// (M5.7 Task 4) and tracks the failed-restart recovery state.
///
/// <para>When a restart stops the old service but its replacement fails to start, the
/// host holds no service, so the dashboard (truthfully) shows Stopped. The coordinator
/// then sets <see cref="RecoveryRequired"/> so the tray keeps offering Restart; a later
/// successful restart clears it.</para>
///
/// <para>Threading: call <see cref="RestartAsync"/> from one thread (the UI thread). The
/// host never needs that thread, and the await resumes on the caller's context, so
/// <see cref="Changed"/> is raised there. <see cref="RestartAsync"/> never throws.</para>
/// </summary>
public sealed class MonitoringRestartCoordinator
{
    private readonly MonitoringHost _host;
    private readonly Func<bool> _isShuttingDown;
    private readonly IAppLogger _logger;

    public MonitoringRestartCoordinator(MonitoringHost host, Func<bool> isShuttingDown, IAppLogger logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _isShuttingDown = isShuttingDown ?? throw new ArgumentNullException(nameof(isShuttingDown));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>True while a restart attempt is running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>True after a restart failed and left no monitoring service; cleared by a successful restart.</summary>
    public bool RecoveryRequired { get; private set; }

    /// <summary>Raised when <see cref="IsRunning"/> or <see cref="RecoveryRequired"/> may have changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Restarts monitoring. Duplicate requests while one is running, and requests during
    /// shutdown, are ignored. Never throws.
    /// </summary>
    public async Task<RestartOutcome> RestartAsync()
    {
        if (IsRunning || _isShuttingDown())
            return RestartOutcome.Ignored;

        IsRunning = true;
        RaiseChanged();
        try
        {
            bool restarted;
            try
            {
                restarted = await _host.RestartAsync();
            }
            catch (Exception ex)
            {
                if (_isShuttingDown())
                    return RestartOutcome.Ignored; // e.g. the host was disposed by the exit in progress

                _logger.Error("monitoring restart failed: " + ex.GetType().Name + ": " + ex.Message);
                if (_host.Service is null)
                {
                    RecoveryRequired = true;
                    return RestartOutcome.Failed;
                }
                return RestartOutcome.Refused;
            }

            if (!restarted)
                return RestartOutcome.Refused; // the old (Error) service was kept; the host logged why

            RecoveryRequired = false;
            return RestartOutcome.Succeeded;
        }
        finally
        {
            IsRunning = false;
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error("restart Changed handler failed: " + ex.GetType().Name);
        }
    }
}

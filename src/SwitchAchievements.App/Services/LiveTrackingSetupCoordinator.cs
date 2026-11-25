using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.App.Services;

/// <summary>The consent dialog's text (M5.8.2), shown by App before any write.</summary>
public static class LiveTrackingConsent
{
    public const string Title = "Enable live tracking?";

    public const string Question =
        "Switch Achievements will add one setting (HOS_CONTROL=longshot_control) to this game's Longshot configuration file (longshot_env.txt).\n\n"
        + "On future launches, Longshot will open a local control connection that Switch Achievements uses to read live gameplay data.\n\n"
        + "Other programs running under your Windows account could also use that connection to send game commands, including button presses, "
        + "quitting, and changing settings. This is why the feature is disabled until you explicitly enable it.\n\n"
        + "Game-memory access remains disabled.\n\n"
        + "This change will not affect the game currently running. Restart the game afterward. The in-game Restart option also works.\n\n"
        + "Do you want to enable live tracking?";
}

/// <summary>When "Enable live tracking" may be offered (M5.8.2). One rule for the dashboard and the coordinator.</summary>
public static class LiveTrackingEligibility
{
    /// <summary>
    /// The game the action may be offered for, or null: monitoring is Connecting with
    /// <see cref="MonitoringDiagnostic.LiveTrackingDisabled"/> (so one process is selected
    /// and past its startup grace), and that process is identified as a supported game of
    /// the Longshot integration. Unknown, Unsupported, Conflict and unavailable
    /// identification never qualify; neither do ambiguity, other states, or no service.
    /// </summary>
    public static IdentifiedGame? EligibleGame(MonitoringState state, MonitoringDiagnostic diagnostic, GameIdentification? identification)
    {
        if (state != MonitoringState.Connecting || diagnostic != MonitoringDiagnostic.LiveTrackingDisabled)
            return null;
        if (identification is not { IsIdentified: true, Game: { } game })
            return null;
        return string.Equals(game.IntegrationId, LongshotIdentityFormat.Integration, StringComparison.Ordinal) ? game : null;
    }

    /// <summary>The service's eligible game right now, or null.</summary>
    public static IdentifiedGame? EligibleGame(IMonitoringService? service)
        => service is null ? null : EligibleGame(service.State, service.Diagnostic, service.CurrentIdentification);

    /// <summary>True while the host's current service still offers the action for the same game in the same process.</summary>
    public static bool IsStillEligible(MonitoringHost host, IdentifiedGame game)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(game);
        return EligibleGame(host.Service) is { } current
            && current.Process == game.Process
            && current.GameId == game.GameId
            && string.Equals(current.IntegrationId, game.IntegrationId, StringComparison.Ordinal);
    }
}

/// <summary>
/// Runs "Enable live tracking" requests (M5.8.2). WPF-free: the confirmation is a
/// supplied function (App shows the dialog) and the write is the Longshot setup, run in
/// the background.
///
/// <para>Order: ignore while an attempt runs or after disposal; re-check the request is
/// still current; ask for confirmation (declining changes nothing); re-check after the
/// dialog closes; run the setup off the calling thread with the shutdown token; log and
/// return the result. The only route to a write is a positive confirmation.</para>
///
/// <para>Threading: call <see cref="RequestAsync"/> on one thread (the UI thread); its
/// continuation and <see cref="Changed"/> run there.</para>
/// </summary>
public sealed class LiveTrackingSetupCoordinator : IDisposable
{
    private readonly Func<IdentifiedGame, CancellationToken, LiveTrackingSetupResult> _enable;
    private readonly Func<IdentifiedGame, bool> _confirm;
    private readonly Func<IdentifiedGame, bool> _isStillEligible;
    private readonly Func<Func<LiveTrackingSetupResult>, Task<LiveTrackingSetupResult>> _runInBackground;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    /// <param name="enable">The setup (blocking disk I/O).</param>
    /// <param name="confirm">Asks the user; true only for an explicit yes.</param>
    /// <param name="isStillEligible">Whether the request still matches the current monitoring state.</param>
    /// <param name="logger">Application logger.</param>
    /// <param name="runInBackground">Runs the setup off the calling thread; defaults to <see cref="Task.Run{TResult}(Func{TResult})"/>.</param>
    public LiveTrackingSetupCoordinator(
        Func<IdentifiedGame, CancellationToken, LiveTrackingSetupResult> enable,
        Func<IdentifiedGame, bool> confirm,
        Func<IdentifiedGame, bool> isStillEligible,
        IAppLogger logger,
        Func<Func<LiveTrackingSetupResult>, Task<LiveTrackingSetupResult>>? runInBackground = null)
    {
        _enable = enable ?? throw new ArgumentNullException(nameof(enable));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _isStillEligible = isStillEligible ?? throw new ArgumentNullException(nameof(isStillEligible));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runInBackground = runInBackground ?? (work => Task.Run(work));
    }

    /// <summary>True from the request until its result (including while the dialog is open).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Raised when <see cref="IsRunning"/> changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Handles one request. Null when nothing happened (ignored, or declined); otherwise the
    /// result for <paramref name="game"/>. Never throws.
    /// </summary>
    public async Task<LiveTrackingSetupResult?> RequestAsync(IdentifiedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (IsRunning || _disposed)
            return null;
        if (!_isStillEligible(game))
            return LiveTrackingSetupResult.ProcessChanged;

        IsRunning = true;
        RaiseChanged();
        try
        {
            bool confirmed;
            try
            {
                confirmed = _confirm(game);
            }
            catch (Exception ex)
            {
                _logger.Error("live tracking setup: the confirmation failed: " + ex.GetType().Name);
                return null;
            }

            if (!confirmed)
            {
                _logger.Info("live tracking setup: declined; nothing was changed");
                return null;
            }

            if (_disposed)
                return LiveTrackingSetupResult.Cancelled;
            if (!_isStillEligible(game))
            {
                _logger.Info("live tracking setup: the game changed while confirming; nothing was changed");
                return LiveTrackingSetupResult.ProcessChanged;
            }

            var token = _shutdown.Token;
            LiveTrackingSetupResult result;
            try
            {
                result = await _runInBackground(() => _enable(game, token));
            }
            catch (Exception ex)
            {
                // Unexpected: where it stopped is unknown, so the file is not claimed to be intact.
                _logger.Error("live tracking setup failed unexpectedly: " + ex.GetType().Name);
                result = LiveTrackingSetupResult.RecoveryFailed;
            }

            if (result == LiveTrackingSetupResult.RecoveryFailed)
                _logger.Error("live tracking setup: writing longshot_env.txt failed and the file could not be verified as restored; it may be partially changed");
            else
                _logger.Info("live tracking setup: " + result);
            return result;
        }
        finally
        {
            IsRunning = false;
            RaiseChanged();
        }
    }

    /// <summary>Cancels any setup that has not started writing; later requests are ignored. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error("live tracking setup Changed handler failed: " + ex.GetType().Name);
        }
    }
}

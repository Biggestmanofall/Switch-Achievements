using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.Core;

namespace SwitchAchievements.App.Services;

/// <summary>
/// Turns monitoring events into native tray notifications (M5.7 Task 4).
///
/// <para>Data flow: the monitor follows <see cref="MonitoringHost.ServiceChanged"/> and
/// subscribes to the current service's <see cref="IMonitoringService.StateChanged"/>.
/// Each event is captured as <c>(service, state, identification)</c> <em>in the handler</em>,
/// on the monitoring thread — identification is cleared quickly on Error, so it must not
/// be read later — and posted <em>separately</em> to the UI thread (never coalesced),
/// where the <see cref="TrayNotificationPolicy"/> evaluates it in order with the window
/// visibility at that moment.</para>
///
/// <para>Races: a new service can raise its first events before the host publishes it.
/// So on every service change the monitor subscribes first and then posts an evaluation
/// of the service's current state and identification; the policy makes repeated
/// evaluations harmless. Captures and posts happen under one lock, so posts reach the UI
/// thread in the order the states were read. Events from a service the host no longer
/// owns are ignored.</para>
///
/// <para>Shutdown: <see cref="Mute"/> stops all notifications immediately (call it before
/// the exit hides the window); <see cref="Dispose"/> also detaches every subscription.</para>
/// </summary>
public sealed class NotificationMonitor : IDisposable
{
    private readonly MonitoringHost _host;
    private readonly TrayNotificationPolicy _policy;
    private readonly Action<Action> _postToUi;
    private readonly Func<bool> _isWindowHidden;
    private readonly Action<TrayNotification> _show;
    private readonly IAppLogger _logger;
    private readonly Action<IMonitoringService?> _onServiceChanged;
    private readonly object _gate = new();

    private IMonitoringService? _attached;
    private Action<MonitoringState>? _attachedHandler;
    private volatile bool _muted;
    private bool _disposed;

    /// <param name="host">The monitoring host.</param>
    /// <param name="policy">The notification policy (UI thread only).</param>
    /// <param name="postToUi">Posts a callback to the UI thread, preserving order.</param>
    /// <param name="isWindowHidden">The visibility rule, evaluated on the UI thread when a notification is due.</param>
    /// <param name="show">Shows a notification (UI thread).</param>
    /// <param name="logger">Application logger.</param>
    public NotificationMonitor(
        MonitoringHost host,
        TrayNotificationPolicy policy,
        Action<Action> postToUi,
        Func<bool> isWindowHidden,
        Action<TrayNotification> show,
        IAppLogger logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _isWindowHidden = isWindowHidden ?? throw new ArgumentNullException(nameof(isWindowHidden));
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _onServiceChanged = OnServiceChanged;
        _host.ServiceChanged += _onServiceChanged;
        OnServiceChanged(_host.Service);
    }

    /// <summary>True once muted (or disposed): no further notification is shown.</summary>
    public bool IsMuted => _muted;

    /// <summary>Stops all further notifications, including ones already posted. Thread-safe; idempotent.</summary>
    public void Mute() => _muted = true;

    /// <summary>Mutes and detaches from the host and the current service. Idempotent.</summary>
    public void Dispose()
    {
        _muted = true;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _host.ServiceChanged -= _onServiceChanged;
            AttachLocked(null);
        }
    }

    private void OnServiceChanged(IMonitoringService? service)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            // Subscribe first, then evaluate the current state: an event raised before
            // this point is covered by the evaluation, one raised after by the handler.
            AttachLocked(service);
            if (service is not null)
                CaptureAndPostLocked(service, service.State);
        }
    }

    // Caller holds _gate.
    private void AttachLocked(IMonitoringService? service)
    {
        if (ReferenceEquals(service, _attached))
            return;

        if (_attached is not null && _attachedHandler is not null)
            _attached.StateChanged -= _attachedHandler;

        _attached = service;
        _attachedHandler = null;
        if (service is null)
            return;

        _attachedHandler = state => OnStateChanged(service, state);
        service.StateChanged += _attachedHandler;
    }

    // Monitoring thread.
    private void OnStateChanged(IMonitoringService service, MonitoringState state)
    {
        if (_muted)
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            CaptureAndPostLocked(service, state);
        }
    }

    // Caller holds _gate. Captures the identification now, on the reporting thread.
    private void CaptureAndPostLocked(IMonitoringService service, MonitoringState state)
    {
        var identification = service.CurrentIdentification;
        _postToUi(() => Evaluate(service, state, identification));
    }

    // UI thread.
    private void Evaluate(IMonitoringService service, MonitoringState state, GameIdentification? identification)
    {
        if (_muted)
            return;
        if (!ReferenceEquals(service, _host.Service))
            return; // stale: the host replaced (or released) this service

        try
        {
            var notification = _policy.Observe(service, state, identification, _isWindowHidden());
            if (notification is not null && !_muted)
                _show(notification);
        }
        catch (Exception ex)
        {
            _logger.Error("tray notification failed: " + ex.GetType().Name);
        }
    }
}

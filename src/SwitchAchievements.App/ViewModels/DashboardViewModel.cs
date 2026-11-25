using System.Windows.Input;
using SwitchAchievements.App.Mvvm;
using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.App.ViewModels;

/// <summary>
/// Live monitoring dashboard (M5.7 Task 2). WPF-independent: owned by <c>App</c>, which
/// supplies a UI-thread poster and a time source, and disposes it before monitoring
/// shuts down.
///
/// <para>Data flow: <see cref="IMonitoringService.StateChanged"/>,
/// <see cref="MonitoringHost.ServiceChanged"/>, and a one-second freshness timer only
/// <em>request</em> a refresh. Requests are coalesced (at most one pending UI callback),
/// and the refresh, on the UI thread, reads the host's current service and its current
/// <see cref="IMonitoringService.State"/>, <see cref="IMonitoringService.Session"/>,
/// and <see cref="IMonitoringService.CurrentIdentification"/> — never event arguments.
/// Those are immutable values in volatile fields, so reading them needs no locking.
/// The service has no per-snapshot event; the timer tick is what picks up new snapshots
/// while the window is visible.</para>
///
/// <para>Threading contract: construct, call <see cref="SetViewVisible"/>, and
/// <see cref="Dispose"/> on the UI thread. Event and timer callbacks may arrive on any
/// thread; after disposal they do nothing.</para>
/// </summary>
public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    /// <summary>Freshness timer interval while the window is visible.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly MonitoringHost _host;
    private readonly Action<Action> _postToUi;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Action<MonitoringState> _onStateChanged;
    private readonly Action<IMonitoringService?> _onServiceChanged;
    private readonly Action _onDiagnosticChanged;

    private IMonitoringService? _attached;
    private int _refreshQueued;
    private volatile bool _disposed;

    private MonitoringState _monitoringState = MonitoringState.Stopped;
    private string _monitoringStatusText = DashboardFormat.MonitoringState(MonitoringState.Stopped);
    private ConnectionStatus _connection;
    private string _connectionText = DashboardFormat.Connection(ConnectionStatus.NotConnected);
    private IdentificationDisplayStatus _identification;
    private string _identificationText = DashboardFormat.Identification(IdentificationDisplayStatus.None);
    private string? _identificationDetail;
    private string? _gameDisplayName;
    private string? _gameId;
    private string? _sessionId;
    private string? _snapshotGameId;
    private string? _map;
    private string? _age;
    private bool? _eventPlaying;
    private bool? _loading;
    private int? _rupees;
    private long? _gameFrame;
    private int? _life;
    private int? _maxLife;
    private string _heartsText = DashboardFormat.Missing;
    private DateTime? _processStartedUtc;
    private DateTime? _sessionActiveSinceUtc;
    private string? _sessionDurationText;
    private GameplayDataStatus _gameplayData;
    private DateTime? _lastSnapshotUtc;
    private string _freshnessText = DashboardFormat.Freshness(GameplayDataStatus.None, TimeSpan.Zero);
    private bool _isStale;
    private string? _staleHint;
    private MonitoringDiagnostic _diagnostic;
    private string? _diagnosticHint;

    // M5.8.2 "Enable live tracking". UI thread only.
    private readonly RelayCommand _enableLiveTrackingCommand;
    private IdentifiedGame? _eligibleGame;
    private bool _setupInProgress;
    private (ProcessFingerprint Process, LiveTrackingSetupResult Result)? _setupResult;
    private bool _isEnableLiveTrackingOffered;
    private bool _canEnableLiveTracking;
    private string? _liveTrackingSetupMessage;

    private string _statusSummary = DashboardFormat.StatusSummary(MonitoringState.Stopped, IdentificationDisplayStatus.None, null);
    private bool _isViewVisible;

    public DashboardViewModel(MonitoringHost host, Action<Action> postToUi, TimeProvider time)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _onStateChanged = _ => RequestRefresh();
        _onServiceChanged = _ => RequestRefresh();
        _onDiagnosticChanged = RequestRefresh;
        _enableLiveTrackingCommand = new RelayCommand(RequestEnableLiveTracking, () => CanEnableLiveTracking);

        // Created paused; SetViewVisible(true) starts it when the window is shown.
        _timer = _time.CreateTimer(_ => RequestRefresh(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _host.ServiceChanged += _onServiceChanged;
        Refresh(); // attach to the current service and show its state immediately
    }

    // ---------------- monitoring ----------------

    public MonitoringState MonitoringState { get => _monitoringState; private set => SetProperty(ref _monitoringState, value); }
    public string MonitoringStatusText { get => _monitoringStatusText; private set => SetProperty(ref _monitoringStatusText, value); }
    public ConnectionStatus Connection { get => _connection; private set => SetProperty(ref _connection, value); }
    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }

    /// <summary>One-line status summary, prepared for the tray (wired in Task 4).</summary>
    public string StatusSummary { get => _statusSummary; private set => SetProperty(ref _statusSummary, value); }

    // ---------------- identification ----------------

    public bool CatalogAvailable => _host.CatalogAvailable;
    public IdentificationDisplayStatus Identification { get => _identification; private set => SetProperty(ref _identification, value); }
    public string IdentificationText { get => _identificationText; private set => SetProperty(ref _identificationText, value); }

    /// <summary>Technical diagnostics for a tooltip; null when there are none.</summary>
    public string? IdentificationDetail { get => _identificationDetail; private set => SetProperty(ref _identificationDetail, value); }

    /// <summary>The catalog display name — only for an identified, supported game.</summary>
    public string? GameDisplayName { get => _gameDisplayName; private set => SetProperty(ref _gameDisplayName, value); }

    /// <summary>The catalog game id — only for an identified, supported game.</summary>
    public string? GameId { get => _gameId; private set => SetProperty(ref _gameId, value); }

    // ---------------- session ----------------

    public string? SessionId { get => _sessionId; private set => SetProperty(ref _sessionId, value); }

    /// <summary>The game id label stamped on the latest snapshot ("unknown" unless identified); not authorization.</summary>
    public string? SnapshotGameId { get => _snapshotGameId; private set => SetProperty(ref _snapshotGameId, value); }
    public string? Map { get => _map; private set => SetProperty(ref _map, value); }
    public string? Age { get => _age; private set => SetProperty(ref _age, value); }
    public bool? EventPlaying { get => _eventPlaying; private set => SetProperty(ref _eventPlaying, value); }
    public bool? Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public int? Rupees { get => _rupees; private set => SetProperty(ref _rupees, value); }
    public long? GameFrame { get => _gameFrame; private set => SetProperty(ref _gameFrame, value); }

    /// <summary>Current life in quarter-hearts (raw); null when not sampled.</summary>
    public int? Life { get => _life; private set => SetProperty(ref _life, value); }

    /// <summary>Maximum life in quarter-hearts (raw); null when not sampled.</summary>
    public int? MaxLife { get => _maxLife; private set => SetProperty(ref _maxLife, value); }
    public string HeartsText { get => _heartsText; private set => SetProperty(ref _heartsText, value); }
    public DateTime? ProcessStartedUtc { get => _processStartedUtc; private set => SetProperty(ref _processStartedUtc, value); }
    public DateTime? SessionActiveSinceUtc { get => _sessionActiveSinceUtc; private set => SetProperty(ref _sessionActiveSinceUtc, value); }
    public string? SessionDurationText { get => _sessionDurationText; private set => SetProperty(ref _sessionDurationText, value); }

    // ---------------- freshness ----------------

    public GameplayDataStatus GameplayData { get => _gameplayData; private set => SetProperty(ref _gameplayData, value); }
    public DateTime? LastSnapshotUtc { get => _lastSnapshotUtc; private set => SetProperty(ref _lastSnapshotUtc, value); }
    public string FreshnessText { get => _freshnessText; private set => SetProperty(ref _freshnessText, value); }
    public bool IsStale { get => _isStale; private set => SetProperty(ref _isStale, value); }

    /// <summary>A gentle hint while data is stale; null otherwise.</summary>
    public string? StaleHint { get => _staleHint; private set => SetProperty(ref _staleHint, value); }

    // ---------------- diagnostic (M5.8.1) ----------------

    /// <summary>The current service's diagnostic, shown only while it is Connecting.</summary>
    public MonitoringDiagnostic Diagnostic
    {
        get => _diagnostic;
        private set
        {
            if (SetProperty(ref _diagnostic, value))
                OnPropertyChanged(nameof(HasDiagnostic));
        }
    }

    public bool HasDiagnostic => _diagnostic != MonitoringDiagnostic.None;

    /// <summary>
    /// Why there is no live data, or (M5.8.2) the result of enabling live tracking for this
    /// process; null when there is no diagnostic.
    /// </summary>
    public string? DiagnosticHint { get => _diagnosticHint; private set => SetProperty(ref _diagnosticHint, value); }

    // ---------------- enable live tracking (M5.8.2) ----------------

    /// <summary>
    /// True while the action is shown: the current process is eligible
    /// (<see cref="LiveTrackingEligibility"/>) and has not already been set up.
    /// </summary>
    public bool IsEnableLiveTrackingOffered { get => _isEnableLiveTrackingOffered; private set => SetProperty(ref _isEnableLiveTrackingOffered, value); }

    /// <summary>True while the action can be used: offered and no attempt is running.</summary>
    public bool CanEnableLiveTracking
    {
        get => _canEnableLiveTracking;
        private set
        {
            if (SetProperty(ref _canEnableLiveTracking, value))
                _enableLiveTrackingCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Raises <see cref="EnableLiveTrackingRequested"/> for the eligible game. It never writes anything itself.</summary>
    public ICommand EnableLiveTrackingCommand => _enableLiveTrackingCommand;

    /// <summary>The result message for the current process; null when there is none.</summary>
    public string? LiveTrackingSetupMessage { get => _liveTrackingSetupMessage; private set => SetProperty(ref _liveTrackingSetupMessage, value); }

    /// <summary>The user asked to enable live tracking for this game (captured with its process). UI thread.</summary>
    public event Action<IdentifiedGame>? EnableLiveTrackingRequested;

    /// <summary>Marks an attempt as running (the action is disabled meanwhile). UI thread.</summary>
    public void SetLiveTrackingSetupInProgress(bool inProgress)
    {
        if (_disposed)
            return;
        _setupInProgress = inProgress;
        Refresh();
    }

    /// <summary>
    /// Shows a result for the process it was requested for. It is kept only while that
    /// process is still the eligible one, and cleared when the process changes, the
    /// diagnostic clears, the service is replaced, or monitoring stops. UI thread.
    /// </summary>
    public void ShowLiveTrackingSetupResult(IdentifiedGame game, LiveTrackingSetupResult result)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (_disposed || result == LiveTrackingSetupResult.Cancelled)
            return;
        _setupResult = (game.Process, result);
        Refresh();
    }

    private void RequestEnableLiveTracking()
    {
        if (!_disposed && CanEnableLiveTracking && _eligibleGame is { } game)
            EnableLiveTrackingRequested?.Invoke(game);
    }

    /// <summary>Results after which the action is not offered again for the same process.</summary>
    private static bool EndsOffer(LiveTrackingSetupResult result) => result is LiveTrackingSetupResult.Enabled
        or LiveTrackingSetupResult.AlreadyEnabled or LiveTrackingSetupResult.AlreadyEnabledByEnvironment
        or LiveTrackingSetupResult.RecoveryFailed;

    public bool IsViewVisible { get => _isViewVisible; private set => SetProperty(ref _isViewVisible, value); }

    // ---------------- lifecycle ----------------

    /// <summary>
    /// Pauses the freshness timer while the window is hidden; when shown, refreshes
    /// immediately and resumes the one-second tick. State and service changes keep
    /// refreshing while hidden. UI thread only.
    /// </summary>
    public void SetViewVisible(bool visible)
    {
        if (_disposed)
            return;

        IsViewVisible = visible;
        if (visible)
        {
            Refresh();
            _timer.Change(TickInterval, TickInterval);
        }
        else
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Unsubscribes from the host and service and disposes the timer. UI thread only; idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _host.ServiceChanged -= _onServiceChanged;
        Attach(null);
        _timer.Dispose();

        // M5.8.2: nothing stays actionable after disposal.
        EnableLiveTrackingRequested = null;
        _eligibleGame = null;
        _setupResult = null;
    }

    private void RequestRefresh()
    {
        if (_disposed)
            return;
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            return; // a refresh is already queued; it will read the latest values

        _postToUi(() =>
        {
            // Cleared before reading, so a change after this point queues another refresh.
            Volatile.Write(ref _refreshQueued, 0);
            if (!_disposed)
                Refresh();
        });
    }

    private void Attach(IMonitoringService? service)
    {
        if (ReferenceEquals(service, _attached))
            return;
        if (_attached is not null)
        {
            _attached.StateChanged -= _onStateChanged;
            _attached.DiagnosticChanged -= _onDiagnosticChanged;
        }
        _attached = service;
        if (service is not null)
        {
            service.StateChanged += _onStateChanged;
            service.DiagnosticChanged += _onDiagnosticChanged;
        }
    }

    /// <summary>Reads the current service's state and updates every property. UI thread only.</summary>
    private void Refresh()
    {
        var service = _host.Service;
        Attach(service);

        var state = service?.State ?? MonitoringState.Stopped;
        var session = service?.Session ?? SessionSnapshot.None;
        var identification = service?.CurrentIdentification;
        var latest = session.Latest;
        var now = _time.GetUtcNow().UtcDateTime;

        // M5.8.1: a diagnostic explains Connecting only, and only the current service's counts.
        var diagnostic = state == MonitoringState.Connecting ? service?.Diagnostic ?? MonitoringDiagnostic.None : MonitoringDiagnostic.None;
        var trackingDisabled = diagnostic == MonitoringDiagnostic.LiveTrackingDisabled;
        Diagnostic = diagnostic;

        // M5.8.2: the action and its result belong to the eligible process only.
        var eligible = LiveTrackingEligibility.EligibleGame(state, diagnostic, identification);
        if (_setupResult is { } kept && (eligible is null || kept.Process != eligible.Process))
            _setupResult = null;
        _eligibleGame = eligible;
        LiveTrackingSetupMessage = _setupResult is { } shown && eligible is not null
            ? DashboardFormat.LiveTrackingSetup(shown.Result, eligible.DisplayName)
            : null;
        IsEnableLiveTrackingOffered = eligible is not null && !(_setupResult is { } done && EndsOffer(done.Result));
        CanEnableLiveTracking = IsEnableLiveTrackingOffered && !_setupInProgress;

        DiagnosticHint = trackingDisabled ? LiveTrackingSetupMessage ?? DashboardFormat.LiveTrackingDisabledHint : null;

        // Monitoring state and connection (while disabled, nothing is being connected to).
        MonitoringState = state;
        MonitoringStatusText = trackingDisabled ? DashboardFormat.LiveTrackingDisabled : DashboardFormat.MonitoringState(state);
        Connection = trackingDisabled ? ConnectionStatus.NotConnected : DashboardFormat.Connection(state);
        ConnectionText = trackingDisabled ? DashboardFormat.LiveTrackingOff : DashboardFormat.Connection(Connection);

        // Identification: only an identified result names a game.
        var game = identification is { IsIdentified: true } ? identification.Game : null;
        Identification = IdentificationStatusFor(state, identification);
        GameDisplayName = game?.DisplayName;
        GameId = game?.GameId.Value;
        IdentificationText = DashboardFormat.Identification(Identification, game?.DisplayName);
        IdentificationDetail = Identification == IdentificationDisplayStatus.Unavailable
            ? "The supported-game catalog could not be loaded."
            : identification is { Diagnostics.Count: > 0 } ? string.Join(Environment.NewLine, identification.Diagnostics) : null;
        StatusSummary = DashboardFormat.StatusSummary(state, Identification, game?.DisplayName, diagnostic);

        // Session data (kept through Reconnecting and Ambiguous; the service ends it otherwise).
        SessionId = session.Id?.ToString();
        SnapshotGameId = latest?.GameId.Value;
        Map = latest?.Map;
        Age = latest?.Age;
        EventPlaying = latest?.EventPlaying;
        Loading = latest?.Loading;
        Rupees = latest?.Rupees;
        GameFrame = latest?.GameFrame;
        Life = latest?.Life;
        MaxLife = latest?.MaxLife;
        HeartsText = DashboardFormat.Hearts(latest?.Life, latest?.MaxLife);
        ProcessStartedUtc = identification?.Evidence.Process.ProcessStartUtc ?? latest?.SourceProcessStartUtc;
        SessionActiveSinceUtc = session.StartedUtc;
        SessionDurationText = session.StartedUtc is { } started ? DashboardFormat.Duration(now - started) : null;

        // Freshness.
        var sinceLast = latest is null ? TimeSpan.Zero : now - latest.ReceivedUtc;
        GameplayData = latest is not null
            ? (sinceLast >= DashboardFormat.StaleThreshold ? GameplayDataStatus.Stale : GameplayDataStatus.Fresh)
            : state == MonitoringState.Monitoring ? GameplayDataStatus.Waiting : GameplayDataStatus.None;
        LastSnapshotUtc = latest?.ReceivedUtc;
        FreshnessText = trackingDisabled ? DashboardFormat.LiveTrackingDisabledFreshness : DashboardFormat.Freshness(GameplayData, sinceLast);
        IsStale = GameplayData == GameplayDataStatus.Stale;
        StaleHint = IsStale ? DashboardFormat.StaleHint : null;
    }

    private IdentificationDisplayStatus IdentificationStatusFor(MonitoringState state, GameIdentification? identification)
    {
        if (identification is not null)
        {
            return identification.Status switch
            {
                IdentificationStatus.Identified when identification.IsIdentified => IdentificationDisplayStatus.Identified,
                IdentificationStatus.Unsupported => IdentificationDisplayStatus.Unsupported,
                IdentificationStatus.Conflict => IdentificationDisplayStatus.Conflict,
                _ => IdentificationDisplayStatus.Unknown,
            };
        }

        // No result: is a process being tracked, and can it be identified at all?
        var tracking = state is MonitoringState.Connecting or MonitoringState.Monitoring or MonitoringState.Reconnecting;
        if (!tracking)
            return IdentificationDisplayStatus.None;
        return _host.CatalogAvailable ? IdentificationDisplayStatus.Identifying : IdentificationDisplayStatus.Unavailable;
    }
}

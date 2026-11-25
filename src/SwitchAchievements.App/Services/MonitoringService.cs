using System.IO;
using System.Threading.Channels;
using SwitchAchievements.App.Infrastructure.Logging;
using SwitchAchievements.Core;

namespace SwitchAchievements.App.Services;

/// <summary>
/// Implements <see cref="IMonitoringService"/>. A single serialized loop drives
/// the <see cref="MonitoringStateMachine"/> and owns process discovery, safe
/// selection, connection, subscription, snapshot fan-in, reconnection, and
/// shutdown. Single-use: start once, then terminal stop/dispose.
/// </summary>
public sealed class MonitoringService : IMonitoringService
{
    private readonly IGameIntegration _integration;
    private readonly IProcessProvider _processProvider;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly MonitoringStateMachine _machine;

    private readonly Channel<NormalizedSnapshot> _snapshotChannel =
        Channel.CreateBounded<NormalizedSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Action<NormalizedSnapshot> _onSnapshot;
    private readonly Action _onDisconnected;
    private readonly Action<string> _onStatus;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private Task? _stopTask;
    private bool _started;

    private int _publishedState = (int)MonitoringState.Stopped;
    private volatile TaskCompletionSource? _disconnectSignal;
    private CancellationTokenSource? _readCts;

    private readonly SessionManager _sessionManager = new();
    private volatile SessionSnapshot _session = SessionSnapshot.None;
    private GameProcessInfo? _selectedProcess;

    // M5.6.5 game identification. Accessed only on the monitoring loop.
    private readonly GameIdentifier? _identifier;

    // The single cached identification attempt, bound to the complete process
    // fingerprint it was collected for. Null means no attempt has been made for
    // any process yet; an entry whose Identification is null records an attempt
    // that produced no result (identification unavailable). The first attempt for
    // a fingerprint is final: it is never retried, and a different fingerprint
    // replaces it with a fresh attempt.
    private IdentityAttempt? _identity;

    // M5.7: the identification of the process being connected to or monitored,
    // published for cross-thread readers (see IMonitoringService.CurrentIdentification).
    private volatile GameIdentification? _currentIdentification;

    private sealed record IdentityAttempt(ProcessFingerprint Fingerprint, GameIdentification? Identification);

    /// <summary>
    /// M5.8.1: how long after a game process starts its endpoint may still be missing
    /// before <see cref="MonitoringDiagnostic.LiveTrackingDisabled"/> is reported.
    /// (Longshot creates its control pipe early in startup, before the game loads.)
    /// </summary>
    public static readonly TimeSpan DefaultBridgeStartupGrace = TimeSpan.FromSeconds(10);

    // M5.8.1 endpoint availability and diagnostics. The fingerprint fields are accessed
    // only on the monitoring loop; the diagnostic itself is published for cross-thread readers.
    private readonly TimeSpan _bridgeStartupGrace;
    private readonly TimeProvider _time;
    private int _diagnostic = (int)MonitoringDiagnostic.None;
    private ProcessFingerprint? _diagnosticFingerprint;   // the process the current diagnostic belongs to
    private ProcessFingerprint? _bridgeSeenFingerprint;   // the process that last connected successfully
    private ProcessFingerprint? _firstAbsentFingerprint;  // fallback grace reference when a start time is unknown
    private DateTime _firstAbsentUtc;

    /// <param name="identifier">
    /// Optional game identification (M5.6.5). When null, snapshots keep the
    /// <see cref="NormalizedSnapshot.GameId"/> the integration produced and behaviour
    /// is exactly as before. When supplied, each selected process is identified once
    /// per process fingerprint before connecting, and every snapshot is assigned the
    /// identified game's id, or <see cref="GameId.Unknown"/>, before session processing.
    /// </param>
    /// <param name="bridgeStartupGrace">M5.8.1: defaults to <see cref="DefaultBridgeStartupGrace"/>.</param>
    /// <param name="timeProvider">
    /// M5.8.1: the clock the startup grace is measured with (only "now" is read; waits
    /// still use the poll interval). Defaults to the system clock.
    /// </param>
    public MonitoringService(
        IGameIntegration integration,
        IProcessProvider processProvider,
        IAppLogger logger,
        int requiredStableObservations = MonitoringStateMachine.DefaultRequiredStableObservations,
        TimeSpan? pollInterval = null,
        GameIdentifier? identifier = null,
        TimeSpan? bridgeStartupGrace = null,
        TimeProvider? timeProvider = null)
    {
        _integration = integration;
        _processProvider = processProvider;
        _logger = logger;
        _identifier = identifier;
        _machine = new MonitoringStateMachine(requiredStableObservations);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1500);
        _bridgeStartupGrace = bridgeStartupGrace ?? DefaultBridgeStartupGrace;
        _time = timeProvider ?? TimeProvider.System;

        _onSnapshot = snapshot => _snapshotChannel.Writer.TryWrite(snapshot);
        _onDisconnected = () => _disconnectSignal?.TrySetResult();
        _onStatus = message => _logger.Info(message);
    }

    public MonitoringState State => (MonitoringState)Volatile.Read(ref _publishedState);

    public SessionSnapshot Session => _session;

    public GameIdentification? CurrentIdentification => _currentIdentification;

    public event Action<MonitoringState>? StateChanged;

    public MonitoringDiagnostic Diagnostic => (MonitoringDiagnostic)Volatile.Read(ref _diagnostic);

    public event Action? DiagnosticChanged;

    public Task StartAsync()
    {
        _lifecycleGate.Wait();
        try
        {
            if (_stopTask is not null || _started)
                return Task.CompletedTask;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _integration.SnapshotReceived += _onSnapshot;
            _integration.Disconnected += _onDisconnected;
            _integration.StatusChanged += _onStatus;
            _runTask = Task.Run(() => RunAsync(cts.Token));
            _started = true;
            return Task.CompletedTask;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task StopAsync() => BeginShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        await BeginShutdownAsync().ConfigureAwait(false);
    }

    private Task BeginShutdownAsync()
    {
        _lifecycleGate.Wait();
        try
        {
            return _stopTask ??= Task.Run(ShutdownWorkerAsync);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ShutdownWorkerAsync()
    {
        _cts?.Cancel();

        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch { /* the loop must not fault shutdown; defensive */ }
        }

        _integration.SnapshotReceived -= _onSnapshot;
        _integration.Disconnected -= _onDisconnected;
        _integration.StatusChanged -= _onStatus;

        _snapshotChannel.Writer.TryComplete();
        await _integration.DisposeAsync().ConfigureAwait(false);

        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _machine.TryTransition(MonitoringState.Searching);
        PublishState();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    switch (_machine.State)
                    {
                        case MonitoringState.Searching:
                        case MonitoringState.Reconnecting:
                        case MonitoringState.Ambiguous:
                            DiscoveryStep();
                            if (_machine.State is MonitoringState.Searching or MonitoringState.Reconnecting or MonitoringState.Ambiguous)
                                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                            break;

                        case MonitoringState.Connecting:
                            await ConnectStepAsync(ct).ConfigureAwait(false);
                            break;

                        case MonitoringState.Monitoring:
                            await MonitorReadAsync(ct).ConfigureAwait(false);
                            break;

                        case MonitoringState.Error:
                            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                            break;

                        case MonitoringState.Stopped:
                            return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Normal shutdown: exit the loop; the finally transitions to Stopped.
                }
                catch (OperationCanceledException ex)
                {
                    _logger.Error("unexpected cancellation while monitoring: " + ex.Message);
                    _machine.TryTransition(MonitoringState.Error);
                    PublishState();
                }
                catch (Exception ex)
                {
                    _logger.Error("monitoring loop error: " + ex);
                    _machine.TryTransition(MonitoringState.Error);
                    PublishState();
                }
            }
        }
        finally
        {
            _machine.TryTransition(MonitoringState.Stopped);
            PublishState();
            _readCts?.Dispose();
            _readCts = null;
        }
    }

    private void DiscoveryStep()
    {
        var result = SelectFromCurrentProcesses();

        _selectedProcess = result.Selection == ProcessSelectionStatus.Unique ? result.SelectedProcess : null;
        _machine.Observe(result.Selection);
        PublishState();
    }

    private async Task ConnectStepAsync(CancellationToken ct)
    {
        // TOCTOU-safe: re-enumerate and re-select immediately before connecting,
        // then require the freshly selected process to match the cached fingerprint.
        var fresh = RevalidateSelect();
        if (fresh is null)
        {
            _selectedProcess = null;
            _machine.TryTransition(MonitoringState.Reconnecting);
            PublishState();
            return;
        }

        // M5.8.1: a diagnostic belongs to one process; never let it describe another.
        if (_diagnosticFingerprint is { } owner && owner != Fingerprint(fresh))
            SetDiagnostic(MonitoringDiagnostic.None, null);

        // M5.6.5: identify the revalidated process once per fingerprint, before connecting.
        if (_identifier is not null)
        {
            var fingerprint = Fingerprint(fresh);
            if (_identity is null || _identity.Fingerprint != fingerprint)
            {
                // Caller cancellation (StopAsync) propagates to the loop, which stops
                // normally; unexpected exceptions propagate to the loop's error handling.
                var identification = await _identifier.IdentifyAsync(fresh, ct).ConfigureAwait(false);
                _identity = new IdentityAttempt(fingerprint, identification);
                LogIdentification(identification);

                // Identification can take time: revalidate again so a process that exited
                // or was replaced meanwhile is never connected to.
                var again = RevalidateSelect();
                if (again is null || Fingerprint(again) != fingerprint)
                {
                    _selectedProcess = null;
                    _machine.TryTransition(MonitoringState.Reconnecting);
                    PublishState();
                    return;
                }

                fresh = again;
            }
        }

        // M5.7: publish the (fresh or cached) identification of the process about to be
        // connected, before Monitoring can be announced. Null without an identifier.
        _currentIdentification = _identifier is null ? null : IdentificationFor(Fingerprint(fresh));

        // M5.8.1: when the endpoint is known to be absent, a connection attempt cannot
        // succeed: stay Connecting without trying (no timeout, no warning), explain why once
        // the startup grace has passed, and check again after one poll interval (the next
        // pass revalidates the process first). Available and Unknown connect as before.
        var selected = Fingerprint(fresh);
        if (CheckEndpoint(fresh) == EndpointAvailability.Absent)
        {
            // A process whose bridge already worked is not "disabled": its pipe is gone
            // because it is going away (or reconnecting), so it is only checked again.
            if (_bridgeSeenFingerprint != selected && StartupGraceElapsed(fresh, selected))
                SetDiagnostic(MonitoringDiagnostic.LiveTrackingDisabled, selected);

            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            return;
        }

        SetDiagnostic(MonitoringDiagnostic.None, null);

        _readCts?.Dispose();
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _disconnectSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _integration.ConnectAsync(fresh, ct).ConfigureAwait(false);
            await _integration.SubscribeAsync(ct).ConfigureAwait(false);
            _machine.TryTransition(MonitoringState.Monitoring);
            _bridgeSeenFingerprint = selected;
        }
        catch (TimeoutException ex)
        {
            _logger.Warn("connect/subscribe timed out: " + ex.Message);
            _machine.TryTransition(MonitoringState.Reconnecting);
        }
        catch (IOException ex)
        {
            _logger.Warn("connect/subscribe transport failure: " + ex.Message);
            _machine.TryTransition(MonitoringState.Reconnecting);
        }

        PublishState();
    }

    /// <summary>
    /// The single candidate pipeline shared by discovery and pre-connect
    /// revalidation: enumerate, let the integration reduce the list to genuine
    /// game candidates (e.g. excluding Longshot crash-reporter helpers), then
    /// apply the unchanged M3 selection policy.
    /// </summary>
    private ProcessSelectionResult SelectFromCurrentProcesses()
    {
        var processes = _processProvider.GetProcesses(_integration.ProcessName);
        var candidates = _integration.SelectCandidates(processes);
        return ProcessSelector.Select(candidates, bridge: null, verifiedOwnership: null);
    }

    private GameProcessInfo? RevalidateSelect()
    {
        var result = SelectFromCurrentProcesses();

        if (result.Selection != ProcessSelectionStatus.Unique || result.SelectedProcess is null || _selectedProcess is null)
            return null;

        var fresh = result.SelectedProcess;
        var freshFingerprint = new ProcessFingerprint(fresh.ProcessId, fresh.ProcessStartUtc);
        var cachedFingerprint = new ProcessFingerprint(_selectedProcess.ProcessId, _selectedProcess.ProcessStartUtc);
        return freshFingerprint == cachedFingerprint ? fresh : null;
    }

    private async Task MonitorReadAsync(CancellationToken ct)
    {
        var readCts = _readCts;
        if (readCts is null)
            return;

        var readTask = _snapshotChannel.Reader.ReadAsync(readCts.Token).AsTask();
        var signal = _disconnectSignal;

        var winner = signal is null
            ? await Task.WhenAny(readTask).ConfigureAwait(false)
            : await Task.WhenAny(readTask, signal.Task).ConfigureAwait(false);

        if (signal is not null && ReferenceEquals(winner, signal.Task))
        {
            // Disconnect: abort the pending read, drain buffered stale snapshots,
            // then mark the session connection-lost (session retained, not ended).
            readCts.Cancel();
            await ObserveAbortedReadAsync(readTask).ConfigureAwait(false);
            DrainSnapshots();
            _sessionManager.MarkConnectionLost();
            _machine.TryTransition(MonitoringState.Reconnecting);
            PublishState();
            readCts.Dispose();
            _readCts = null;
            return;
        }

        NormalizedSnapshot snapshot;
        try
        {
            snapshot = await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw; // shutdown; the loop finally transitions to Stopped

            // The read CTS was cancelled without a disconnect signal (defensive).
            readCts.Cancel();
            DrainSnapshots();
            _sessionManager.MarkConnectionLost();
            _machine.TryTransition(MonitoringState.Reconnecting);
            PublishState();
            readCts.Dispose();
            _readCts = null;
            return;
        }

        if (snapshot.SourceProcessId <= 0 || snapshot.SourceProcessStartUtc == default)
        {
            _logger.Warn("discarding snapshot without a valid process fingerprint");
            return;
        }

        var identification = IdentificationFor(snapshot.Fingerprint);
        if (_identifier is not null)
        {
            // MonitoringService alone assigns the game id: the identified game only for
            // the exact process fingerprint it was identified for, otherwise Unknown.
            // Stamped before session processing, so a new session inherits it.
            snapshot = snapshot.WithGameId(identification?.Game?.GameId ?? GameId.Unknown);
        }

        _sessionManager.Process(snapshot);
        var sessionId = _sessionManager.Current?.Id;

        // M5.7: a session keeps its start time; a new session starts at the receive
        // time of the snapshot that created it.
        var previous = _session;
        var startedUtc = sessionId is null ? (DateTime?)null
            : previous.Id == sessionId ? previous.StartedUtc
            : snapshot.ReceivedUtc;
        _session = new SessionSnapshot(sessionId, snapshot, identification, startedUtc);
    }

    private static ProcessFingerprint Fingerprint(GameProcessInfo process)
        => new(process.ProcessId, process.ProcessStartUtc);

    /// <summary>The cached identification, only when it belongs to exactly this fingerprint.</summary>
    private GameIdentification? IdentificationFor(ProcessFingerprint fingerprint)
        => _identity is { } attempt && attempt.Fingerprint == fingerprint ? attempt.Identification : null;

    private void LogIdentification(GameIdentification? identification)
    {
        if (identification is null)
        {
            _logger.Warn("game identification unavailable: no supported-game catalog is loaded");
            return;
        }

        var game = identification.Game is { } identified ? $" '{identified.GameId}'" : string.Empty;
        var message = $"game identification: {identification.Status}{game}; " + string.Join("; ", identification.Diagnostics);
        if (identification.Status is IdentificationStatus.Identified or IdentificationStatus.Unsupported)
            _logger.Info(message);
        else
            _logger.Warn(message);
    }

    private static async Task ObserveAbortedReadAsync(Task<NormalizedSnapshot> readTask)
    {
        try { await readTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    private void DrainSnapshots()
    {
        while (_snapshotChannel.Reader.TryRead(out _)) { }
    }

    private void PublishState()
    {
        ReconcileSession();

        // M5.8.1: a diagnostic only explains Connecting; any other state clears it.
        if (_machine.State != MonitoringState.Connecting)
            SetDiagnostic(MonitoringDiagnostic.None, null);

        var current = (int)_machine.State;
        if (Volatile.Read(ref _publishedState) == current)
            return;

        Volatile.Write(ref _publishedState, current);
        RaiseStateChanged(_machine.State);
    }

    private void ReconcileSession()
    {
        // A session ends only when monitoring terminally stops tracking it:
        // confirmed process absence (Searching), fatal error, or stop.
        if (_machine.State is MonitoringState.Searching or MonitoringState.Error or MonitoringState.Stopped)
            EndSession();
    }

    private void EndSession()
    {
        _sessionManager.MarkSessionEnded();
        _session = SessionSnapshot.None;
        _currentIdentification = null; // no process is tracked any more (Searching, Error, Stopped)
    }

    /// <summary>The integration's endpoint check; a failing check is treated as unknown (connect as usual).</summary>
    private EndpointAvailability CheckEndpoint(GameProcessInfo process)
    {
        try
        {
            return _integration.CheckEndpoint(process);
        }
        catch (Exception ex)
        {
            _logger.Warn("endpoint availability check failed: " + ex.GetType().Name);
            return EndpointAvailability.Unknown;
        }
    }

    /// <summary>
    /// True once the process has run for the startup grace period: measured from its
    /// start time, or, when that is unknown, from the first absent check for it.
    /// </summary>
    private bool StartupGraceElapsed(GameProcessInfo process, ProcessFingerprint fingerprint)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        DateTime reference;
        if (process.ProcessStartUtc != default)
        {
            reference = process.ProcessStartUtc.Kind == DateTimeKind.Local
                ? process.ProcessStartUtc.ToUniversalTime()
                : process.ProcessStartUtc;
        }
        else
        {
            if (_firstAbsentFingerprint != fingerprint)
            {
                _firstAbsentFingerprint = fingerprint;
                _firstAbsentUtc = now;
            }
            reference = _firstAbsentUtc;
        }

        return now - reference >= _bridgeStartupGrace;
    }

    /// <summary>Sets the diagnostic and the process it belongs to; logs and raises only on a change. Loop only.</summary>
    private void SetDiagnostic(MonitoringDiagnostic value, ProcessFingerprint? owner)
    {
        _diagnosticFingerprint = value == MonitoringDiagnostic.None ? null : owner;
        var previous = (MonitoringDiagnostic)Interlocked.Exchange(ref _diagnostic, (int)value);
        if (previous == value)
            return;

        if (value == MonitoringDiagnostic.LiveTrackingDisabled)
            _logger.Info($"live tracking disabled: the game's live-data bridge is not available (pid={owner?.ProcessId}); not connecting");
        else
            _logger.Info("live tracking diagnostic cleared");

        var handlers = DiagnosticChanged;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch { /* isolate subscriber exceptions from the loop */ }
        }
    }

    private void RaiseStateChanged(MonitoringState state)
    {
        var handlers = StateChanged;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<MonitoringState>)handler)(state); }
            catch { /* isolate subscriber exceptions from the loop */ }
        }
    }
}
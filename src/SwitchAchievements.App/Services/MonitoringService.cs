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

    public MonitoringService(
        IGameIntegration integration,
        IProcessProvider processProvider,
        IAppLogger logger,
        int requiredStableObservations = MonitoringStateMachine.DefaultRequiredStableObservations,
        TimeSpan? pollInterval = null)
    {
        _integration = integration;
        _processProvider = processProvider;
        _logger = logger;
        _machine = new MonitoringStateMachine(requiredStableObservations);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1500);

        _onSnapshot = snapshot => _snapshotChannel.Writer.TryWrite(snapshot);
        _onDisconnected = () => _disconnectSignal?.TrySetResult();
        _onStatus = message => _logger.Info(message);
    }

    public MonitoringState State => (MonitoringState)Volatile.Read(ref _publishedState);

    public SessionSnapshot Session => _session;

    public event Action<MonitoringState>? StateChanged;

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
        var processes = _processProvider.GetProcesses(_integration.ProcessName);
        var candidates = processes.Where(p => _integration.CanHandle(p)).ToList();
        var result = ProcessSelector.Select(candidates, bridge: null, verifiedOwnership: null);

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

        _readCts?.Dispose();
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _disconnectSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _integration.ConnectAsync(fresh, ct).ConfigureAwait(false);
            await _integration.SubscribeAsync(ct).ConfigureAwait(false);
            _machine.TryTransition(MonitoringState.Monitoring);
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

    private GameProcessInfo? RevalidateSelect()
    {
        var processes = _processProvider.GetProcesses(_integration.ProcessName);
        var candidates = processes.Where(p => _integration.CanHandle(p)).ToList();
        var result = ProcessSelector.Select(candidates, bridge: null, verifiedOwnership: null);

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

        _sessionManager.Process(snapshot);
        _session = new SessionSnapshot(_sessionManager.Current?.Id, snapshot);
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
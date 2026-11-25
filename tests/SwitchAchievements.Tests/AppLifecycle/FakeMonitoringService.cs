using SwitchAchievements.App.Services;
using SwitchAchievements.Core;

namespace SwitchAchievements.Tests.AppLifecycle;

/// <summary>
/// Scriptable <see cref="IMonitoringService"/> for host and shutdown tests. Like the
/// real service, stopping is idempotent (one shared stop task) and disposing stops.
/// A shared <see cref="RunningCounter"/> records the maximum number of fakes running
/// at once, to prove no two services overlap.
/// </summary>
internal sealed class FakeMonitoringService : IMonitoringService
{
    private readonly object _lock = new();
    private readonly RunningCounter _running;
    private Task? _stopTask;
    private bool _started;
    private bool _stopped;

    public FakeMonitoringService(GameCatalog? catalog, RunningCounter running)
    {
        Catalog = catalog;
        _running = running;
    }

    public GameCatalog? Catalog { get; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public Exception? StartException { get; set; }
    public Exception? StopException { get; set; }

    /// <summary>When set, the stop does not complete until this gate is completed.</summary>
    public TaskCompletionSource? StopGate { get; set; }

    public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsRunning { get { lock (_lock) return _started && !_stopped; } }
    public bool IsStopped { get { lock (_lock) return _stopped; } }

    public MonitoringState State => IsRunning ? MonitoringState.Searching : MonitoringState.Stopped;
    public SessionSnapshot Session => SessionSnapshot.None;
    public GameIdentification? CurrentIdentification => null;

    public event Action<MonitoringState>? StateChanged { add { } remove { } }

    public Task StartAsync()
    {
        lock (_lock)
        {
            StartCalls++;
            if (StartException is not null)
                return Task.FromException(StartException);
            _started = true;
        }

        _running.Increment();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            StopCalls++;
            return _stopTask ??= StopCoreAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            DisposeCalls++;
            return new ValueTask(_stopTask ??= StopCoreAsync());
        }
    }

    private async Task StopCoreAsync()
    {
        StopStarted.TrySetResult();
        if (StopGate is { } gate)
            await gate.Task.ConfigureAwait(false);
        if (StopException is not null)
            throw StopException;

        bool wasRunning;
        lock (_lock)
        {
            wasRunning = _started && !_stopped;
            _stopped = true;
        }

        if (wasRunning)
            _running.Decrement();
    }
}

/// <summary>Counts concurrently running fake services and remembers the maximum.</summary>
internal sealed class RunningCounter
{
    private int _current;
    private int _max;

    public int Current => Volatile.Read(ref _current);
    public int Max => Volatile.Read(ref _max);

    public void Increment()
    {
        var now = Interlocked.Increment(ref _current);
        int max;
        while (now > (max = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, max) != max) { }
    }

    public void Decrement() => Interlocked.Decrement(ref _current);
}

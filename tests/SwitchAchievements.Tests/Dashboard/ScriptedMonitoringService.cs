using SwitchAchievements.App.Services;
using SwitchAchievements.Core;

namespace SwitchAchievements.Tests.Dashboard;

/// <summary>
/// A monitoring service whose observable state is set directly by the test. Changes take
/// effect immediately; <see cref="RaiseStateChanged"/> notifies subscribers explicitly.
/// </summary>
internal sealed class ScriptedMonitoringService : IMonitoringService
{
    private Action<MonitoringState>? _stateChanged;
    private int _subscribers;

    public MonitoringState State { get; set; } = MonitoringState.Stopped;
    public SessionSnapshot Session { get; set; } = SessionSnapshot.None;
    public GameIdentification? CurrentIdentification { get; set; }

    public int Subscribers => Volatile.Read(ref _subscribers);

    public event Action<MonitoringState>? StateChanged
    {
        add { _stateChanged += value; Interlocked.Increment(ref _subscribers); }
        remove { _stateChanged -= value; Interlocked.Decrement(ref _subscribers); }
    }

    // ---------------- M5.8.1 diagnostic ----------------

    private Action? _diagnosticChanged;
    private int _diagnosticSubscribers;

    public MonitoringDiagnostic Diagnostic { get; set; } = MonitoringDiagnostic.None;

    public int DiagnosticSubscribers => Volatile.Read(ref _diagnosticSubscribers);

    public event Action? DiagnosticChanged
    {
        add { _diagnosticChanged += value; Interlocked.Increment(ref _diagnosticSubscribers); }
        remove { _diagnosticChanged -= value; Interlocked.Decrement(ref _diagnosticSubscribers); }
    }

    /// <summary>Sets the diagnostic and raises DiagnosticChanged.</summary>
    public void SetDiagnostic(MonitoringDiagnostic diagnostic)
    {
        Diagnostic = diagnostic;
        _diagnosticChanged?.Invoke();
    }

    /// <summary>Sets the state and raises StateChanged with it.</summary>
    public void Set(MonitoringState state)
    {
        State = state;
        RaiseStateChanged(state);
    }

    /// <summary>Raises StateChanged with an arbitrary (possibly stale) argument.</summary>
    public void RaiseStateChanged(MonitoringState argument) => _stateChanged?.Invoke(argument);

    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A UI poster that queues callbacks until the test runs them, like a busy dispatcher.</summary>
internal sealed class QueuedPoster
{
    private readonly Queue<Action> _queue = new();

    public int Pending => _queue.Count;

    public void Post(Action action) => _queue.Enqueue(action);

    public int RunAll()
    {
        var ran = 0;
        while (_queue.TryDequeue(out var action))
        {
            action();
            ran++;
        }
        return ran;
    }
}

using SwitchAchievements.App.Services;

namespace SwitchAchievements.Tests.Fakes;

/// <summary>One recorded <see cref="MonitoringService.StateChanged"/> notification.</summary>
/// <param name="Index">Position in the recorded sequence (0-based).</param>
/// <param name="State">The state the service transitioned to.</param>
/// <param name="Session">The service's session at that moment (reconciled before the event is raised).</param>
internal readonly record struct StateTransition(int Index, MonitoringState State, SessionSnapshot Session);

/// <summary>
/// Records every state transition of a <see cref="MonitoringService"/> so tests can
/// wait for a transient state (such as Reconnecting, which may last only until the
/// next discovery passes) without sampling <see cref="MonitoringService.State"/>,
/// which can miss it entirely.
///
/// Create it before triggering the action, so no transition can be missed. Waits
/// are ordered: <see cref="WaitForAsync"/> returns the first matching transition at or
/// after a given index (use <see cref="Count"/> just before the trigger). Waits are
/// bounded and fail the test with the recorded sequence on timeout. Dispose
/// unsubscribes; a notification racing with disposal only touches this object's own
/// lock-protected state.
/// </summary>
internal sealed class StateTransitionObserver : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private readonly MonitoringService _service;
    private readonly object _lock = new();
    private readonly List<StateTransition> _transitions = new();
    private readonly List<(MonitoringState State, int After, TaskCompletionSource<StateTransition> Done)> _waiters = new();

    public StateTransitionObserver(MonitoringService service)
    {
        _service = service;
        _service.StateChanged += OnStateChanged;
    }

    /// <summary>Number of transitions recorded so far (the index the next one will get).</summary>
    public int Count
    {
        get { lock (_lock) return _transitions.Count; }
    }

    /// <summary>The recorded states, in order.</summary>
    public IReadOnlyList<MonitoringState> States
    {
        get { lock (_lock) return _transitions.Select(t => t.State).ToArray(); }
    }

    /// <summary>
    /// Waits for the first transition to <paramref name="state"/> whose index is at
    /// least <paramref name="after"/>; returns at once if it was already recorded.
    /// </summary>
    public async Task<StateTransition> WaitForAsync(MonitoringState state, int after)
    {
        Task<StateTransition> pending;
        lock (_lock)
        {
            for (var i = after; i < _transitions.Count; i++)
            {
                if (_transitions[i].State == state)
                    return _transitions[i];
            }

            var done = new TaskCompletionSource<StateTransition>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((state, after, done));
            pending = done.Task;
        }

        try
        {
            return await pending.WaitAsync(WaitLimit).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Timed out waiting for a {state} transition at or after #{after}; recorded: {string.Join(" -> ", States)}");
            throw; // unreachable: Assert.Fail throws
        }
    }

    private void OnStateChanged(MonitoringState state)
    {
        var session = _service.Session;
        lock (_lock)
        {
            var transition = new StateTransition(_transitions.Count, state, session);
            _transitions.Add(transition);

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (waiter.State == state && transition.Index >= waiter.After)
                {
                    waiter.Done.TrySetResult(transition);
                    _waiters.RemoveAt(i);
                }
            }
        }
    }

    public void Dispose() => _service.StateChanged -= OnStateChanged;
}

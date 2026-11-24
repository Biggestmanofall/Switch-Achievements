namespace SwitchAchievements.App.Infrastructure.SingleInstance;

/// <summary>
/// Owned by the primary instance for its entire lifetime. It creates the named
/// activation event (AutoReset) and runs a background thread that waits for
/// signals from secondary instances and invokes a callback.
/// </summary>
public sealed class ActivationListener : IDisposable
{
    private readonly EventWaitHandle _event;
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private readonly Action _onActivated;
    private readonly Action<Exception>? _onError;
    private Thread? _thread;
    private int _disposed;

    public ActivationListener(string name, Action onActivated, Action<Exception>? onError = null)
    {
        // Primary instance is the sole creator and holds this handle open until
        // Dispose, so the named object can never be destroyed early by a
        // signaller closing its own handle.
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        _onActivated = onActivated;
        _onError = onError;
    }

    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("listener already started");

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SwitchAchievements.ActivationListener",
        };
        _thread.Start();
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            int idx;
            try
            {
                idx = WaitHandle.WaitAny(new[] { _event, _cts.Token.WaitHandle });
            }
            catch (ObjectDisposedException)
            {
                return; // defensive: handles already disposed
            }

            if (idx == 1)
                return; // shutdown signalled

            if (idx == 0)
            {
                try
                {
                    _onActivated();
                }
                catch (Exception ex)
                {
                    // Never let a callback/dispatcher failure escape this thread
                    // and terminate the process. Swallow + report, then continue
                    // so a later Dispose/Cancel still unblocks WaitAny normally.
                    _onError?.Invoke(ex);
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Unblock the listener: Cancel sets _cts.Token.WaitHandle, which is one
        // of the handles inside WaitAny.
        _cts.Cancel();

        // Wait unconditionally for the thread to terminate. Cancellation
        // guarantees WaitAny returns promptly, and the callback is try/caught
        // and non-blocking, so it cannot hold the thread past shutdown.
        _thread?.Join();

        // Only now, with the thread provably stopped, is it safe to dispose the
        // handles the thread was using.
        _cts.Dispose();
        _event.Dispose();
    }
}

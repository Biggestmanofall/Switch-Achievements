namespace SwitchAchievements.App.Infrastructure.SingleInstance;

/// <summary>
/// Holds a named mutex for the lifetime of the process to enforce at most one
/// running instance. Acquisition is non-blocking; an abandoned mutex (previous
/// owner died without releasing) is re-acquired and treated as this process
/// becoming the sole owner.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    public bool IsFirstInstance { get; }

    public SingleInstanceGuard(string mutexName)
    {
        // initiallyOwned: false so the constructor never acquires or blocks;
        // abandonment is handled explicitly by WaitOne below.
        _mutex = new Mutex(initiallyOwned: false, mutexName, out _);

        bool acquired;
        try
        {
            acquired = _mutex.WaitOne(0); // non-blocking
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // previous owner died; we have acquired ownership
        }

        _ownsMutex = acquired;
        IsFirstInstance = acquired;
    }

    public void Dispose()
    {
        // Ownership is thread-affine: only the thread that acquired via WaitOne
        // may release. The app acquires in OnStartup and disposes in OnExit,
        // both on the UI thread, so this is always the same thread.
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Defensive: not the owner.
            }
        }

        _mutex.Dispose();
        _ownsMutex = false;
    }
}

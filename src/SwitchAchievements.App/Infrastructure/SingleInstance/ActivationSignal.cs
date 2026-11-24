namespace SwitchAchievements.App.Infrastructure.SingleInstance;

/// <summary>
/// Used by secondary instances to ask the primary instance to show its window.
/// The signaller only ever opens the named event created by the primary
/// instance; it never creates it, so it can never destroy the event and lose a
/// signal. A short retry tolerates the primary instance's tiny startup window
/// between acquiring the mutex and creating the activation event.
/// </summary>
public static class ActivationSignal
{
    public static bool TrySignal(string name, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (true)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(name);
                ev.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                if (DateTime.UtcNow >= deadline)
                    return false; // primary instance never appeared
                Thread.Sleep(50);
            }
        }
    }
}

using SwitchAchievements.App.Infrastructure.SingleInstance;

namespace SwitchAchievements.Tests;

public class SingleInstanceGuardTests
{
    private static string UniqueName() => $@"Local\SA.Test.{Guid.NewGuid():N}";

    [Fact]
    public void First_instance_acquires_ownership()
    {
        using var guard = new SingleInstanceGuard(UniqueName());
        Assert.True(guard.IsFirstInstance);
    }

    [Fact]
    public void Second_instance_on_another_thread_is_not_first()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name); // acquired on test thread

        var secondIsFirst = false;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            secondIsFirst = second.IsFirstInstance;
        });
        thread.Start();
        thread.Join();

        Assert.True(first.IsFirstInstance);
        Assert.False(secondIsFirst);
    }

    [Fact]
    public void Disposed_guard_releases_so_a_new_guard_acquires()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        Assert.True(first.IsFirstInstance);
        first.Dispose();

        using var second = new SingleInstanceGuard(name);
        Assert.True(second.IsFirstInstance);
    }

    [Fact]
    public void Abandoned_mutex_is_reacquired_as_first_instance()
    {
        var name = UniqueName();
        var acquired = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name, out _);
            mutex.WaitOne(); // acquire and then abandon (Dispose without Release)
            acquired.Set();
        });
        thread.Start();
        acquired.Wait();
        thread.Join();

        // The mutex is now abandoned; a new guard should treat itself as first.
        using var guard = new SingleInstanceGuard(name);
        Assert.True(guard.IsFirstInstance);
    }
}

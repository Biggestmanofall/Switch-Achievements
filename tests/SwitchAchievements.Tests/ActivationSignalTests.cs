using SwitchAchievements.App.Infrastructure.SingleInstance;

namespace SwitchAchievements.Tests;

public class ActivationSignalTests
{
    private static string UniqueName() => $@"Local\SA.Activation.{Guid.NewGuid():N}";

    [Fact]
    public void Listener_delivers_signal_to_callback()
    {
        var name = UniqueName();
        var signalled = new ManualResetEventSlim();
        using var listener = new ActivationListener(name, () => signalled.Set());
        listener.Start();

        var signalledOk = ActivationSignal.TrySignal(name, TimeSpan.FromSeconds(2));
        var received = signalled.Wait(TimeSpan.FromSeconds(2));

        Assert.True(signalledOk);
        Assert.True(received);
    }

    [Fact]
    public void Signal_before_listener_waits_is_not_lost()
    {
        var name = UniqueName();
        var signalled = new ManualResetEventSlim();
        using var listener = new ActivationListener(name, () => signalled.Set());
        listener.Start();

        // Signal immediately, racing the listener's first wait.
        ActivationSignal.TrySignal(name, TimeSpan.FromSeconds(2));

        Assert.True(signalled.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Signaller_retries_until_listener_exists()
    {
        var name = UniqueName();
        var signalled = new ManualResetEventSlim();

        bool signalResult = false;
        var thread = new Thread(() =>
            signalResult = ActivationSignal.TrySignal(name, TimeSpan.FromSeconds(3)));
        thread.Start();

        Thread.Sleep(200);
        using var listener = new ActivationListener(name, () => signalled.Set());
        listener.Start();

        thread.Join();

        Assert.True(signalResult);
        Assert.True(signalled.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Signaller_returns_false_when_no_listener_exists()
    {
        var name = UniqueName();
        Assert.False(ActivationSignal.TrySignal(name, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Callback_exception_does_not_kill_the_listener()
    {
        var name = UniqueName();
        var errorObserved = new ManualResetEventSlim();
        var secondSignalReceived = new ManualResetEventSlim();
        var invocations = 0;

        using var listener = new ActivationListener(
            name,
            () =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                    throw new InvalidOperationException("boom");
                secondSignalReceived.Set();
            },
            _ => errorObserved.Set());
        listener.Start();

        ActivationSignal.TrySignal(name);
        Assert.True(errorObserved.Wait(TimeSpan.FromSeconds(2)), "onError not invoked");

        ActivationSignal.TrySignal(name);
        Assert.True(secondSignalReceived.Wait(TimeSpan.FromSeconds(2)), "second signal not delivered");
    }

    [Fact]
    public void Dispose_terminates_thread_and_frees_event()
    {
        var name = UniqueName();
        var callbackCount = 0;
        var listener = new ActivationListener(name, () => Interlocked.Increment(ref callbackCount));
        listener.Start();
        listener.Dispose();

        // The event handle is closed, so a signal can no longer be delivered.
        var signalResult = ActivationSignal.TrySignal(name, TimeSpan.FromMilliseconds(300));
        Assert.False(signalResult);

        Thread.Sleep(100); // give any lingering callback time to run
        Assert.Equal(0, callbackCount);
    }
}

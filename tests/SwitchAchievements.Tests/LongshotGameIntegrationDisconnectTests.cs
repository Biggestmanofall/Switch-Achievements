using System.IO.Pipes;
using SwitchAchievements.Core;
using SwitchAchievements.Longshot;

namespace SwitchAchievements.Tests;

/// <summary>
/// Covers the <see cref="IGameIntegration.Disconnected"/> lifecycle using a real
/// in-process <see cref="NamedPipeServerStream"/> as a controlled stand-in for
/// the game: opening the server lets the client connect, and disposing the
/// server closes the remote end, which the client's read loop observes as a
/// genuine loss. No Longshot process, game, or timing-sensitive race is needed.
/// </summary>
public class LongshotGameIntegrationDisconnectTests
{
    private static string NewPipeName() => "m5_1_" + Guid.NewGuid().ToString("N");

    private static GameProcessInfo TestProcess()
        => new(1234, @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe", DateTime.UtcNow);

    private static (NamedPipeServerStream Server, Task Connected) OpenServer(string pipeName)
    {
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        return (server, connected);
    }

    [Fact]
    public async Task Disconnected_raises_once_on_genuine_connection_loss()
    {
        var pipeName = NewPipeName();
        var (server, connected) = OpenServer(pipeName);

        var integration = new LongshotGameIntegration(pipeName);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        integration.Disconnected += () =>
        {
            Interlocked.Increment(ref count);
            raised.TrySetResult();
        };

        await integration.ConnectAsync(TestProcess());
        await connected;

        await server.DisposeAsync(); // remote end closes -> genuine loss

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref count));

        await integration.DisposeAsync(); // already gone; must not re-raise
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task Intentional_disposal_does_not_raise_disconnected()
    {
        var pipeName = NewPipeName();
        var (server, connected) = OpenServer(pipeName);

        var integration = new LongshotGameIntegration(pipeName);
        var count = 0;
        integration.Disconnected += () => Interlocked.Increment(ref count);

        await integration.ConnectAsync(TestProcess());
        await connected;
        Assert.True(integration.IsConnected);

        await integration.DisposeAsync();
        Assert.False(integration.IsConnected);

        await Task.Delay(200); // bounded window for the cancelled read loop to wind down
        Assert.Equal(0, Volatile.Read(ref count));

        await server.DisposeAsync();
    }

    [Fact]
    public async Task Subscriber_exception_does_not_break_other_subscribers()
    {
        var pipeName = NewPipeName();
        var (server, connected) = OpenServer(pipeName);

        var integration = new LongshotGameIntegration(pipeName);
        var secondFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        integration.Disconnected += () => throw new InvalidOperationException("boom");
        integration.Disconnected += () => secondFired.TrySetResult();

        await integration.ConnectAsync(TestProcess());
        await connected;

        await server.DisposeAsync();
        await secondFired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await integration.DisposeAsync();
    }

    [Fact]
    public async Task Disconnected_can_be_rearmed_for_a_subsequent_connection()
    {
        var pipeName = NewPipeName();
        var integration = new LongshotGameIntegration(pipeName);

        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        integration.Disconnected += () =>
        {
            var n = Interlocked.Increment(ref count);
            (n == 1 ? first : second).TrySetResult();
        };

        var (server1, connected1) = OpenServer(pipeName);
        await integration.ConnectAsync(TestProcess());
        await connected1;
        await server1.DisposeAsync();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref count));

        var (server2, connected2) = OpenServer(pipeName);
        await integration.ConnectAsync(TestProcess());
        await connected2;
        await server2.DisposeAsync();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, Volatile.Read(ref count));

        await integration.DisposeAsync();
    }

    [Fact]
    public async Task Unsubscribing_disconnected_stops_notifications()
    {
        var pipeName = NewPipeName();
        var integration = new LongshotGameIntegration(pipeName);

        var count = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action handler = () =>
        {
            Interlocked.Increment(ref count);
            first.TrySetResult();
        };

        integration.Disconnected += handler;

        var (server1, connected1) = OpenServer(pipeName);
        await integration.ConnectAsync(TestProcess());
        await connected1;
        await server1.DisposeAsync();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref count));

        integration.Disconnected -= handler;

        var (server2, connected2) = OpenServer(pipeName);
        await integration.ConnectAsync(TestProcess());
        await connected2;
        await server2.DisposeAsync();
        await Task.Delay(200);

        Assert.Equal(1, Volatile.Read(ref count)); // removed handler never re-fired

        await integration.DisposeAsync();
    }
}
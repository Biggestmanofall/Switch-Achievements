using SwitchAchievements.Core;

namespace SwitchAchievements.Tests.Fakes;

/// <summary>Scriptable <see cref="IGameIntegration"/> for monitoring tests (no game or pipe).</summary>
public sealed class FakeGameIntegration : IGameIntegration
{
    private const string GameBinarySuffix = @"\bin\longshot.exe";

    public string Id { get; init; } = "longshot";
    public string DisplayName { get; init; } = "Longshot (recompilation)";
    public string ProcessName { get; init; } = "longshot";
    public string PipeName { get; init; } = "longshot_control";

    public Func<GameProcessInfo, bool>? CanHandleOverride { get; set; }
    public Func<GameProcessInfo, CancellationToken, Task>? OnConnect { get; set; }
    public Exception? SubscribeException { get; set; }

    public int ConnectCount { get; private set; }
    public int SubscribeCount { get; private set; }
    public int DisposeCount { get; private set; }
    public GameProcessInfo? LastConnectedProcess { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<NormalizedSnapshot>? SnapshotReceived;
    public event Action? Disconnected;

    public bool CanHandle(GameProcessInfo process)
    {
        if (CanHandleOverride is not null)
            return CanHandleOverride(process);

        var path = process.ExecutablePath;
        return !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(GameBinarySuffix, StringComparison.OrdinalIgnoreCase);
    }

    public async Task ConnectAsync(GameProcessInfo process, CancellationToken ct = default)
    {
        ConnectCount++;
        LastConnectedProcess = process;

        if (OnConnect is not null)
            await OnConnect(process, ct).ConfigureAwait(false);
        else
            await Task.CompletedTask.ConfigureAwait(false);

        IsConnected = true;
    }

    public Task SubscribeAsync(CancellationToken ct = default)
    {
        SubscribeCount++;
        if (SubscribeException is not null)
            throw SubscribeException;
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<NormalizedSnapshot?> GetSnapshotAsync(CancellationToken ct = default)
        => Task.FromResult<NormalizedSnapshot?>(null);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    public void RaiseSnapshot(NormalizedSnapshot snapshot) => SnapshotReceived?.Invoke(snapshot);

    public void RaiseDisconnected() => Disconnected?.Invoke();

    public void RaiseStatus(string message) => StatusChanged?.Invoke(message);
}
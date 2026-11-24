namespace SwitchAchievements.Core;

/// <summary>
/// A single game integration (Longshot, a future emulator, a recompilation, etc.).
/// Implementations translate integration-specific data into normalized snapshots.
///
/// Phase 1 ships exactly one implementation, in SwitchAchievements.Longshot.
/// Nothing above this interface may depend on integration-specific details such
/// as pipe names or protocol wire format.
/// </summary>
public interface IGameIntegration : IAsyncDisposable
{
    /// <summary>Stable machine-readable id, e.g. "longshot".</summary>
    string Id { get; }

    /// <summary>Human-readable name, e.g. "Longshot (recompilation)".</summary>
    string DisplayName { get; }

    /// <summary>OS process name used to search for this integration's game (e.g. "longshot").</summary>
    string ProcessName { get; }

    /// <summary>Name of the transport endpoint this integration connects to (e.g. a named-pipe name).</summary>
    string PipeName { get; }

    /// <summary>
    /// True when this integration recognizes the given operating-system process
    /// as a game it can connect to. Used by the desktop app's detection loop.
    /// </summary>
    bool CanHandle(GameProcessInfo process);

    /// <summary>Connect to the game and prepare the snapshot stream.</summary>
    Task ConnectAsync(GameProcessInfo process, CancellationToken ct);

    /// <summary>Begin receiving normalized snapshots (implemented in a later milestone).</summary>
    Task SubscribeAsync(CancellationToken ct);

    /// <summary>Stop receiving snapshots (implemented in a later milestone).</summary>
    Task UnsubscribeAsync(CancellationToken ct);

    /// <summary>Ask once for the current game state (used by the status window).</summary>
    Task<NormalizedSnapshot?> GetSnapshotAsync(CancellationToken ct);

    /// <summary>Raised for human-readable lifecycle messages (connect/disconnect/errors).</summary>
    event Action<string>? StatusChanged;

    /// <summary>Raised for each normalized snapshot received from the game.</summary>
    event Action<NormalizedSnapshot>? SnapshotReceived;

    /// <summary>
    /// Raised once for each unexpected connection loss. It is never raised for
    /// intentional disposal or normal shutdown, and never after the integration
    /// has been disposed. A subsequent connection arms the event again.
    /// </summary>
    event Action? Disconnected;
}
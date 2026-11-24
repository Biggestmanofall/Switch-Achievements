namespace SwitchAchievements.Core;

/// <summary>
/// Owns game-session identity and snapshot comparison. It groups incoming
/// normalized snapshots by their process fingerprint (process id + start time),
/// assigns <see cref="SessionId"/>s, establishes comparison baselines, and
/// reports within-session state changes.
///
/// The manager is independent of any specific integration and of the pipe
/// connection. Callers (the future monitor) drive connection-loss and end-of-
/// process signals via <see cref="MarkConnectionLost"/> and
/// <see cref="MarkSessionEnded"/>.
/// </summary>
public sealed class SessionManager
{
    public GameSession? Current { get; private set; }

    public event Action<GameSession>? SessionStarted;
    public event Action<GameSession>? SessionEnded;
    public event Action<GameSession>? ConnectionLost;
    public event Action<GameSession>? ConnectionRestored;
    public event Action<GameSession>? BaselineEstablished;

    /// <summary>
    /// Processes one snapshot and returns the state changes it represents. An
    /// empty list means the snapshot was a baseline (new session or reconnect).
    /// </summary>
    public IReadOnlyList<GameStateChange> Process(NormalizedSnapshot snapshot)
    {
        var fingerprint = snapshot.Fingerprint;

        if (Current is null || Current.Fingerprint != fingerprint)
        {
            // A brand-new game process (no prior session, or a different instance).
            EndCurrent();

            var session = new GameSession
            {
                Id = SessionId.NewId(),
                GameId = snapshot.GameId,
                Fingerprint = fingerprint,
                Phase = SessionPhase.Sampling,
                Baseline = snapshot,
            };

            Current = session;
            SessionStarted?.Invoke(session);
            BaselineEstablished?.Invoke(session);
            return Array.Empty<GameStateChange>();
        }

        // Same process instance.
        if (Current.Phase == SessionPhase.Disconnected)
        {
            // Reconnect: fresh baseline, never compared across the gap.
            Current.Baseline = snapshot;
            Current.Phase = SessionPhase.Sampling;
            ConnectionRestored?.Invoke(Current);
            BaselineEstablished?.Invoke(Current);
            return Array.Empty<GameStateChange>();
        }

        // Normal sampling: compare against the current reference, then advance it.
        var baseline = Current.Baseline ?? snapshot;
        var changes = SnapshotComparer.Compare(Current.Id, baseline, snapshot);
        Current.Baseline = snapshot;
        return changes;
    }

    /// <summary>Marks the connection lost but keeps the session (and its id) alive.</summary>
    public void MarkConnectionLost()
    {
        if (Current is null || Current.Phase == SessionPhase.Disconnected)
            return;

        Current.Phase = SessionPhase.Disconnected;
        ConnectionLost?.Invoke(Current);
    }

    /// <summary>Ends the current session (the process was confirmed gone).</summary>
    public void MarkSessionEnded() => EndCurrent();

    private void EndCurrent()
    {
        if (Current is null)
            return;

        var ended = Current;
        Current = null;
        ended.Phase = SessionPhase.Ended;
        SessionEnded?.Invoke(ended);
    }
}
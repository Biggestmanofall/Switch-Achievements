using SwitchAchievements.Core;

namespace SwitchAchievements.Tests;

public class SessionManagerTests
{
    private static readonly ProcessFingerprint FpA =
        new(1000, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static readonly ProcessFingerprint FpB =
        new(2000, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void First_snapshot_establishes_baseline_and_produces_zero_changes()
    {
        var manager = new SessionManager();
        var started = new List<GameSession>();
        var baselines = new List<GameSession>();
        manager.SessionStarted += started.Add;
        manager.BaselineEstablished += baselines.Add;

        var changes = manager.Process(TestSnapshot.Create(FpA, rupees: 5));
        var current = Assert.IsType<GameSession>(manager.Current);

        Assert.Empty(changes);
        Assert.Single(started);
        Assert.Same(started[0], current);
        Assert.Single(baselines);
        Assert.Same(baselines[0], current);
        Assert.Equal(SessionPhase.Sampling, current.Phase);
    }

    [Fact]
    public void New_process_fingerprint_creates_a_new_session()
    {
        var manager = new SessionManager();
        var started = new List<GameSession>();
        var ended = new List<GameSession>();
        manager.SessionStarted += started.Add;
        manager.SessionEnded += ended.Add;

        manager.Process(TestSnapshot.Create(FpA, rupees: 1));
        var firstId = manager.Current!.Id;

        manager.Process(TestSnapshot.Create(FpB, rupees: 0));
        var secondId = manager.Current!.Id;

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, started.Count);
        Assert.Single(ended);
    }

    [Fact]
    public void Same_pid_different_start_time_creates_a_new_session()
    {
        var manager = new SessionManager();
        var early = new ProcessFingerprint(42, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var late = new ProcessFingerprint(42, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));

        manager.Process(TestSnapshot.Create(early));
        var firstId = manager.Current!.Id;

        manager.Process(TestSnapshot.Create(late));
        var secondId = manager.Current!.Id;

        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public void Same_fingerprint_after_disconnect_retains_the_same_session()
    {
        var manager = new SessionManager();
        var restored = new List<GameSession>();
        var ended = new List<GameSession>();
        manager.ConnectionRestored += restored.Add;
        manager.SessionEnded += ended.Add;

        manager.Process(TestSnapshot.Create(FpA, rupees: 5));
        var sessionId = manager.Current!.Id;

        manager.MarkConnectionLost();
        Assert.Equal(SessionPhase.Disconnected, manager.Current!.Phase);

        manager.Process(TestSnapshot.Create(FpA, rupees: 6));

        Assert.Equal(sessionId, manager.Current!.Id);
        Assert.Single(restored);
        Assert.Empty(ended);
    }

    [Fact]
    public void First_snapshot_after_reconnect_establishes_a_fresh_baseline()
    {
        var manager = new SessionManager();

        manager.Process(TestSnapshot.Create(FpA, rupees: 1)); // baseline = 1
        manager.MarkConnectionLost();

        // The game reports 0 rupees right after reconnect; this is a fresh baseline.
        var changes = manager.Process(TestSnapshot.Create(FpA, rupees: 0));

        Assert.Empty(changes);
        Assert.Equal(0, manager.Current!.Baseline!.Rupees!.Value);
        Assert.Equal(SessionPhase.Sampling, manager.Current!.Phase);
    }

    [Fact]
    public void New_process_does_not_inherit_the_previous_baseline()
    {
        var manager = new SessionManager();

        manager.Process(TestSnapshot.Create(FpA, rupees: 5));            // fpA baseline = 5
        var changes = manager.Process(TestSnapshot.Create(FpB, rupees: 0)); // fpB baseline = 0

        Assert.Empty(changes); // no "rupees 5 -> 0" cross-session change
    }

    [Fact]
    public void MarkConnectionLost_twice_fires_only_one_ConnectionLost_event()
    {
        var manager = new SessionManager();
        var lost = new List<GameSession>();
        manager.ConnectionLost += lost.Add;

        manager.Process(TestSnapshot.Create(FpA));
        manager.MarkConnectionLost();
        manager.MarkConnectionLost();

        Assert.Single(lost);
        Assert.Equal(SessionPhase.Disconnected, manager.Current!.Phase);
    }

    [Fact]
    public void MarkSessionEnded_twice_fires_only_one_SessionEnded_event()
    {
        var manager = new SessionManager();
        var ended = new List<GameSession>();
        manager.SessionEnded += ended.Add;

        manager.Process(TestSnapshot.Create(FpA));
        manager.MarkSessionEnded();
        manager.MarkSessionEnded();

        Assert.Single(ended);
        Assert.Null(manager.Current);
    }

    [Fact]
    public void Every_generated_change_carries_the_sessions_id()
    {
        var manager = new SessionManager();

        manager.Process(TestSnapshot.Create(FpA, rupees: 0, life: 12, map: "KokiriForest_Child"));
        var changes = manager.Process(TestSnapshot.Create(FpA, rupees: 1, life: 11, map: "HyruleField_Child"));

        Assert.NotEmpty(changes);
        Assert.All(changes, c => Assert.Equal(manager.Current!.Id, c.SessionId));
    }
}
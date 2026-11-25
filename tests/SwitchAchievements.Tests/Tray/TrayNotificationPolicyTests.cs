using SwitchAchievements.App.Services;
using SwitchAchievements.Core;
using SwitchAchievements.Tests.Identity;
using SwitchAchievements.Tests.MonitoringIdentity;

namespace SwitchAchievements.Tests.Tray;

/// <summary>M5.7 Task 4: which monitoring observations become tray notifications.</summary>
public class TrayNotificationPolicyTests
{
    private const string GamePath = @"C:\Recomp Library\longshot\Longshot\bin\longshot.exe";
    private static readonly DateTime Start = new(2026, 11, 24, 5, 0, 0, DateTimeKind.Utc);

    internal static GameIdentification Identify(string kind, int pid = 100, DateTime? start = null)
        => GameIdentityResolver.Resolve(IdentityTestData.OotCatalog(),
            IdentityEvidence.For(kind, new GameProcessInfo(pid, GamePath, start ?? Start)));

    private readonly TrayNotificationPolicy _policy = new();
    private readonly object _service = new();

    // ---------------- visibility rule ----------------

    [Theory]
    [InlineData(true, false, false)]  // visible, normal: not hidden
    [InlineData(false, false, true)]  // hidden to the tray
    [InlineData(true, true, true)]    // minimized
    [InlineData(false, true, true)]
    public void Window_is_hidden_when_not_visible_or_minimized(bool visible, bool minimized, bool hidden)
        => Assert.Equal(hidden, TrayNotificationPolicy.IsWindowHidden(visible, minimized));

    // ---------------- supported game ----------------

    [Fact]
    public void Supported_game_notifies_once()
    {
        var id = Identify("identified");

        var first = _policy.Observe(_service, MonitoringState.Monitoring, id, windowHidden: true);
        var second = _policy.Observe(_service, MonitoringState.Monitoring, id, windowHidden: true);

        Assert.NotNull(first);
        Assert.Equal(TrayNotificationKind.SupportedGameDetected, first.Kind);
        Assert.Equal("Game detected", first.Title);
        Assert.Contains("The Legend of Zelda: Ocarina of Time", first.Text, StringComparison.Ordinal);
        Assert.False(first.IsError);
        Assert.Null(second);
    }

    [Fact]
    public void Same_fingerprint_with_a_new_identification_object_does_not_notify_again()
    {
        Assert.NotNull(_policy.Observe(_service, MonitoringState.Monitoring, Identify("identified"), true));
        Assert.Null(_policy.Observe(_service, MonitoringState.Monitoring, Identify("identified"), true));
    }

    [Fact]
    public void Reconnecting_to_the_same_process_does_not_notify_again()
    {
        var id = Identify("identified");
        Assert.NotNull(_policy.Observe(_service, MonitoringState.Monitoring, id, true));

        Assert.Null(_policy.Observe(_service, MonitoringState.Reconnecting, id, true));
        Assert.Null(_policy.Observe(_service, MonitoringState.Connecting, id, true));
        Assert.Null(_policy.Observe(_service, MonitoringState.Monitoring, id, true));
    }

    [Fact]
    public void A_new_fingerprint_notifies()
    {
        Assert.NotNull(_policy.Observe(_service, MonitoringState.Monitoring, Identify("identified", pid: 100), true));
        Assert.NotNull(_policy.Observe(_service, MonitoringState.Monitoring, Identify("identified", pid: 200), true));
    }

    [Fact]
    public void Same_pid_with_a_different_start_time_is_a_new_process()
    {
        Assert.NotNull(_policy.Observe(_service, MonitoringState.Monitoring, Identify("identified", pid: 100), true));
        Assert.NotNull(_policy.Observe(_service, MonitoringState.Monitoring, Identify("identified", pid: 100, start: Start.AddMinutes(5)), true));
    }

    [Fact]
    public void Identification_outside_monitoring_does_not_notify_and_is_not_consumed()
    {
        var id = Identify("identified");
        Assert.Null(_policy.Observe(_service, MonitoringState.Connecting, id, true));
        Assert.NotNull(_policy.Observe(_service, MonitoringState.Monitoring, id, true));
    }

    [Fact]
    public void After_a_restart_the_same_running_process_is_not_announced_again()
    {
        var oldService = new object();
        var newService = new object();
        Assert.NotNull(_policy.Observe(oldService, MonitoringState.Monitoring, Identify("identified"), true));
        Assert.Null(_policy.Observe(newService, MonitoringState.Monitoring, Identify("identified"), true));
    }

    // ---------------- unsupported / unknown / conflict ----------------

    [Fact]
    public void Unsupported_game_notifies_once()
    {
        var id = Identify("unsupported");

        var first = _policy.Observe(_service, MonitoringState.Monitoring, id, true);
        Assert.NotNull(first);
        Assert.Equal(TrayNotificationKind.UnsupportedGameDetected, first.Kind);
        Assert.Null(_policy.Observe(_service, MonitoringState.Monitoring, id, true));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("conflict")]
    public void Unknown_and_conflict_never_notify(string kind)
    {
        var id = Identify(kind);
        Assert.Null(_policy.Observe(_service, MonitoringState.Monitoring, id, true));
        Assert.Null(_policy.Observe(_service, MonitoringState.Monitoring, id, true));
    }

    [Fact]
    public void Monitoring_without_identification_does_not_notify()
        => Assert.Null(_policy.Observe(_service, MonitoringState.Monitoring, null, true));

    [Theory]
    [InlineData(MonitoringState.Stopped)]
    [InlineData(MonitoringState.Searching)]
    [InlineData(MonitoringState.Connecting)]
    [InlineData(MonitoringState.Reconnecting)]
    [InlineData(MonitoringState.Ambiguous)]
    public void Ordinary_states_never_notify(MonitoringState state)
        => Assert.Null(_policy.Observe(_service, state, Identify("identified"), true));

    // ---------------- error ----------------

    [Fact]
    public void Error_notifies_once_per_service()
    {
        var first = _policy.Observe(_service, MonitoringState.Error, null, true);

        Assert.NotNull(first);
        Assert.Equal(TrayNotificationKind.MonitoringError, first.Kind);
        Assert.True(first.IsError);
        Assert.Null(_policy.Observe(_service, MonitoringState.Error, null, true));
    }

    [Fact]
    public void A_new_service_can_independently_notify_an_error()
    {
        Assert.NotNull(_policy.Observe(new object(), MonitoringState.Error, null, true));
        Assert.NotNull(_policy.Observe(new object(), MonitoringState.Error, null, true));
    }

    // ---------------- visibility ----------------

    [Fact]
    public void Events_seen_while_visible_are_consumed_and_never_replayed()
    {
        var id = Identify("identified");
        Assert.Null(_policy.Observe(_service, MonitoringState.Monitoring, id, windowHidden: false));
        Assert.Null(_policy.Observe(_service, MonitoringState.Error, null, windowHidden: false));

        // The window is hidden afterwards: nothing is replayed.
        Assert.Null(_policy.Observe(_service, MonitoringState.Monitoring, id, windowHidden: true));
        Assert.Null(_policy.Observe(_service, MonitoringState.Error, null, windowHidden: true));
    }

    [Fact]
    public void Events_while_hidden_generate_notifications()
    {
        Assert.NotNull(_policy.Observe(_service, MonitoringState.Monitoring, Identify("identified"), windowHidden: true));
        Assert.NotNull(_policy.Observe(_service, MonitoringState.Error, null, windowHidden: true));
    }

    // ---------------- restart failure ----------------

    [Theory]
    [InlineData(RestartOutcome.Failed)]
    [InlineData(RestartOutcome.Refused)]
    public void Restart_failure_notifies_only_while_hidden(RestartOutcome outcome)
    {
        var hidden = TrayNotificationPolicy.ForRestartOutcome(outcome, windowHidden: true);
        Assert.NotNull(hidden);
        Assert.Equal(TrayNotificationKind.RestartFailed, hidden.Kind);
        Assert.True(hidden.IsError);

        Assert.Null(TrayNotificationPolicy.ForRestartOutcome(outcome, windowHidden: false));
    }

    [Theory]
    [InlineData(RestartOutcome.Succeeded)]
    [InlineData(RestartOutcome.Ignored)]
    public void Other_restart_outcomes_never_notify(RestartOutcome outcome)
        => Assert.Null(TrayNotificationPolicy.ForRestartOutcome(outcome, windowHidden: true));
}

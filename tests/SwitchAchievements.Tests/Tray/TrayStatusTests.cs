using SwitchAchievements.App.Services;
using SwitchAchievements.App.ViewModels;

namespace SwitchAchievements.Tests.Tray;

/// <summary>M5.7 Task 4: the tray's view state.</summary>
public class TrayStatusTests
{
    private static string Summary(MonitoringState state)
        => DashboardFormat.StatusSummary(state, IdentificationDisplayStatus.None, null);

    [Theory]
    [InlineData(MonitoringState.Stopped, "Monitoring stopped")]
    [InlineData(MonitoringState.Searching, "Searching for Longshot")]
    [InlineData(MonitoringState.Connecting, "Connecting to Longshot")]
    [InlineData(MonitoringState.Monitoring, "Monitoring Longshot")]
    [InlineData(MonitoringState.Reconnecting, "Reconnecting to Longshot")]
    [InlineData(MonitoringState.Ambiguous, "Multiple game processes found")]
    [InlineData(MonitoringState.Error, "Monitoring error")]
    public void Every_state_shows_the_dashboard_summary(MonitoringState state, string expected)
    {
        var view = TrayStatus.Compute(state, Summary(state), restartInProgress: false, restartFailed: false);

        Assert.Equal(expected, view.StatusText);
        Assert.Equal("Switch Achievements — " + expected, view.Tooltip);
    }

    [Theory]
    [InlineData(MonitoringState.Stopped)]
    [InlineData(MonitoringState.Searching)]
    [InlineData(MonitoringState.Connecting)]
    [InlineData(MonitoringState.Monitoring)]
    [InlineData(MonitoringState.Reconnecting)]
    [InlineData(MonitoringState.Ambiguous)]
    public void Restart_is_hidden_outside_error(MonitoringState state)
    {
        var view = TrayStatus.Compute(state, Summary(state), restartInProgress: false, restartFailed: false);

        Assert.False(view.RestartVisible);
        Assert.False(view.RestartEnabled);
    }

    [Fact]
    public void Restart_is_visible_and_enabled_in_error()
    {
        var view = TrayStatus.Compute(MonitoringState.Error, Summary(MonitoringState.Error), false, false);

        Assert.True(view.RestartVisible);
        Assert.True(view.RestartEnabled);
    }

    [Theory]
    [InlineData(MonitoringState.Error)]
    [InlineData(MonitoringState.Stopped)] // the old service is being replaced
    public void Restart_stays_visible_but_disabled_while_an_attempt_runs(MonitoringState state)
    {
        var view = TrayStatus.Compute(state, Summary(state), restartInProgress: true, restartFailed: false);

        Assert.True(view.RestartVisible);
        Assert.False(view.RestartEnabled);
    }

    [Fact]
    public void Failed_restart_recovery_shows_restart_failed_and_keeps_restart_available()
    {
        // The host has no service, so the dashboard truthfully reports Stopped.
        var view = TrayStatus.Compute(MonitoringState.Stopped, Summary(MonitoringState.Stopped), false, restartFailed: true);

        Assert.Equal("Monitoring error — restart failed", view.StatusText);
        Assert.Equal("Switch Achievements — Monitoring error — restart failed", view.Tooltip);
        Assert.True(view.RestartVisible);
        Assert.True(view.RestartEnabled);
    }

    [Fact]
    public void Recovery_retry_in_progress_disables_restart()
    {
        var view = TrayStatus.Compute(MonitoringState.Stopped, Summary(MonitoringState.Stopped), restartInProgress: true, restartFailed: true);

        Assert.True(view.RestartVisible);
        Assert.False(view.RestartEnabled);
    }

    [Fact]
    public void Identified_game_summary_is_shown()
    {
        var summary = DashboardFormat.StatusSummary(MonitoringState.Monitoring, IdentificationDisplayStatus.Identified, "The Legend of Zelda: Ocarina of Time");
        var view = TrayStatus.Compute(MonitoringState.Monitoring, summary, false, false);

        Assert.Equal("Monitoring — The Legend of Zelda: Ocarina of Time", view.StatusText);
        Assert.Equal("Switch Achievements — Monitoring — The Legend of Zelda: Ocarina of Time", view.Tooltip);
    }

    [Fact]
    public void A_long_summary_is_shortened_to_the_tooltip_limit()
    {
        var summary = DashboardFormat.StatusSummary(MonitoringState.Monitoring, IdentificationDisplayStatus.Identified, new string('x', 300));
        var view = TrayStatus.Compute(MonitoringState.Monitoring, summary, false, false);

        Assert.Equal(TrayStatus.MaxTooltipLength, view.Tooltip.Length);
        Assert.StartsWith("Switch Achievements — Monitoring — xxx", view.Tooltip, StringComparison.Ordinal);
        Assert.EndsWith("…", view.Tooltip, StringComparison.Ordinal);
        Assert.Equal(summary, view.StatusText); // the menu item keeps the full text
    }

    [Fact]
    public void A_tooltip_exactly_at_the_limit_is_kept()
    {
        var status = new string('y', TrayStatus.MaxTooltipLength - "Switch Achievements — ".Length);
        Assert.Equal(TrayStatus.MaxTooltipLength, TrayStatus.Tooltip(status).Length);
        Assert.DoesNotContain("…", TrayStatus.Tooltip(status), StringComparison.Ordinal);
    }
}

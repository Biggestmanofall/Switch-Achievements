using SwitchAchievements.App.Services;
using SwitchAchievements.App.ViewModels;

namespace SwitchAchievements.Tests.Dashboard;

/// <summary>M5.7 Task 2: deterministic dashboard text formatting.</summary>
public class DashboardFormatTests
{
    [Theory]
    [InlineData(MonitoringState.Stopped, "Stopped")]
    [InlineData(MonitoringState.Searching, "Searching for game")]
    [InlineData(MonitoringState.Connecting, "Connecting")]
    [InlineData(MonitoringState.Monitoring, "Monitoring")]
    [InlineData(MonitoringState.Reconnecting, "Reconnecting")]
    [InlineData(MonitoringState.Ambiguous, "Multiple game processes found")]
    [InlineData(MonitoringState.Error, "Error")]
    public void Monitoring_state_text(MonitoringState state, string expected)
        => Assert.Equal(expected, DashboardFormat.MonitoringState(state));

    [Fact]
    public void Every_monitoring_state_has_text()
        => Assert.All(Enum.GetValues<MonitoringState>(), s => Assert.NotEqual("Unknown", DashboardFormat.MonitoringState(s)));

    [Theory]
    [InlineData(MonitoringState.Monitoring, ConnectionStatus.Connected, "Connected")]
    [InlineData(MonitoringState.Connecting, ConnectionStatus.Connecting, "Connecting")]
    [InlineData(MonitoringState.Reconnecting, ConnectionStatus.Connecting, "Connecting")]
    [InlineData(MonitoringState.Searching, ConnectionStatus.NotConnected, "Not connected")]
    [InlineData(MonitoringState.Ambiguous, ConnectionStatus.NotConnected, "Not connected")]
    [InlineData(MonitoringState.Error, ConnectionStatus.NotConnected, "Not connected")]
    [InlineData(MonitoringState.Stopped, ConnectionStatus.NotConnected, "Not connected")]
    public void Connection_is_derived_from_state(MonitoringState state, ConnectionStatus expected, string text)
    {
        Assert.Equal(expected, DashboardFormat.Connection(state));
        Assert.Equal(text, DashboardFormat.Connection(expected));
    }

    [Theory]
    [InlineData(IdentificationDisplayStatus.None, "No game detected")]
    [InlineData(IdentificationDisplayStatus.Identifying, "Identifying game…")]
    [InlineData(IdentificationDisplayStatus.Unknown, "Unrecognized game")]
    [InlineData(IdentificationDisplayStatus.Unsupported, "Unsupported game or version")]
    [InlineData(IdentificationDisplayStatus.Conflict, "Game could not be identified")]
    [InlineData(IdentificationDisplayStatus.Unavailable, "Game identification unavailable")]
    public void Identification_text_is_generic(IdentificationDisplayStatus status, string expected)
    {
        Assert.Equal(expected, DashboardFormat.Identification(status));
        Assert.Equal(expected, DashboardFormat.Identification(status, "Some Game")); // a name never leaks into non-identified text
    }

    [Fact]
    public void Identified_text_is_the_catalog_name()
        => Assert.Equal("The Legend of Zelda: Ocarina of Time",
            DashboardFormat.Identification(IdentificationDisplayStatus.Identified, "The Legend of Zelda: Ocarina of Time"));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "¼")]
    [InlineData(2, "½")]
    [InlineData(3, "¾")]
    [InlineData(4, "1")]
    [InlineData(9, "2¼")]
    [InlineData(11, "2¾")]
    [InlineData(12, "3")]
    [InlineData(80, "20")]
    public void Quarter_hearts(int quarters, string expected) => Assert.Equal(expected, DashboardFormat.Hearts(quarters));

    [Theory]
    [InlineData(11, 12, "2¾ / 3 hearts")]
    [InlineData(12, 12, "3 / 3 hearts")]
    [InlineData(0, 12, "0 / 3 hearts")]          // zero is a real value, not missing
    [InlineData(2, 4, "½ / 1 heart")]
    [InlineData(6, null, "1½ hearts")]
    [InlineData(4, null, "1 heart")]
    [InlineData(null, 12, "—")]
    [InlineData(null, null, "—")]
    [InlineData(-4, 12, "—")]
    public void Life_and_max_life(int? life, int? max, string expected) => Assert.Equal(expected, DashboardFormat.Hearts(life, max));

    [Theory]
    [InlineData(0, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(12, "12 seconds")]
    [InlineData(59, "59 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(150, "2 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(3600 + 60, "1 hour 1 minute")]
    [InlineData(2 * 3600 + 5 * 60 + 9, "2 hours 5 minutes")]
    [InlineData(-5, "0 seconds")]
    public void Elapsed(int seconds, string expected) => Assert.Equal(expected, DashboardFormat.Elapsed(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(65, "0:01:05")]
    [InlineData(3909, "1:05:09")]
    [InlineData(26 * 3600, "26:00:00")]
    [InlineData(-1, "0:00:00")]
    public void Duration(int seconds, string expected) => Assert.Equal(expected, DashboardFormat.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Freshness_text()
    {
        Assert.Equal("Last gameplay update just now", DashboardFormat.Freshness(GameplayDataStatus.Fresh, TimeSpan.FromMilliseconds(400)));
        Assert.Equal("Last gameplay update 12 seconds ago", DashboardFormat.Freshness(GameplayDataStatus.Fresh, TimeSpan.FromSeconds(12)));
        Assert.Equal("Last gameplay update 3 minutes ago", DashboardFormat.Freshness(GameplayDataStatus.Stale, TimeSpan.FromMinutes(3)));
        Assert.Equal("Waiting for gameplay data…", DashboardFormat.Freshness(GameplayDataStatus.Waiting, TimeSpan.Zero));
        Assert.Equal("No gameplay data", DashboardFormat.Freshness(GameplayDataStatus.None, TimeSpan.Zero));
    }

    [Fact]
    public void Stale_threshold_is_two_minutes() => Assert.Equal(TimeSpan.FromMinutes(2), DashboardFormat.StaleThreshold);

    [Theory]
    [InlineData(MonitoringState.Stopped, IdentificationDisplayStatus.None, null, "Monitoring stopped")]
    [InlineData(MonitoringState.Searching, IdentificationDisplayStatus.None, null, "Searching for Longshot")]
    [InlineData(MonitoringState.Connecting, IdentificationDisplayStatus.Identifying, null, "Connecting to Longshot")]
    [InlineData(MonitoringState.Reconnecting, IdentificationDisplayStatus.Identified, "OoT", "Reconnecting to Longshot")]
    [InlineData(MonitoringState.Ambiguous, IdentificationDisplayStatus.None, null, "Multiple game processes found")]
    [InlineData(MonitoringState.Error, IdentificationDisplayStatus.None, null, "Monitoring error")]
    [InlineData(MonitoringState.Monitoring, IdentificationDisplayStatus.Identified, "The Legend of Zelda: Ocarina of Time", "Monitoring — The Legend of Zelda: Ocarina of Time")]
    [InlineData(MonitoringState.Monitoring, IdentificationDisplayStatus.Unknown, null, "Monitoring — unrecognized game")]
    [InlineData(MonitoringState.Monitoring, IdentificationDisplayStatus.Unsupported, "Leaked", "Monitoring — unsupported game")]
    [InlineData(MonitoringState.Monitoring, IdentificationDisplayStatus.Conflict, null, "Monitoring — game could not be identified")]
    [InlineData(MonitoringState.Monitoring, IdentificationDisplayStatus.Unavailable, null, "Monitoring Longshot")]
    public void Status_summary(MonitoringState state, IdentificationDisplayStatus id, string? name, string expected)
        => Assert.Equal(expected, DashboardFormat.StatusSummary(state, id, name));
}

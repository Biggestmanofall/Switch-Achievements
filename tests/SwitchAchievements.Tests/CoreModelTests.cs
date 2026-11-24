using SwitchAchievements.Core;

namespace SwitchAchievements.Tests;

public class CoreModelTests
{
    [Fact]
    public void GameId_Unknown_has_expected_value()
    {
        Assert.Equal("unknown", GameId.Unknown.Value);
    }

    [Fact]
    public void SessionId_NewId_is_unique()
    {
        Assert.NotEqual(SessionId.NewId(), SessionId.NewId());
    }

    [Fact]
    public void NormalizedSnapshot_exposes_process_fingerprint()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var snapshot = new NormalizedSnapshot
        {
            IntegrationId = "longshot",
            GameId = GameId.Unknown,
            SourceProcessId = 1234,
            SourceProcessStartUtc = start,
            Rupees = 5,
        };

        Assert.Equal(new ProcessFingerprint(1234, start), snapshot.Fingerprint);
        Assert.Equal(5, snapshot.Rupees);
    }

    [Fact]
    public void ProcessFingerprint_equivalent_utc_start_times_are_equal()
    {
        var start = new DateTime(2026, 1, 1, 12, 30, 45, DateTimeKind.Utc);
        var equivalent = new DateTime(2026, 1, 1, 12, 30, 45, DateTimeKind.Utc);

        Assert.Equal(new ProcessFingerprint(1234, start), new ProcessFingerprint(1234, equivalent));
    }

    [Fact]
    public void ProcessFingerprint_different_start_times_are_not_equal()
    {
        var start = new DateTime(2026, 1, 1, 12, 30, 45, DateTimeKind.Utc);
        var later = new DateTime(2026, 1, 1, 12, 30, 46, DateTimeKind.Utc);

        Assert.NotEqual(new ProcessFingerprint(1234, start), new ProcessFingerprint(1234, later));
    }
}
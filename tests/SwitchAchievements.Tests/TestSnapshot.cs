using SwitchAchievements.Core;

namespace SwitchAchievements.Tests;

/// <summary>Builds synthetic normalized snapshots for tests (no game required).</summary>
internal static class TestSnapshot
{
    public static NormalizedSnapshot Create(
        ProcessFingerprint fingerprint,
        int? rupees = null,
        int? life = null,
        int? maxLife = null,
        string? map = null,
        string? age = null,
        bool? eventPlaying = null,
        bool? loading = null,
        IReadOnlyDictionary<string, long?>? flags = null,
        long gameFrame = 0,
        DateTime? receivedUtc = null)
    {
        return new NormalizedSnapshot
        {
            IntegrationId = "longshot",
            GameId = GameId.Unknown,
            SourceProcessId = fingerprint.ProcessId,
            SourceProcessStartUtc = fingerprint.ProcessStartUtc,
            GameFrame = gameFrame,
            ReceivedUtc = receivedUtc ?? DateTime.UtcNow,
            Map = map,
            Age = age,
            EventPlaying = eventPlaying,
            Loading = loading,
            Life = life,
            MaxLife = maxLife,
            Rupees = rupees,
            Flags = flags,
        };
    }
}
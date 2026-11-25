using System.Reflection;
using SwitchAchievements.Core;

namespace SwitchAchievements.Tests.MonitoringIdentity;

/// <summary>M5.6.5: Core NormalizedSnapshot.WithGameId and the session rule it relies on.</summary>
public class NormalizedSnapshotWithGameIdTests
{
    private static readonly PropertyInfo[] Settable = typeof(NormalizedSnapshot)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite)
        .ToArray();

    // A distinct, non-default sample per property type. A future property of a new
    // type fails here with a clear message, so WithGameId cannot silently miss it.
    private static object Sample(PropertyInfo p)
    {
        var type = p.PropertyType;
        if (type == typeof(string)) return "sample-" + p.Name;
        if (type == typeof(GameId)) return new GameId("original-game");
        if (type == typeof(int)) return 4242;
        if (type == typeof(long)) return 9_876_543_210L;
        if (type == typeof(DateTime)) return new DateTime(2026, 11, 24, 5, 51, 31, 839, DateTimeKind.Utc).AddMinutes(p.Name.Length);
        if (type == typeof(bool?)) return true;
        if (type == typeof(int?)) return 17 + p.Name.Length;
        if (type == typeof(IReadOnlyDictionary<string, long?>)) return new Dictionary<string, long?> { ["PlayerStatus.Life"] = 12 };
        Assert.Fail($"Add a sample value for NormalizedSnapshot.{p.Name} ({type}) and make sure WithGameId copies it.");
        return null!;
    }

    private static NormalizedSnapshot FullyPopulated()
    {
        var snapshot = new NormalizedSnapshot { IntegrationId = "x", GameId = GameId.Unknown };
        foreach (var property in Settable)
            property.SetValue(snapshot, Sample(property));
        return snapshot;
    }

    [Fact]
    public void Every_settable_property_has_a_non_default_sample()
    {
        Assert.NotEmpty(Settable);
        var snapshot = FullyPopulated();
        foreach (var property in Settable)
            Assert.NotEqual(property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null, property.GetValue(snapshot));
    }

    [Fact]
    public void WithGameId_replaces_only_the_game_id_and_copies_every_other_property()
    {
        var original = FullyPopulated();
        var gameId = new GameId("zelda-ocarina-of-time");

        var copy = original.WithGameId(gameId);

        Assert.NotSame(original, copy);
        Assert.Equal(gameId, copy.GameId);
        foreach (var property in typeof(NormalizedSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == nameof(NormalizedSnapshot.GameId))
                continue;
            Assert.True(Equals(property.GetValue(original), property.GetValue(copy)), $"{property.Name} was not copied");
        }

        Assert.Equal(original.Fingerprint, copy.Fingerprint);
        Assert.Equal(new GameId("original-game"), original.GameId); // the original is unchanged
    }

    [Fact]
    public void WithGameId_rejects_null()
        => Assert.Throws<ArgumentNullException>(() => FullyPopulated().WithGameId(null!));

    [Fact]
    public void A_new_session_takes_the_game_id_of_its_first_snapshot_and_keeps_it()
    {
        // The rule M5.6.5 relies on: stamping happens before SessionManager.Process.
        var fingerprint = new ProcessFingerprint(100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var manager = new SessionManager();

        manager.Process(TestSnapshot.Create(fingerprint).WithGameId(new GameId("zelda-ocarina-of-time")));
        var session = manager.Current!;
        Assert.Equal("zelda-ocarina-of-time", session.GameId.Value);

        manager.MarkConnectionLost();
        manager.Process(TestSnapshot.Create(fingerprint, gameFrame: 2)); // reconnect, same process, unstamped
        Assert.Same(session, manager.Current);
        Assert.Equal("zelda-ocarina-of-time", manager.Current!.GameId.Value); // never overwritten
    }
}

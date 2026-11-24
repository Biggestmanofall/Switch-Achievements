using SwitchAchievements.Core;

namespace SwitchAchievements.Tests;

public class SnapshotComparerTests
{
    private static readonly ProcessFingerprint Fingerprint =
        new(1234, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static readonly SessionId Session = SessionId.NewId();

    [Fact]
    public void Identical_snapshots_produce_no_changes()
    {
        var a = TestSnapshot.Create(Fingerprint, rupees: 5, life: 12, map: "KokiriForest_Child");
        var b = TestSnapshot.Create(Fingerprint, rupees: 5, life: 12, map: "KokiriForest_Child");

        Assert.Empty(SnapshotComparer.Compare(Session, a, b));
    }

    [Fact]
    public void Rupee_increase_produces_a_rupee_change()
    {
        var a = TestSnapshot.Create(Fingerprint, rupees: 0);
        var b = TestSnapshot.Create(Fingerprint, rupees: 1);

        var change = Assert.Single(SnapshotComparer.Compare(Session, a, b));

        Assert.Equal("Rupees", change.Field);
        Assert.Equal(0, change.PreviousValue);
        Assert.Equal(1, change.NewValue);
        Assert.Equal(Session, change.SessionId);
    }

    [Fact]
    public void Life_decrease_produces_a_life_change()
    {
        var a = TestSnapshot.Create(Fingerprint, life: 12);
        var b = TestSnapshot.Create(Fingerprint, life: 11);

        var change = Assert.Single(SnapshotComparer.Compare(Session, a, b));

        Assert.Equal("Life", change.Field);
        Assert.Equal(12, change.PreviousValue);
        Assert.Equal(11, change.NewValue);
    }

    [Fact]
    public void Multiple_fields_changing_in_one_snapshot_are_all_detected()
    {
        var a = TestSnapshot.Create(Fingerprint, rupees: 0, life: 12, map: "KokiriForest_Child");
        var b = TestSnapshot.Create(Fingerprint, rupees: 1, life: 11, map: "HyruleField_Child");

        var fields = SnapshotComparer.Compare(Session, a, b)
            .Select(c => c.Field)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Life", "Map", "Rupees" }, fields);
    }

    [Fact]
    public void Named_flag_change_is_detected()
    {
        var a = TestSnapshot.Create(Fingerprint, flags: new Dictionary<string, long?> { ["PlayerStatus.Life"] = 12 });
        var b = TestSnapshot.Create(Fingerprint, flags: new Dictionary<string, long?> { ["PlayerStatus.Life"] = 11 });

        var change = Assert.Single(SnapshotComparer.Compare(Session, a, b));

        Assert.Equal("flag:PlayerStatus.Life", change.Field);
        Assert.Equal(12L, change.PreviousValue);
        Assert.Equal(11L, change.NewValue);
    }

    [Fact]
    public void Unnamed_hash_flags_are_ignored()
    {
        var a = TestSnapshot.Create(Fingerprint, flags: new Dictionary<string, long?> { ["0xe53eb6d1"] = 500 });
        var b = TestSnapshot.Create(Fingerprint, flags: new Dictionary<string, long?> { ["0xe53eb6d1"] = 501 });

        Assert.Empty(SnapshotComparer.Compare(Session, a, b));
    }

    [Fact]
    public void Field_becoming_unavailable_is_not_a_change()
    {
        var a = TestSnapshot.Create(Fingerprint, rupees: 5);
        var b = TestSnapshot.Create(Fingerprint); // rupees null

        Assert.Empty(SnapshotComparer.Compare(Session, a, b));
    }

    [Fact]
    public void Field_becoming_available_is_not_a_change()
    {
        var a = TestSnapshot.Create(Fingerprint); // rupees null
        var b = TestSnapshot.Create(Fingerprint, rupees: 5);

        Assert.Empty(SnapshotComparer.Compare(Session, a, b));
    }

    [Fact]
    public void Life_null_is_not_reinterpreted_as_zero()
    {
        var a = TestSnapshot.Create(Fingerprint, life: 12);
        var b = TestSnapshot.Create(Fingerprint); // life null, not 0

        Assert.Empty(SnapshotComparer.Compare(Session, a, b));
        Assert.Empty(SnapshotComparer.Compare(Session, b, a));
    }

    [Fact]
    public void Boolean_state_null_is_not_reinterpreted_as_false()
    {
        var a = TestSnapshot.Create(Fingerprint, eventPlaying: true);
        var b = TestSnapshot.Create(Fingerprint); // event null, not false

        Assert.Empty(SnapshotComparer.Compare(Session, a, b));
        Assert.Empty(SnapshotComparer.Compare(Session, b, a));
    }

    [Fact]
    public void Map_and_age_null_are_skipped_on_either_side()
    {
        var a = TestSnapshot.Create(Fingerprint, map: "KokiriForest_Child", age: "child");
        var b = TestSnapshot.Create(Fingerprint); // map/age null

        Assert.Empty(SnapshotComparer.Compare(Session, a, b));
        Assert.Empty(SnapshotComparer.Compare(Session, b, a));
    }

    [Fact]
    public void Flag_payload_absent_on_either_side_reports_no_flag_changes()
    {
        var a = TestSnapshot.Create(Fingerprint, flags: new Dictionary<string, long?> { ["PlayerStatus.Life"] = 12 });
        var b = TestSnapshot.Create(Fingerprint); // flags null

        Assert.Empty(SnapshotComparer.Compare(Session, a, b));
        Assert.Empty(SnapshotComparer.Compare(Session, b, a));
    }

    [Fact]
    public void Named_flag_value_null_is_skipped()
    {
        var a = TestSnapshot.Create(Fingerprint, flags: new Dictionary<string, long?> { ["Some.NamedFlag"] = 1 });
        var b = TestSnapshot.Create(Fingerprint, flags: new Dictionary<string, long?> { ["Some.NamedFlag"] = null });

        Assert.Empty(SnapshotComparer.Compare(Session, a, b));
        Assert.Empty(SnapshotComparer.Compare(Session, b, a));
    }
}
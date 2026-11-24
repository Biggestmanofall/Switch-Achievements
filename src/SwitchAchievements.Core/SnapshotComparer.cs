namespace SwitchAchievements.Core;

/// <summary>
/// Compares two normalized snapshots and reports the gameplay-meaningful field
/// changes between them. It compares named flags only and ignores unnamed hash
/// keys (the noisy raw values observed in Phase 0).
/// </summary>
/// <remarks>
/// Nullable-field policy: for every normalized field, <c>null</c> means "no
/// value was sampled in this snapshot" — never a concrete gameplay value
/// (<c>false</c>, <c>0</c>, an empty string, etc.). The comparer therefore
/// reports a change only when both snapshots carry a concrete value for a field;
/// a <c>null</c>→value or value→<c>null</c> step is an availability change, not
/// gameplay, and is deliberately skipped. This keeps pause/loading gaps (where
/// Longshot stops sampling Link) from fabricating transitions. See
/// <c>docs/m2-snapshot-null-policy.md</c> for the field-by-field rationale.
/// </remarks>
public static class SnapshotComparer
{
    /// <summary>Prefix applied to flag-derived changes, e.g. "flag:PlayerStatus.Life".</summary>
    public const string FlagFieldPrefix = "flag:";

    public static IReadOnlyList<GameStateChange> Compare(
        SessionId sessionId,
        NormalizedSnapshot previousSnapshot,
        NormalizedSnapshot currentSnapshot)
    {
        var changes = new List<GameStateChange>();

        AddChange(changes, currentSnapshot, sessionId, "Map", previousSnapshot.Map, currentSnapshot.Map);
        AddChange(changes, currentSnapshot, sessionId, "Age", previousSnapshot.Age, currentSnapshot.Age);
        AddChange(changes, currentSnapshot, sessionId, "EventPlaying", previousSnapshot.EventPlaying, currentSnapshot.EventPlaying);
        AddChange(changes, currentSnapshot, sessionId, "Loading", previousSnapshot.Loading, currentSnapshot.Loading);
        AddChange(changes, currentSnapshot, sessionId, "Life", previousSnapshot.Life, currentSnapshot.Life);
        AddChange(changes, currentSnapshot, sessionId, "MaxLife", previousSnapshot.MaxLife, currentSnapshot.MaxLife);
        AddChange(changes, currentSnapshot, sessionId, "Rupees", previousSnapshot.Rupees, currentSnapshot.Rupees);

        CompareFlags(changes, currentSnapshot, sessionId, previousSnapshot.Flags, currentSnapshot.Flags);

        return changes;
    }

    private static void AddChange(
        List<GameStateChange> changes,
        NormalizedSnapshot currentSnapshot,
        SessionId sessionId,
        string field,
        object? previous,
        object? next)
    {
        // Null means "no data was sampled for this field", never a concrete
        // in-game value (see the class summary for the per-field policy). A
        // change can only be reported when both samples carry a real value:
        // a null->value or value->null step is an availability change, not
        // gameplay, so it is deliberately not reported. This also keeps
        // pause/loading gaps from fabricating transitions.
        if (previous is null || next is null)
            return;

        if (object.Equals(previous, next))
            return;

        changes.Add(new GameStateChange
        {
            SessionId = sessionId,
            Field = field,
            PreviousValue = previous,
            NewValue = next,
            GameFrame = currentSnapshot.GameFrame,
            ObservedUtc = currentSnapshot.ReceivedUtc,
        });
    }

    private static void CompareFlags(
        List<GameStateChange> changes,
        NormalizedSnapshot currentSnapshot,
        SessionId sessionId,
        IReadOnlyDictionary<string, long?>? previousFlags,
        IReadOnlyDictionary<string, long?>? currentFlags)
    {
        // No flag payload on either sample means flag data is unavailable, not
        // that every flag changed; skip all flag comparisons in that case.
        if (previousFlags is null || currentFlags is null)
            return;

        foreach (var (name, nextValue) in currentFlags)
        {
            if (!IsNamedFlag(name))
                continue;

            // A name present in only one sample has no concrete before/after
            // pair, so no change is reported for it.
            if (!previousFlags.TryGetValue(name, out var previousValue))
                continue;

            AddChange(changes, currentSnapshot, sessionId, FlagFieldPrefix + name, previousValue, nextValue);
        }
    }

    private static bool IsNamedFlag(string name)
    {
        // Unnamed values arrive as 8-digit MurmurHash3 keys ("0xe53eb6d1"); a
        // named flag (e.g. "PlayerStatus.Life") is not a bare 0x + 8 hex digits.
        if (name.Length != 10 || !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return true;

        for (var i = 2; i < name.Length; i++)
        {
            var c = name[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
                return true;
        }

        return false;
    }
}
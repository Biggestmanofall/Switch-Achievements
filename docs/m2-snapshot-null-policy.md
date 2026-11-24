# M2 — Snapshot nullable-field policy

This document records, field by field, where each `NormalizedSnapshot` value
comes from and what `null` means, so that the comparer's "skip a field when
either side is null" rule is a documented policy rather than a blanket
assumption.

Implemented in `SnapshotComparer`; pinned by the synthetic tests in
`tests/SwitchAchievements.Tests/SnapshotComparerTests.cs` (null behavior) and
`tests/SwitchAchievements.Tests/SessionManagerTests.cs` (session isolation and
lifecycle edge cases). No Longshot transport or protocol change is involved.

## The common rule

A normalized field's `null` means **"no value was sampled for this field in this
snapshot."** It never means `false`, `0`, an empty string, or any other concrete
gameplay value.

Consequences of the rule:

- Missing/unavailable data never fabricates a gameplay change. For example,
  `Life` going `12 → null` is **not** reported as "Life 12 → 0"; the field merely
  became unavailable.
- The reverse direction (`null → 12`) is also not a change: the field merely
  became available, it did not transition from a known previous value.
- A field is only reported when **both** snapshots carry a concrete value, so a
  `null` on either side is skipped.

## Field-by-field policy

| Field | Source (Longshot) | What `null` means | Policy |
|---|---|---|---|
| `Map` | `game.map` | map name not yet sampled (title/boot, or Link not currently updated) | report only when both sides concrete; a `null`↔name step is an availability change, not a map transition |
| `Age` | `game.age` | age not yet sampled | same as `Map`; only concrete `"child"`↔`"adult"` transitions are reported |
| `EventPlaying` | `game.event` | Link not being sampled (pause/title/loading) | `null` ≠ `false`; only concrete `true`↔`false` transitions are reported |
| `Loading` | `game.loading` | Link not being sampled (samples stop during load) | `null` ≠ `false`; only concrete transitions are reported |
| `Life` | `game.life` | not sampled | `null` ≠ `0`; only concrete quarter-heart changes are reported |
| `MaxLife` | `game.max_life` | not sampled | same as `Life`; only concrete changes reported |
| `Rupees` | `game.rupees` | not sampled | `null` ≠ `0`; only concrete rupee changes reported |
| Named flags | `game.flags` | see below | see below |

## Named flags

`Flags` is `IReadOnlyDictionary<string, long?>?`; there are two independent
levels of null.

1. **Dictionary-level (`Flags` is null).** `ToNormalized` maps an absent or empty
   `game.flags` object to `null`, so a null dictionary means "no flag payload in
   this sample." Flag comparison requires a flag payload in *both* snapshots;
   otherwise no flag changes are reported. A flag name that appears in only one
   snapshot also has no before/after pair and is skipped.

2. **Value-level (`long?` is null).** A flag's value is read as a `long?`:
   numbers → the number, booleans → `1`/`0`, numeric strings → the parsed number.
   An enum (rendered by the bridge as `#` + 8 hex chars), a non-numeric
   `String64`, JSON `null`, or any other unreadable value maps to `null`. A flag
   change is reported only when both snapshots hold a concrete numeric value for
   that flag name.

   Unnamed hash keys (`0x` + 8 hex digits) are ignored entirely regardless of
   value: they are the noisy `changes:true` stream identified in Phase 0, not
   progression state.

## Limitations

- **Reference-by-reference comparison.** The comparer only ever sees two
  snapshots. If the comparison reference (the previous snapshot) itself carried
  `null` for a field, a later concrete value yields a `null → value` step, which
  is skipped. The comparer therefore cannot recover a transition that was
  interrupted by a sample where the field was unavailable. This is a consequence
  of comparing consecutive samples, not of misreading `null`; carrying the
  last-known concrete value forward is an explicit design change that is out of
  scope for M2.
- **Flags are only compared when both snapshots carry a flag payload**, and only
  for names present in both snapshots.
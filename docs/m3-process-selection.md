# M3 — Process Selection & Two-Process Investigation

Status: **M3 complete.** Implemented, unit-tested, and verified across two live
launch/relaunch diagnostic runs. The named-pipe server ownership is consistent
but **experimentally** (not independently) confirmed — recorded below as an
optional, non-blocking future check.

## Findings

### Verified

- The two-process behavior is real and reproducible: exactly two `bin\longshot.exe`
  processes appeared in the two Phase 0 log captures (`9872`/`16992`,
  `4164`/`19712`) and in both M3 live diagnostic runs.
- In both M3 runs the detector saw **two** game binaries and **no**
  `Longshot\longshot.exe` stub, so the second process is not the ~191 KB launcher
  stub — both are the ~536 MB `bin\longshot.exe`.
- The process hierarchy is repeatable (2/2 launches) and was independently
  confirmed by Toolhelp32 and WMI:

  ```text
  LongshotBuilder.exe
      └── bin\longshot.exe (parent)     <- direct child of LongshotBuilder
              └── bin\longshot.exe (child)  <- spawned by the parent
  ```

  The game binary spawns a second copy of itself as a child; the parent starts
  ~30 ms before the child.
- The Phase 0 code selected `FirstOrDefault(p => p.IsGameBinary)` and reported
  `game: PID <first>`. That connected and received `state=play`, proving the
  bridge *responded*, but it never proved the chosen PID owned the pipe.

### Hypothesis (mechanism verified, functional role inferred)

- The *mechanism* is now verified: `bin\longshot.exe` spawns a second copy of
  itself as a child. The *functional role* of that child (rendering worker,
  supervisor, etc.) is **inferred, not instrumented** and remains unestablished.

### Experimental (to validate against an independent observation)

- `GetNamedPipeServerProcessId` invoked on a **client** handle. Microsoft's docs
  state the handle must be created by `CreateNamedPipe` (server end), so the
  client-handle result is unverified and is surfaced only as
  `experimentalServerPid`. It is never used to resolve selection.

### Pending (optional, non-blocking)

- Independent named-pipe ownership confirmation (Sysinternals Handle or Process
  Explorer). The experimental client-handle PID consistently reports the parent,
  but that has not been independently confirmed.
- The functional role of the spawned child process.

## Ownership mechanism (conclusion)

There is **no fully supported, documented Win32 API for a client to learn the
named-pipe server's PID.** The documented identity functions
(`GetNamedPipeServerProcessId` / `GetNamedPipeClientProcessId`) describe
server-end (`CreateNamedPipe`) handles. The underlying
`FILE_PIPE_REMOTE_INFORMATION` structure is WDK/`ntifs.h` material and is reached
via the undocumented NT `NtQueryInformationFile` path. Sysinternals Handle /
Process Explorer remain manual diagnostic tools only and are **not** runtime
dependencies.

Consequence: the production selector does **not** rely on ownership detection. It
keeps connectivity, bridge responsiveness, and process ownership as three
separate facts, and preserves `Ambiguous` for the two-process case unless an
independently verified owner is explicitly supplied.

## Three separate concepts

1. **Pipe connectivity** — can we connect to the pipe? (`Connected`)
2. **Bridge responsiveness** — does a read-only `status` return `ok:true`? (`Responding`)
3. **Server ownership** — which process hosts the server? (experimental / unverified)

`Selection` and `Readiness` are independent axes in `ProcessSelectionResult`.

## Selection rules

| Candidates | Bridge readiness | Selection |
|---|---|---|
| 0 | Any | None |
| 1 | Unavailable | Unique |
| 1 | Responding | Unique |
| 2+ | Unavailable | Ambiguous |
| 2+ | Responding | Ambiguous |
| 2+ | Independently verified owner | Unique (only when the verified-ownership capability is explicitly enabled) |

- Parent/child shape and start times are **diagnostic only**; they never
  auto-select one of several candidates.
- Never select the first candidate.
- Experimental `experimentalServerPid` never resolves ambiguity.

## Architecture

Core (`SwitchAchievements.Core`, reusable + pure):
- `ProcessSelectionResult.cs` — `ProcessSelectionStatus`, `BridgeReadinessStatus`,
  `VerifiedOwnership`, `ProcessSelectionResult`.
- `INamedPipeProbe.cs` — `BridgeProbeResult`, `INamedPipeProbe`.
- `ProcessSelector.cs` — pure selection policy.

Longshot (`SwitchAchievements.Longshot`, Windows-specific):
- `NativeMethods.cs` — Toolhelp32 (parent PID) + experimental `GetNamedPipeServerProcessId`.
- `WindowsProcessProvider.cs` — implements `IProcessProvider`.
- `NamedPipeBridgeProbe.cs` — connect + read-only `status` + experimental ownership
  on the same handle, with finite timeouts and guaranteed disposal.
- `LongshotProcessSelector.cs` — Longshot classification (`bin\longshot.exe`) +
  diagnostics, delegating to `ProcessSelector`.
- `ProcessDetection.cs` — now enumerates via `WindowsProcessProvider`.

CLI (`SwitchAchievements.Longshot.Poc`):
- `detect` reports selection + readiness and refuses to guess on ambiguity
  (exit 2); `detect --diagnose` prints per-process evidence and the bridge probe.
- `status` / `watch` / `quit` no longer `FirstOrDefault`; they use the selector
  and treat multiple candidates as not-selectable.
- Command lines are **not** collected anywhere.

## Tests (fake/synthetic; no game required)

`tests/SwitchAchievements.Tests/LongshotProcessSelectorTests.cs` covers the truth
table, classification, verified-owner resolution/mismatch, experimental PID
non-resolution, parent/child non-selection, and inaccessible-path handling.

## Live diagnostic

Ran on this machine over two launches via the project-local script.

Launch 1 (`detect --diagnose`):

```
longshot process(es): 2
  PID  31480  GAME  bin/longshot.exe  C:\Recomp Library\longshot\Longshot\bin\longshot.exe
  PID  26068  GAME  bin/longshot.exe  C:\Recomp Library\longshot\Longshot\bin\longshot.exe
bridge: connected=True responding=True experimentalServerPid=31480
  pid=31480 parent=20472 startUtc=2026-11-22T06:43:21.1750787Z path=...\bin\longshot.exe role=game
  pid=26068 parent=31480 startUtc=2026-11-22T06:43:21.2053892Z path=...\bin\longshot.exe role=game
  experimental pipe server pid=31480 (diagnostic only; not used for selection)
  2 candidates and no verified ownership; ambiguous
AMBIGUOUS: ... Refusing to guess a process.   EXIT=2
```

Launch 2 (`detect --diagnose`):

```
longshot process(es): 2
  PID  31452  GAME  bin/longshot.exe
  PID  11304  GAME  bin/longshot.exe
bridge: connected=True responding=True experimentalServerPid=31452
  pid=31452 parent=12020 ... role=game
  pid=11304 parent=31452 ... role=game
  experimental pipe server pid=31452 (diagnostic only; not used for selection)
  2 candidates and no verified ownership; ambiguous
AMBIGUOUS: ... Refusing to guess a process.   EXIT=2
```

WMI (`Win32_Process`) independently confirmed both chains, and the experimental
client-handle query returned the parent PID in both runs:

| Launch | Builder PID | parent game PID | child game PID | experimental server PID |
|---|---|---|---|---|
| 1 | 20472 | 31480 | 26068 | 31480 (parent) |
| 2 | 12020 | 31452 | 11304 | 31452 (parent) |

Evidence classification:

- **Verified:** the process hierarchy
  (`LongshotBuilder.exe → bin\longshot.exe (parent) → bin\longshot.exe (child)`)
  and its repeatability across two launches, confirmed independently by
  Toolhelp32 and WMI.
- **Experimental:** the client-handle `GetNamedPipeServerProcessId` query
  consistently reports the parent PID, but this has not been independently
  confirmed.
- **Pending (optional, non-blocking):** independent named-pipe ownership
  confirmation via Sysinternals Handle / Process Explorer.

Selection policy (unchanged and enforced): multiple candidates without verified
ownership remain `Ambiguous`; parent PID, process start time, and hierarchy stay
diagnostic-only; experimental ownership results never authorize selection; the
application keeps refusing to guess when multiple candidates exist. In both
launches the selector returned `Ambiguous` (exit `2`) and never silently chose
the first candidate.

**Conclusion: M3 is complete.** The outstanding ownership check is an optional
future investigation, not a blocker.

## Remaining limitations

- Two-process selection remains `Ambiguous` until a verified owner or documented
  role rule is established; no heuristic is introduced to hide this.
- Command-line evidence is deliberately omitted from the current implementation.
  (Superseded narrowly by the M5.6 R1 addendum below: command lines are now read,
  parsed for two arguments, and discarded — solely to recognise crash helpers.)

---

## Addendum (M5.6 R1) — crash-reporter helpers

Status: **implemented, unit-tested, and live-verified** (2026-11-23, Longshot
v3.0.0 / source `87cb36aa`). This resolves the M3 "two-process" finding without
changing any selection rule.

### Why there are two `bin\longshot.exe` processes

The second process is Longshot's crash-reporter helper. Early in startup the game
starts a copy of its own executable (`GetModuleFileNameW`), hence the identical path:

```text
bin\longshot.exe                                              <- the game (bridge host)
  └─ bin\longshot.exe --crash-watch <game pid> <run id> <game folder> <log> [<options>]
```

It waits for the game to end, then reads the run's crash records and log and may
show a "closed unexpectedly" window. A second mode,
`--crash-show <record> <game folder> <log> [<options>]`, is started (and waited for)
at launch when an earlier crash record was never shown. Source:
`LongshotBuilder\tools\host\hos_crash_dialog.{h,c}` (`hos_crash_watch_start`,
`hos_crash_args`, `spawn_exe`).

Why a helper can never be the game or own the pipe (source evidence):

- `main()` calls `hos_crash_helper_main` first; for a helper command line it
  returns before any game, renderer, input, or control-bridge code
  (`hos_main.c`, both `main` variants).
- The game starts its bridge (`hos_control_start`) *before* it spawns the helper,
  and the helper is created with `bInheritHandles = FALSE`; the pipe's security
  attributes are also non-inheritable (`control.c`).
- The pipe is created with `FILE_FLAG_FIRST_PIPE_INSTANCE`, so only one process
  can serve `longshot_control`.

`GetNamedPipeServerProcessId` remains experimental: Microsoft documents its handle
as one "created by the CreateNamedPipe function" (a server handle), so it is still
never used for selection.

### Rule: classify by the process's own arguments, then select as before

`LongshotProcessRoleClassifier` (Longshot project) assigns each `bin\longshot.exe`
candidate a role from its own command line:

| Role | Condition |
|---|---|
| `CrashWatchHelper` | `argv[1]` is exactly `--crash-watch` and the arguments pass Longshot's own validation: argc 6–7, `argv[2]` 1–20 ASCII digits and non-zero, `argv[3]` 32 lowercase hex digits, non-empty folder/log/(options) |
| `CrashShowHelper` | `argv[1]` is exactly `--crash-show`, argc 5–6, `argv[2]` a bare record name `(crash\|unclean)-YYYYMMDDTHHMMSSZ[-n-n].txt`, non-empty folder/log/(options) |
| `Game` | readable, and `argv[1]` is anything else (or absent) — including `--prepare-shaders` and near-misses such as `--CRASH-WATCH` |
| `Unknown` | unreadable (null, empty, access denied, exited, WMI failure, reader exception), or a helper flag with incomplete/malformed arguments |

Only `CrashWatchHelper` and `CrashShowHelper` are removed. `Game` and `Unknown`
are retained. The remaining list goes to `ProcessSelector.Select` **unchanged**.
Matching is exact and case-sensitive, mirroring Longshot's `strcmp`; parsing
follows `CommandLineToArgvW` rules, which is how Longshot quotes the helper's
arguments (`hos_crash_quote_arg`).

For a crash-watch helper the referenced game PID is related to the other
candidates (`Confirmed` when it is a non-helper candidate that started earlier;
`Orphaned` when absent; `Unconfirmed` otherwise, e.g. PID reuse). This is
**diagnostic only**: an orphaned helper is still a helper and is still excluded,
and the relationship never selects anything.

Classification runs whenever there is at least one path-matched candidate (never
when there are none). A lone process can be an orphaned helper — the helper
outlives the game — so skipping classification for a single candidate would let a
helper be selected as `Unique`. WMI is therefore not queried while no Longshot
process exists, nor while the service is in `Monitoring` (discovery is not running).

### Why this does not weaken M3

- The selection table above is unchanged: two or more genuine game candidates are
  still `Ambiguous`; verified ownership is still the only multi-candidate resolver.
- Parent PID, start time, and hierarchy still never select a process.
- Exclusion is a *classification* from Longshot's own documented helper contract,
  in the same category as the existing launcher-stub exclusion by path.
- Every uncertainty fails toward retention, therefore toward `Ambiguous`, never
  toward connecting: an unreadable or unrecognised process stays a candidate.
- A future Longshot that renames the helper arguments degrades to today's
  behaviour (`Ambiguous`), not to a wrong selection.

### Where it applies

- `IGameIntegration.SelectCandidates` (Core) — new list-level hook with a default
  implementation equal to the previous behaviour (`processes.Where(CanHandle)`).
- `LongshotGameIntegration.SelectCandidates` — `CanHandle` path filter, then the
  classifier. It reports a classification summary (PIDs and roles only) through
  `StatusChanged` when it changes.
- `MonitoringService` — discovery (`DiscoveryStep`) and the pre-connect
  revalidation (`RevalidateSelect`) share one method,
  `SelectFromCurrentProcesses`, so both apply identical classification.
- `LongshotProcessSelector.Select` (PoC CLI) — optional `classifier` argument; the
  CLI passes the shared classifier, so `detect`/`status`/`watch`/`quit` agree with
  the service. Without a classifier its behaviour is unchanged.

### Reading command lines (privacy)

`ICommandLineReader` / `WmiCommandLineReader` use the documented WMI class
`Win32_Process` (`CommandLine`, `CreationDate`) through `System.Management`
(NuGet `System.Management` 10.0.14, Longshot project only; Core is unaffected).

- A WMI row is accepted only if its `CreationDate` matches the expected process
  start time (±1 s); otherwise, or when the start time is unknown, the result is
  "unreadable". This guards process-exit and PID-reuse races.
- Every failure returns null (never throws); a throwing reader is also caught by
  the classifier. Either way the process is retained.
- The command line is parsed for `argv[1..3]` and argument count, then discarded.
  It is never stored, logged, or placed in diagnostics or exceptions.
  `GameProcessInfo.CommandLine` stays `null` (the provider still never collects it).
- Diagnostics contain only PIDs, roles, and relationships.

### Tests (synthetic; no game required)

- `LongshotProcessRoleClassifierTests` — valid `--crash-watch` / `--crash-show`
  (with and without options, quoted and unquoted), no arguments,
  `--prepare-shaders`, unrelated and near-miss flags, null/empty, reader
  exception, incomplete and malformed helper arguments, `CommandLineToArgvW`
  quoting, relationship states (confirmed, orphaned, PID reuse), and PoC selector
  behaviour (helper excluded, stub still excluded, unchanged without a classifier,
  experimental PID still not authoritative).
- `MonitoringServiceHelperClassificationTests` — game + crash-watch helper
  connects to the game; helper only never connects; game + unreadable (or
  throwing) candidate stays ambiguous; two games + two helpers stay ambiguous;
  game + crash-show helper connects; restart sequence yields a new session for
  the new game; revalidation applies the same classification; a helper left after
  the game exits is never selected.
- `CommandLinePrivacyTests` — a sentinel in every command line never reaches
  status messages, the monitoring log, classification results, PoC diagnostics,
  or `GameProcessInfo.CommandLine`.
- The existing `Multiple_candidates_are_ambiguous_and_never_connect` still passes.

### Live verification (2026-11-23, this machine)

Launched the way RecompLauncher's profile starts the game
(`LongshotBuilder.exe --play {install}/Longshot`) with `HOS_CONTROL` set on that
process only. Observed with the real `MonitoringService`, `LongshotGameIntegration`,
`WindowsProcessProvider`, and WMI reader; command lines were inspected through
`CommandLineToArgvW` and recorded only as flag/PID/run-id tokens.

| Check | Result |
|---|---|
| Normal launch: game + crash-watch helper | **Pass** — game `argc=1` (parent `LongshotBuilder.exe`; no launcher stub observed); helper started 57 ms later as its child with `--crash-watch <game pid> <run id>` (argc 7); run id matched the game log's `run_id:` line; `bug-reports\running\<game pid>.running` named the game PID |
| Helper recognised and excluded; game selected | **Pass** — "crash-watch helper (references game pid, game confirmed); excluded"; selection `Unique`; service Searching → Connecting → Monitoring, connected to `\\.\pipe\longshot_control`. PoC `detect --diagnose` selected the game (previously `AMBIGUOUS`, exit 2); its experimental server PID agreed (diagnostic only) |
| Session created for the game | **Pass** — snapshots in `KokiriForest_Child`; session source PID = the game PID |
| Restart Now cycle | **Pass** — triggered with the bridge `relaunch` command, which Longshot documents as "the Insert menu's Restart now" (same `game_relaunch` path). New game started as a child of the old game (`argc=1`) |
| Old/new overlap | **Pass** — old game (exiting, briefly unreadable → retained), old helper, and new game coexisted → `Ambiguous`, no connect; then the old helper lingered ~1 s while being torn down (unreadable → retained → still `Ambiguous`); then new game + new helper → `Unique` new game |
| New session and bridge reconnect after restart | **Pass** — reconnected to the same pipe name ~4 s after the restart request; a new session id was created with the new game's PID |
| Normal quit and orphaned helper | **Pass (partially observed)** — window closed with `WM_CLOSE`; on a clean quit the helper exited within ~0.4 s with the game, so no long-lived orphan was observable; service went to `Searching` and the session ended. A positively classified orphan was not observed live (it only occurs while a crash window is open, which was out of scope); it is covered by synthetic tests |
| `HOS_CRASH_WATCH=0` (process environment only) | **Pass** — a single process, no `crash window: watching` log line; classified `Game`, selected, connected |

No crash was forced, nothing was terminated, and nothing in the Longshot install
was written by this project or the harness. A before/after listing of the whole
install (188,819 files) showed configuration hashes unchanged (`VERSION`,
`recomplauncher.json`, `tools.lock`, `longshot_options.ini`, `longshot_version.txt`,
`language.txt`); the only differences were Longshot's own normal runtime writes:
log rotation in `logs\`, two new shader-pipeline cache files and one refreshed
cache timestamp in `cache\`, and `account.sav`/`option.sav` rewritten at the same
size during startup. No `progress.sav` (game progress) changed, and
`bug-reports\pending` and `bug-reports\running` were empty afterwards (no crash
records; clean exits).

### Remaining limitations

- Two genuine game instances (for example, two installs) remain `Ambiguous` by design.
- During teardown races a process's command line can be momentarily unreadable;
  it is then retained, which can make selection briefly `Ambiguous` (observed
  live during restart; resolved within the debounce window).
- If an exiting game is still readable when its helper has already gone, a
  momentary `Unique` selection of the exiting game is possible; the existing
  debounce and pre-connect revalidation (fingerprint re-check) bound this, and
  connecting to a vanished bridge simply fails back to `Reconnecting`.
- The helper argument contract is Longshot v3.0.0's. A change upstream degrades
  to `Ambiguous`, not to a wrong selection.

### R1.1 hardening — bounded WMI reads and a classification cache

**Bounded reads.** `System.Management` cannot cancel a synchronous query, and its
`Timeout` bounds only result enumeration, not connecting to WMI
(`ConnectionOptions` has no connect timeout). `WmiCommandLineReader` therefore
runs each WMI round-trip on its own background thread and waits at most an
overall **3-second deadline** (`DefaultDeadline`). A missed deadline or any
failure is "unreadable": the process is classified `Unknown` and retained.

**Single flight.** While an earlier read is still blocked inside WMI, the reader
starts no new WMI operation; reads return "unreadable" immediately until the
blocked call returns. At most one WMI operation per reader is ever outstanding,
so a hung WMI service cannot accumulate threads or operations. The worker is a
background thread that nothing waits on, so it never blocks `StopAsync`,
disposal, or process exit; the monitoring loop waits at most about one deadline
per poll. The worker hands back only the raw row, and identity matching (PID +
creation time) happens on the caller, so a row that arrives after the caller
gave up is discarded unused.

**Classification cache.** `LongshotProcessRoleClassifier` caches positively
identified roles only (`Game`, `CrashWatchHelper`, `CrashShowHelper`, plus the
referenced PID — never command-line text), keyed by **PID and process start
time** (`ProcessFingerprint`). A process's command line cannot change, so a role
stays valid for that instance; a reused PID with a different start time is a
different key and is read again. Processes without a known start time are never
cached. Entries for instances absent from the latest process list are pruned on
every call (and the cache is cleared when no Longshot process remains); the
cache is capped at **64 entries**. `Unknown` results are never cached and are
retried on the next poll. Helper relationships are recomputed each call.

**Defensive parsing.** An unexpected exception while parsing becomes `Unknown`
(retained); the exception is not logged or kept, since its message could contain
command-line text. A crash-watch result without a referenced PID is also `Unknown`.

**Limitation.** A WMI call blocked inside COM (for example in connect) cannot be
forcibly terminated; it is contained, not killed. Until it returns, newly
appearing process instances stay `Unknown` (already-cached instances keep their
roles), so a new game and its helper may remain `Ambiguous` temporarily. This
delays monitoring but never selects a helper or guesses between candidates.

Tests: `WmiCommandLineReaderTests` (identity match, tolerance, missing row,
null/malformed creation date, missing start time, start-time mismatch, query
exception, hung query bounded with no further queries until it ends and its late
result discarded, plus two real-WMI checks against the test process) and
`LongshotClassifierCacheTests` (reuse, PID reuse re-read, pruning, cap, `Unknown`
retried, no caching without a start time, relationships recomputed, two genuine
games still `Ambiguous`, parser exceptions → `Unknown`). The privacy test of the
process provider now checks the test process itself rather than possibly no process.

Live re-verification (2026-11-23, same launch method and checks as R1): game
selected and crash-watch helper excluded ("game confirmed"); service reached
`Monitoring` and a session was created for the game PID; Restart Now (bridge
`relaunch`) produced an old/new overlap that the classifier reported as
`Ambiguous` — the exiting old game's command line was already unreadable, but its
cached `Game` role kept it a genuine candidate — then the new game was selected,
the bridge reconnected (~4.4 s after the request), and a new session was created
for the new PID; a normal close (`WM_CLOSE`) returned the service to `Searching`
and ended the session. The service's 1.5 s polls did not fall inside the ~1 s
overlap window, so its own state went `Reconnecting` → `Connecting` without
entering `Ambiguous`; it never attempted the old process. WMI: 1,201 reads,
maximum 299 ms, average 126 ms — no read approached the 3 s deadline.
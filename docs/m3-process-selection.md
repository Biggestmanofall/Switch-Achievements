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
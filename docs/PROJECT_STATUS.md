# Switch Achievements — Project Status and Handoff

**Status snapshot:** 2026-11-24  
**Current branch:** `development`  
**Recorded HEAD:** `b02214f8de2a9250678b5a381cf5ace8c54f24f1`
(`Initial commit - M5.5 complete`)  
**Current milestone state:** M5.8.2 implementation and live acceptance
complete; M5.6–M5.8.2 work is uncommitted (see §7). M5.9 is the next
proposed milestone.

This document is the authoritative handoff for future development
sessions and coding agents. `README.md` is the short product overview;
`AGENTS.md` defines how agents must work. Recheck the live repository
before acting on anything here: this snapshot becomes stale as
development continues.

## 1. Product overview

Switch Achievements is a Windows WPF application intended to provide
achievement tracking for Nintendo Switch game recompilations running on
PC.

The initial supported integration is Longshot 3.0.0, with *The Legend of
Zelda: Ocarina of Time* Switch 2 recompilation as the first supported
game.

The project has completed the foundation for discovering the game
process, identifying the supported game, connecting to Longshot’s local
control bridge, receiving normalized gameplay snapshots, tracking
sessions, and presenting live information in the desktop application.

**The achievement system itself is not implemented yet.** There are no
production achievement definitions, no evaluation engine, no unlock
persistence, and no achievement UI or Ocarina of Time pack.
`IAchievementEngine` in Core is an empty architectural seam.

## 2. Completed milestones

### Phase 0 — Longshot proof of concept

Established that Longshot’s Windows control bridge could be used to
receive live Ocarina of Time gameplay state. Verified process detection,
bridge status, OoT subscription, a real rupee change, and
disconnect/reconnect/re-subscribe behavior. Records: `docs/phase0-test-1.md`,
`docs/phase0-test-2.md`, `docs/00-research-findings.md`,
`docs/control-protocol.md`.

### M1–M4 — Core integration foundation

Completed the initial architecture and implementation for process
discovery/selection, integration lifecycle, normalized snapshots,
comparison and session handling, monitoring, and related test coverage.
Design records: `docs/m2-snapshot-null-policy.md` (snapshot `null`
semantics) and `docs/m3-process-selection.md` (conservative selection).

### M5.1–M5.5 — Monitoring and desktop foundation

Completed the monitoring lifecycle, app infrastructure, dashboard
foundation, settings, tray behavior, and reliability hardening. M5.5
also hardened asynchronous test synchronization: the
`StateTransitionObserver` test helper replaces race-prone event
assertions and arbitrary sleeps. M5.5 is the last committed state.

### R1 / R1.1 — Longshot process classification

Longshot runs a second `bin\longshot.exe` as a crash-reporter helper
(`--crash-watch`/`--crash-show`). `LongshotProcessRoleClassifier`
recognizes positively identified helpers from their command line and
excludes only those; unknown or unreadable processes are retained, so
ambiguity is reported, never guessed. `WmiCommandLineReader` runs WMI
queries on a dedicated background thread with a bounded caller wait;
command-line content is parsed and discarded, never stored or logged.
Evidence: the R1 addendum in `docs/m3-process-selection.md`.

### M5.6 — Game identity and catalog

Implemented generic Core identity contracts, deterministic identity
resolution, integration-specific identity formats, a developer-managed
catalog, and the Longshot identity provider.

- The Longshot provider reads only bounded `main.npdm` and `main` NSO
  header data from the game’s `.longshot_exefs` folder, validates
  layouts and identity values, and emits evidence. It does not own
  monitoring lifecycle or session state.
- Resolution outcomes are Identified, Unsupported, Unknown, and
  Conflict. Only an `IdentifiedGame` carries a game; a `GameId` on a
  snapshot is a label, not authorization.
- Identification is integrated into monitoring with
  per-process-fingerprint caching (PID plus start time): a new
  fingerprint gets a new attempt.

### M5.7 — Desktop application, dashboard, and tray

Implemented the application-owned `MonitoringHost` and coordinated
lifecycle (`ShutdownCoordinator`), the dashboard view model and
presentation, the red Frutiger Metro-inspired UI, tray status and
native notifications (`TrayNotificationPolicy`, `NotificationMonitor`),
monitoring restart with failed-restart recovery
(`MonitoringRestartCoordinator`), and optional Windows sign-in startup
(`StartupRegistration`, current-user Run key).

Live acceptance verified normal RecompLauncher launch, game identity,
bridge connection, gameplay snapshots, the tray status while searching,
the game-detected notification, and clean shutdown.

### M5.8.1 — Bridge availability diagnostics

Implemented non-connecting named-pipe availability detection
(`NamedPipeDirectory`, `EndpointAvailability`). When the game is
identified but Longshot’s bridge pipe is absent, monitoring stays in
Connecting without connection attempts and, after a 10-second grace
period from process start, reports `MonitoringDiagnostic.LiveTrackingDisabled`.
The diagnostic belongs to one process fingerprint, is never shown for a
process whose bridge already worked, and is cleared when the process,
state, or service changes.

### M5.8.2 — Consent-based live-tracking setup

Implemented the dashboard action **Enable live tracking**, which can
enable Longshot’s control bridge for the next game launch after explicit
user consent. See §5 for the configuration model and safeguards, and §6
for verification.

## 3. Current architecture

The solution `SwitchAchievements.slnx` contains:

| Project | Target | Role |
|---|---|---|
| `src/SwitchAchievements.Core` | `net10.0` | Platform-neutral contracts and domain logic |
| `src/SwitchAchievements.Longshot` | `net10.0-windows` | Windows/Longshot integration (`System.Management` for WMI) |
| `src/SwitchAchievements.App` | `net10.0-windows`, WPF | Composition, lifecycle, UI, settings, tray |
| `src/SwitchAchievements.Longshot.Poc` | `net10.0-windows` | Retained Phase 0 console tooling |
| `tests/SwitchAchievements.Tests` | `net10.0-windows`, xUnit | All automated tests |

### Core identity contracts and catalog (`SwitchAchievements.Core`)

- Integration and process contracts: `IGameIntegration` (including the
  default `CheckEndpoint`), `IProcessProvider`, `INamedPipeProbe`,
  `EndpointAvailability`.
- Selection and identity of processes: `ProcessSelector`,
  `ProcessSelectionResult`, `ProcessFingerprint` (PID plus start time).
- Snapshots and sessions: `NormalizedSnapshot`, `SnapshotComparer`,
  `SessionManager`, `GameSession`, `GameStateChange`, `GameId`.
- Identity (`Identity/`): `IGameIdentityProvider`,
  `IIntegrationIdentityFormat`, `GameIdentityEvidence`, `EvidenceIssue`,
  the validators in `IdentityValidators.cs` (`IntegrationIdentifier`,
  `IdentityFactKey`, `IdentityFactValue`), `GameIdentityResolver`,
  `GameIdentification`, `IdentifiedGame`.
- Catalog (`Catalog/`): `GameCatalog`, `CatalogModels`.
- Seam: `IAchievementEngine` (empty).

Core does not depend on WPF, Windows APIs, Longshot, or file parsing.

### Longshot integration (`SwitchAchievements.Longshot`)

- Integration and transport: `LongshotGameIntegration`,
  `ControlBridgeClient`, `Protocol`, `NamedPipeBridgeProbe`.
- Discovery and classification: `WindowsProcessProvider`,
  `ProcessDetection`, `LongshotProcessSelector`,
  `LongshotProcessRoleClassifier`, `WmiCommandLineReader`
  (`ICommandLineReader`).
- Identity: `LongshotGameIdentityProvider`, `LongshotExefsParser`,
  `LongshotIdentityFormat`.
- Bridge availability: `NamedPipeDirectory` (`INamedPipeDirectory`).
- Live-tracking setup: `LongshotGameFolder`, `LongshotEnvFile`,
  `LongshotLiveTrackingSetup`.

### Monitoring and session management (`SwitchAchievements.App/Services`)

- `MonitoringService` owns discovery, selection, identification
  (`GameIdentifier`), connection, snapshot fan-in, sessions, reconnection,
  and the bridge diagnostic. `MonitoringStateMachine` validates the
  states Stopped, Searching, Connecting, Monitoring, Reconnecting,
  Ambiguous, and Error.
- `MonitoringHost` owns at most one service at a time and replaces it on
  restart; `MonitoringComposition` composes the production service and
  loads the catalog once.

### Application composition and lifecycle

- `App.xaml.cs` is the composition root: single-instance guard
  (`Infrastructure/SingleInstance`), logging, settings, startup
  registration, monitoring host, dashboard, tray, notifications, and the
  live-tracking setup coordinator.
- `ShutdownCoordinator` gives every exit path one bounded, idempotent
  monitoring shutdown.
- The catalog under `catalog/` is embedded at build time and loaded by
  `App/Catalog/CatalogLoader` through `EmbeddedCatalogResourceSource`.

### Dashboard and tray

- `ViewModels/DashboardViewModel` and `DashboardFormat` present
  monitoring state, identification, gameplay values, freshness, the
  bridge diagnostic, and the live-tracking action.
- `MainWindow.xaml` with `Styles/FrutigerMetro.xaml`,
  `Styles/MetroDecor.xaml`, and `Controls/MetroBackdrop`.
- Tray: `TrayIconController`, `TrayStatus`, `TrayNotificationPolicy`,
  `NotificationMonitor`, `MonitoringRestartCoordinator`.
- Live-tracking action: `LiveTrackingSetupCoordinator`,
  `LiveTrackingEligibility`, `LiveTrackingConsent`.

### Logging and settings

- `Infrastructure/Logging/FileLogger` writes to
  `%LOCALAPPDATA%\SwitchAchievements\logs`.
- `Infrastructure/Settings/SettingsService` persists `AppSettings`
  (`ShowWindowOnLaunch`, `MinimizeToTrayOnClose`, `StartWithWindows`)
  as `%APPDATA%\SwitchAchievements\config.json`.
- `Infrastructure/Startup/StartupRegistration` manages only the
  current-user Run value `SwitchAchievements`.

### Test organization (`tests/SwitchAchievements.Tests`)

Grouped by area: `AppLifecycle/`, `BridgeAvailability/`, `Catalog/`,
`Dashboard/`, `Identity/`, `LiveTrackingSetup/`, `LongshotIdentity/`,
`MonitoringIdentity/`, `Startup/`, `Tray/`, plus top-level suites for
Core, monitoring, selection, classification, settings, and logging.
Shared doubles live in `Fakes/` (including `StateTransitionObserver`
and `TestClock`). Tests use fakes, injected clocks, temporary
directories, and bounded waits; none touch the real Longshot
installation.

## 4. Supported scope and deferred work

Supported:

- Longshot 3.0.0.
- *The Legend of Zelda: Ocarina of Time* Switch 2 recompilation.

| Field | Value |
|---|---|
| Catalog game ID | `zelda-ocarina-of-time` |
| Display name | `The Legend of Zelda: Ocarina of Time` |
| Platform | `switch2` |
| Integration ID | `longshot` |
| Program ID | `0400486024dec000` |
| Main NSO build ID | `e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348` |

Deferred (no work without a new explicit product decision):

- Switch 1. The user does not currently have Eden installed or a
  Switch 1 game available for testing.
- Eden and other emulators.
- Generic ROM hashing as a substitute for integration-specific identity.
- User-importable achievement packs.

Key design decisions:

1. Future Switch 2 recompilations may need different
   integration-specific identity mechanisms; add them as providers per
   integration, keeping Core integration-independent.
2. Supported games and achievement definitions are developer-managed,
   version-controlled JSON catalog data.
3. SQLite is planned for mutable player-specific state only (unlock
   records/timestamps, progress, settings, history).
4. Process selection is conservative; ambiguity is reported, never
   guessed.
5. Snapshot `null` means unavailable/not sampled, not zero or false.
6. Longshot’s control bridge is opt-in; guest-memory access stays off.
7. Planning and implementation are separate authorization steps.

## 5. Longshot live-tracking configuration

- Longshot starts its local control bridge only when `HOS_CONTROL` is
  set at game startup. It reads the setting from the process environment
  or, if absent there, from the documented `longshot_env.txt` beside the
  game. The environment takes priority over the file.
- On the user’s machine, `HOS_CONTROL=longshot_control` is enabled
  through the real `longshot_env.txt` in the Longshot game folder. The
  file was created by the M5.8.2 consent workflow and is **intentionally
  retained**. Do not remove or modify it without explicit authorization.
  It is not part of this repository.
- When the bridge is disabled, Switch Achievements detects it (M5.8.1)
  and offers **Enable live tracking** for the positively identified
  supported game only. Nothing is written without an explicit **Yes** in
  the consent dialog (No is the default).
- The setting takes effect only when the game is restarted; a normal
  relaunch through RecompLauncher works, and Longshot’s in-game Restart
  also re-reads the file.
- `HOS_CONTROL_MEM` (guest-memory access) remains disabled and is never
  set.
- Security: the pipe is restricted to the current Windows user, but
  other processes running as that user can send game-control commands
  (input, quit, settings). This is why the feature is opt-in and the
  dialog explains it.

Safeguards in the setup workflow:

- **Process revalidation:** the identified process (PID plus start
  time) is rechecked when the action is requested, after the dialog
  closes, and immediately before writing, using the monitoring
  selection pipeline.
- **Game folder:** derived only from the validated process executable
  path (`...\bin\longshot.exe`, folder containing `image.bin`; no
  relative, UNC, device, or network paths).
- **File identity:** the file is opened without following reparse
  points and with no sharing; the opened handle itself must be a
  regular file with a single link (symlinks, junctions, directories, and
  hard links are refused).
- **Append-only editing:** existing bytes, encoding, BOM, comments, and
  line endings are preserved; Longshot’s own parser semantics decide
  whether the setting is present; a conflicting value is never
  overwritten; a missing file is created with create-new semantics.
- **Rollback:** a failed append is truncated back and verified byte for
  byte; an unverifiable recovery is reported distinctly as
  `RecoveryFailed`.
- **Environment conflicts:** User and Machine `HOS_CONTROL` values are
  read (never written); a conflicting or disagreeing value refuses the
  write. A per-launch override cannot be detected and is mentioned in
  the result message.

## 6. Verification status

### Historical: M5.8.2 completion (2026-11-24)

- Automated test suite: 1,132 tests (148 added in M5.8.2).
- Ten consecutive successful full-suite runs, after a test
  synchronization fix to one new test.
- Forced rebuild: zero warnings and zero errors.
- Live acceptance: **successful**. It verified normal RecompLauncher
  launch without environment injection, the disabled-bridge diagnostic
  and action, the user-confirmed write, Longshot bridge activation after
  relaunch (`control: listening on longshot_control`, guest memory off),
  connection and real gameplay snapshots (134 snapshots, rupee change,
  cutscene flag), normal game exit and monitoring shutdown, and
  installation integrity (game assets, identity files, and protected
  configuration unchanged; only `longshot_env.txt` added by the app).

Limitations of that live acceptance:

- Longshot’s in-game Restart was not live-tested; a normal quit and
  relaunch through RecompLauncher was verified.
- Declining the consent dialog was not live-tested; automated tests
  cover the decline/no-write path.
- Some notification scenarios, and the tray text while connecting or
  monitoring, were covered by unit tests rather than live acceptance.

### Pre-checkpoint audit (2026-11-24), separate from the above

- `dotnet build SwitchAchievements.slnx` (SDK 10.0.401): exit 0,
  0 warnings, 0 errors; forced rebuild (`--no-incremental`): exit 0,
  0 warnings, 0 errors.
- `dotnet test SwitchAchievements.slnx --no-build`: exit 0, 1,132
  passed, 0 failed, 0 skipped (single run).
- `git diff --check`: clean.

## 7. Git checkpoint state

Observed during the pre-checkpoint audit (2026-11-24):

- Branch `development`, tracking `origin/development`; 0 ahead and
  0 behind as of the last fetch (no fetch performed during the audit).
- HEAD `b02214f8de2a9250678b5a381cf5ace8c54f24f1`
  (`Initial commit - M5.5 complete`), the only commit.
- 121 working-tree entries: 24 modified tracked files and 97 untracked
  files, comprising the M5.6–M5.8.2 implementation and tests, the
  `catalog/` JSON, and the documentation drafts (`README.md`,
  `AGENTS.md`, this file).
- Nothing staged; no stashes.
- `Switch-Achievements-Documentation-Update.zip` (repository root,
  untracked) is a documentation delivery archive, not project source;
  it is recommended to leave it out of the checkpoint.

**No checkpoint commit or push has occurred.** The user handles staging,
committing, and pushing. The uncommitted changes are intentional and
user-owned; they must not be reset, cleaned, stashed, staged, committed,
pushed, or overwritten without explicit authorization.

## 8. Next milestone: M5.9 — Achievement Definition System

**Proposed, planning first. Not approved for implementation.**

Expected scope:

- A developer-managed `achievements.json` per supported game, embedded
  with the catalog.
- Achievement IDs, names, descriptions, icons, and condition
  definitions.
- Validation of achievement definitions.
- Association of achievements with catalog game IDs.
- A generic, integration-independent definition model (no Longshot
  transport details).

Explicitly out of scope for M5.9:

- No achievement evaluation engine.
- No unlock persistence.
- No achievement UI.

Starting point: inspect the `IAchievementEngine` seam, the catalog
models and loader (`Core/Catalog`, `App/Catalog`), embedded-resource
behavior, and test conventions; then propose a minimal, validated
definition architecture and test plan for approval.

## 9. Future roadmap

Planned milestones, not completed work; each is planned and authorized
individually:

- **M5.9 — Achievement Definition System** (above).
- **M5.10 — Achievement Evaluation Engine:** evaluate normalized
  snapshots against definitions, including stateful and multi-snapshot
  conditions, one-time unlocks, and deduplication.
- **M5.11 — Achievement Persistence (SQLite):** player-specific unlock
  timestamps, progress, and history; the catalog remains the source of
  truth for definitions.
- **M5.12 — Achievement UI and Notifications:** browsing, details,
  locked/unlocked state, progress, unlock notifications, and history,
  following the existing view-model and lifecycle patterns.
- **M5.13 — Ocarina of Time Achievement Pack:** author and validate the
  initial achievement set against actual gameplay.

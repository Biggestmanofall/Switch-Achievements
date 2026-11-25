# Switch Achievements

A Windows desktop application for tracking achievements in Nintendo
Switch game recompilations running on PC. The initial integration
targets **Longshot 3.0.0** and **The Legend of Zelda: Ocarina of Time**
(Switch 2 recompilation).

> **Project status:** The process-integration, game-identification,
> live-snapshot, dashboard, tray, and opt-in Longshot bridge setup
> foundations are implemented and have passed automated and live
> verification. The achievement-definition and achievement-unlocking
> pipeline has **not** been implemented yet.

## Current status

### Implemented

- Windows WPF desktop application with automatic monitoring startup,
  single-instance behavior, coordinated shutdown, and system-tray
  integration.
- Longshot process discovery and conservative process selection. Known
  Longshot crash helpers are classified and excluded; ambiguous
  game-process selection is not resolved through guesswork.
- Game identity detection from the game’s own executable files in
  Longshot’s `.longshot_exefs` folder: the program ID from `main.npdm`
  and the build ID from the `main` NSO header, with bounded parsing and
  validation.
- Developer-managed game catalog, embedded into the application. The
  current catalog contains one supported game: *The Legend of Zelda:
  Ocarina of Time*.
- Longshot named-pipe availability diagnostics and live gameplay
  snapshot reception.
- Session tracking and normalized snapshots, including explicit
  nullable-field semantics.
- Dashboard presentation of monitoring state and available gameplay
  data, including map, age, hearts, rupees, and snapshot freshness.
- System-tray status, notifications, monitoring restart, close-to-tray
  behavior, and optional Windows sign-in startup.
- Consent-based setup for Longshot live tracking. When the user
  confirms, the application can add `HOS_CONTROL=longshot_control` to
  the game’s `longshot_env.txt`. The game must be restarted before the
  bridge becomes available.
- Automated tests (xUnit) covering the systems above: 1,132 tests passing
  at the M5.8.2 checkpoint.

### Not implemented yet

- Achievement definition files such as `achievements.json`.
- Achievement-condition evaluation and unlock detection.
- Multi-stage or persistent achievement progress.
- Local SQLite persistence for unlocks, timestamps, player settings, and
  history.
- Achievement browsing, details, progress presentation, unlock
  notifications, and statistics.
- A production Ocarina of Time achievement pack.

The `IAchievementEngine` interface is currently only an architectural
seam; it is not a functioning achievement engine.

## Supported scope

| Area                | Current support                                               |
|---------------------|---------------------------------------------------------------|
| Desktop platform    | Windows; WPF application targeting `net10.0-windows`          |
| Game integration    | Longshot                                                      |
| Supported game      | *The Legend of Zelda: Ocarina of Time* Switch 2 recompilation |
| Catalog ID          | `zelda-ocarina-of-time`                                       |
| Integration ID      | `longshot`                                                    |
| Switch 2 program ID | `0400486024dec000`                                            |
| Main NSO build ID   | `e0f3ccfaf50065ae4ea01eb5aeb13b1a45da3348`                    |
| Longshot bridge     | Local Windows named pipe                                      |
| Guest-memory bridge | Disabled; `HOS_CONTROL_MEM` is not enabled                    |

Switch 1, Eden, emulator integrations, generic ROM hashing, and
user-importable achievement packs are explicitly outside the current
scope. Future integrations should be added through integration-specific
providers without introducing their dependencies into Core.

## Requirements

- Windows environment capable of running the WPF application.
- .NET 10 SDK.
- Longshot 3.0.0 installed and working.
- The supported Ocarina of Time Switch 2 recompilation, launched through
  RecompLauncher.

## Build, test, and run

Run these commands from the repository root in PowerShell:

``` powershell
dotnet restore SwitchAchievements.slnx
dotnet build SwitchAchievements.slnx
dotnet test SwitchAchievements.slnx
dotnet run --project src/SwitchAchievements.App/SwitchAchievements.App.csproj
```

The application project targets `net10.0-windows` and uses WPF. Build
and runtime verification should therefore be performed on Windows. The
test project also targets `net10.0-windows`; `SwitchAchievements.Core`
targets plain `net10.0`. These commands were last verified with the
.NET SDK 10.0.401.

Stop any running Switch Achievements instance before building: a running
app locks its output folder and the build cannot copy over it.

The older `SwitchAchievements.Longshot.Poc` console project remains in
the solution as historical/diagnostic tooling; the WPF application is
the primary product.

## First launch and Longshot setup

1.  Launch the supported game normally through RecompLauncher.
2.  Start Switch Achievements. Monitoring starts automatically.
3.  If the dashboard reports that Longshot’s live data bridge is
    disabled, use **Enable live tracking** and review the consent
    dialog.
4.  Confirm only if you want to enable Longshot’s local control bridge.
    The application adds the setting to the game’s `longshot_env.txt`;
    it does not enable guest-memory access.
5.  Restart the game. A normal relaunch through RecompLauncher works;
    Longshot’s in-game Restart option also reloads the setting.
6.  Once the named pipe is available, Switch Achievements can connect
    and receive gameplay snapshots.

The bridge is intentionally opt-in. Longshot’s local control pipe can
accept game-control commands from other processes running as the same
Windows user. The confirmation dialog explains this security trade-off.
`HOS_CONTROL_MEM` is separate and remains disabled.

The application does not silently modify Longshot configuration at
startup. The only application flow that writes `longshot_env.txt` is the
explicit, user-confirmed setup action.

## Architecture

``` text
SwitchAchievements.App
  WPF UI, dashboard, settings, tray, lifecycle, composition
        |
        +---- SwitchAchievements.Core
        |       Platform-neutral domain contracts, process selection,
        |       identity resolution, normalized snapshots, sessions, catalog
        |
        +---- SwitchAchievements.Longshot
                Windows process discovery, Longshot classification,
                identity evidence, named-pipe integration, live setup
```

- **Core** contains reusable, integration-independent contracts and
  domain logic. It should not depend on WPF, Windows APIs, Longshot, or
  file-format parsing.
- **Longshot** contains Windows- and Longshot-specific integration code
  and implements Core contracts.
- **App** composes the services and owns the WPF user interface,
  lifecycle, settings, tray, and embedded catalog loading.
- **`catalog/`** contains developer-managed JSON catalog data embedded
  at build time.
- **`tests/SwitchAchievements.Tests/`** contains unit and
  integration-style tests using fakes and deterministic test helpers.

See `AGENTS.md` for repository-wide instructions for coding agents, and
`docs/PROJECT_STATUS.md` for milestone history, architectural decisions,
verification evidence, and the planned roadmap.

## Roadmap

The next planned milestones are:

1.  **M5.9 — Achievement Definition System:** developer-managed
    achievement definitions, schema/validation, and catalog association.
2.  **M5.10 — Achievement Evaluation Engine:** evaluate normalized
    snapshots and detect achievement conditions, including stateful and
    multi-stage conditions.
3.  **M5.11 — Achievement Persistence:** local SQLite storage for
    player-specific unlocks, timestamps, progress, and history.
4.  **M5.12 — Achievement UI and Notifications:** achievement
    list/details, locked/unlocked state, progress, unlock notifications,
    and history.
5.  **M5.13 — Ocarina of Time Achievement Pack:** author and validate
    the initial game-specific achievements.

These are planned milestones, not implemented features. The order and
scope should be confirmed during planning before implementation.

## Documentation

- `AGENTS.md` — instructions for all coding agents working in this
  repository.
- `docs/PROJECT_STATUS.md` — authoritative project handoff and milestone
  status.
- `docs/00-research-findings.md` — Longshot research findings.
- `docs/control-protocol.md` — verified Longshot control-bridge protocol
  reference.
- `docs/m2-snapshot-null-policy.md` — normalized snapshot null
  semantics.
- `docs/m3-process-selection.md` — process-selection investigation and
  policy.

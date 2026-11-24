# Phase 0 — Research findings (verified against Longshot v3.0.0)

All claims below were verified against the actual v3.0.0 files in
`C:\Recomp Library\longshot\` and the `v3.0.0` tag of
`git.magicbean.dev/MagicBeanDev/Longshot` (build source `87cb36aa`, release
target `09cbdd97`, tag object `e76c008b`).

## 1. Installation and process architecture

```
C:\Recomp Library\longshot\        <- RecompLauncher install root
├─ VERSION                         "v3.0.0"
├─ recomplauncher.json             RecompLauncher profile (nup_build_id e0f3ccfa)
├─ tools.lock                      builder toolchain manifest
├─ LongshotBuilder.exe             builder entry (a .NET apphost stub)
├─ LongshotBuilder\                the .NET builder + kernel runtime files
├─ recompdata\                     (empty)
└─ Longshot\                       the built game folder
   ├─ longshot.exe                 launcher stub (~191 KB)
   ├─ bin\longshot.exe             THE GAME (~536 MB; runs the control bridge)
   ├─ bin\longshot_vk.dll          Vulkan backend (~14 MB)
   ├─ bin\SDL2.dll, bin\SDL3.dll
   ├─ longshot_version.txt         version=v3.0.0, source=87cb36aa, builder=<path>
   ├─ longshot_options.ini         settings (no HOS_CONTROL key anywhere)
   ├─ logs\longshot_log.txt        runtime log (control log lines go here)
   └─ romfs\, .longshot_exefs\, cache\, user\   (game data + saves: not touched)
```

Process flow (Windows):

```
RecompLauncher
  -> LongshotBuilder.exe --play {install}/Longshot
     -> Longshot\longshot.exe        (stub; builder/setup/setup.c)
        -> Longshot\bin\longshot.exe (the recompiled game - bridge host)
```

- The real game process is `bin\longshot.exe`; `Longshot\longshot.exe` is a
  launcher stub. Both are named `longshot.exe`, so detection disambiguates by
  full path (the game path contains `\bin\`).
- Build identity: `longshot_version.txt` (version + `source` commit) and
  `recomplauncher.json` -> `game.check.nup_build_id` (`e0f3ccfa`). The bridge
  `status` reply also returns a `version` field.
- Runtime log: `Longshot\logs\longshot_log.txt`.

## 2. Control bridge architecture

- Game side: `host/control.c`; OoT sampling: `host/oot_state_tap.c`.
- Client: `scripts/longshot_ctl.py` (stdlib only). Memory tour:
  `scripts/memtour.py` (Linux only; reads /proc).
- Docs: `docs/control.md`. Tests: `tests/test_control.py`,
  `tests/test_longshot_ctl.py`, `tests/test_oot_state_tap.py`,
  `tests/test_control_pipe_security.py`.
- Introduced/evolved by PRs #58 (sampled OoT state streaming) and #570
  (memtour.py) — both merged before v3.0.0.

## 3. Verified protocol details

See `docs/control-protocol.md` for the full reference. Highlights:

- Transport: local named pipe on Windows (`\\.\pipe\<name>`), Unix socket on
  Linux. Never TCP. Local-only, opt-in.
- Enablement: `HOS_CONTROL` env var (off unless set). Optional
  `HOS_CONTROL_MEM=ro|rw` for raw memory (not used by the PoC).
- Framing: newline-delimited JSON, `protocol:1`, requests <= 64 KB, replies
  matched by `id` and carrying `frame`; subscriptions emit `event` lines.
- Read path used by the PoC: `status` and `subscribe` (events
  `present`/`state`/`map`/`log`/`oot`) with named/hash flags and change
  detection; OoT snapshot fields `map`, `age`, `loading`, `event`, `life`,
  `max_life`, `rupees`, `pos`, `facing`, `camera`, `prompt`, `music`, `flags`,
  `changed`.

## 4. Windows compatibility findings

- The named-pipe transport, pipe DACL and AppContainer handling are implemented
  and covered by `test_control_pipe_security.py`; the Python client is
  Windows-aware. A C# client can speak to the pipe directly with no Python.
- **Documented gap:** CI has no Windows runner, so the bridge inside a Windows
  game build, `launch` on Windows, and `PIPE_REJECT_REMOTE_CLIENTS` are only
  exercised by a manual Windows tester. This is why Phase D/E must run live on
  this machine to be confirmed.
- `HOS_CONTROL` is enabled only via the environment, not via
  `longshot_options.ini`. RecompLauncher's profile has no supported env
  passthrough we can set without editing the install, so we set it via a
  launcher helper inside this project.

## 5. Proof-of-concept implementation

`.NET 10` console app, no NuGet packages, inside
`src/SwitchAchievements.Longshot.Poc/`:

- `Program.cs` — CLI (`detect`, `status`, `watch`, `quit`), option parsing,
  Ctrl+C handling, output formatting.
- `ProcessDetection.cs` — finds `longshot.exe` processes and disambiguates the
  game (`bin\longshot.exe`) from the launcher stub.
- `BuildIdentifier.cs` — reads `longshot_version.txt` + `recomplauncher.json`.
- `ControlBridgeClient.cs` — `NamedPipeClientStream` client, newline-delimited
  JSON framing, id-matched replies, subscription events, reconnect support.
- `Protocol.cs` — protocol constants, request builders, typed models.
- `DiagnosticsLogger.cs` — local log (no game files/PII).

The launcher helper `scripts/launch-recomp-with-control.ps1` sets
`HOS_CONTROL=longshot_control` and starts RecompLauncher.

## 6. Test results

| Scenario | Result |
|---|---|
| Build | PASS - 0 warnings / 0 errors (.NET 10.0) |
| `help` | PASS - prints usage |
| `detect` with the game closed | PASS - reports "none" / "game not running" |
| Game launched via RecompLauncher (helper sets `HOS_CONTROL`) | PASS |
| Detect the game process (PID, path, build) | PASS - PID 9872, `bin\longshot.exe`, v3.0.0 / 87cb36aa / e0f3ccfa |
| Connect to the bridge + `status` | PASS - `protocol=1`, `state=play`, `map=KokiriForest_Child` |
| Receive `oot` samples | PASS - streamed samples, `PlayerStatus.Life=12` |
| Observe a state change | PASS - `RUPEES CHANGED: 0 -> 1` mid-session (a rupee collected) |
| Normal quit -> disconnect handling | PASS - `[bridge] disconnected` detected |
| Relaunch -> reconnect + re-subscribe | PASS - `connected to \\.\pipe\longshot_control`, re-subscribe, samples resumed |

Raw console captures for the live runs are in `docs/phase0-test-1.md` and
`docs/phase0-test-2.md`; the on-machine diagnostics are in `logs/`. Every output
line in §7 is quoted verbatim from those captures — nothing is simulated.

Two details observed during the live runs, recorded without assuming a cause:
- `detect` listed **two** processes both running `bin\longshot.exe` (PIDs 9872
  and 16992, identical path); the PoC selected the first.
- The `1 -> 0` rupee line after reconnect is a cross-session comparison (see §8).

## 7. Console output (real, verbatim)

### status (one-shot)

```
game: PID 9872  C:\Recomp Library\longshot\Longshot\bin\longshot.exe
build: version=v3.0.0 source=87cb36aa nup_build_id=e0f3ccfa
[bridge] connected to \\.\pipe\longshot_control
  frame=3722  protocol=1  fps=47.8  state=play  stalled=False
  draws=1754.5  replayed=3717  uptime=54.8s  clients=1
  map=KokiriForest_Child  version=v3.0.0 (source 87cb36aa)
  renderer=NVIDIA GeForce RTX 4060 (Vulkan, native)
  game: map=KokiriForest_Child  age=child  event=false  loading=false
        life=12/12  rupees=0  pos=(-10.3, 110.0, 3.1)
        prompt=Empty  music=BGM_KokiriForest
        flags=(none)
```

### watch — OoT samples and a real rupee change

```
[watch] connected (pipe 'longshot_control'); subscribing oot every=60 flags=PlayerStatus.Life changes=True
[watch] subscribed. Press Ctrl+C to stop. ...
[oot #1] frame=4218 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10.3, 110, 3.1] facing=81.9 camera=81.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: {}
...
  >>> RUPEES CHANGED: 0 -> 1
[oot #49] frame=7100 map=KokiriForest_Child ... life=12/12 rupees=1 ...
```

### disconnect -> relaunch -> reconnect -> re-subscribe

```
[bridge] disconnected
[watch] bridge disconnected - waiting for the game to relaunch so it can reconnect.
[watch] game not running - ...          (repeated while the game was closed)
[bridge] connected to \\.\pipe\longshot_control
[watch] connected (pipe 'longshot_control'); subscribing oot every=60 flags=PlayerStatus.Life changes=True
[watch] subscribed. ...
  >>> RUPEES CHANGED: 1 -> 0
[oot #1] frame=3189 ... rupees=0 ... | changed: {}
```

## 8. Known limitations

- Bridge-on-Windows is implemented but not CI-verified by upstream (only a manual
  Windows tester); this Phase 0 run is that manual exercise.
- `state` is a draw-count heuristic; `map` is the game's own name, refreshed
  ~250 ms; samples stop when Link is not updated (title/pause/loading).
- A subscriber keeps <=32 pending flag changes; overflow sets `more`.
- `HOS_CONTROL_MEM` (raw memory) was intentionally left off, so `mem_*`
  commands are out of scope for this PoC.
- **Two-process detection:** `detect` observed two concurrent processes both
  running `C:\Recomp Library\longshot\Longshot\bin\longshot.exe` (PIDs 9872 and
  16992, identical path); the PoC selected the first. The reason for the second
  process was not determined, and no assumption is made about its role.
- **Change-scan noise:** with `changes:true`, two unnamed hash values
  (`0xe53eb6d1`, `0xe573f564`) were reported as changed on nearly every sample
  (their values increment each pass). They were not identified. Watching by a
  named flag (e.g. `PlayerStatus.Life`) avoids this noise.
- **Cross-session value comparison:** the `RUPEES CHANGED: 1 -> 0` line right
  after reconnect reflects the PoC comparing its in-memory last value from the
  prior session (1, a rupee collected before quitting) with the new session's
  first sample (0). The game's own value was `rupees=0` at that instant; no claim
  is made about why it was 0 after the restart (e.g. whether the rupee had been
  saved).

## 9. Recommended architecture (permanent integration)

Keep the platform modular; Longshot is the first integration adapter:

- **Game detection** — process/env detection (started by this PoC).
- **Game identification** — `longshot_version.txt` + `recomplauncher.json`
  build id + the bridge `status.version` and `map` table.
- **Integration adapters** — an adapter interface speaking the control bridge;
  future adapters (other recomp methods / emulators) plug in alongside.
- **Shared achievement engine** — consumes a normalized, frame-stamped snapshot
  (map, age, event, loading, flags/changed) - not built yet.
- **Local persistence**, **overlay/notifications**, **online sync** — later.

## 10. Recommended next phase

1. With the user, launch via the helper and validate live `detect` -> `status`
   -> `watch` -> state-change -> reconnect (Phase E).
2. Pin a small set of named OoT flags (mirroring `PlayerStatus.Life`) and their
   hashes for progression tracking; record a real `oot` payload.
3. Define a normalized "game snapshot" schema and a multi-game detection
   abstraction for the achievement engine.
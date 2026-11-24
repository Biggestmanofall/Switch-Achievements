# Switch Achievements — Phase 0 (Longshot integration PoC)

Windows achievement tracking for the Switch 2 recompilation of *The Legend of
Zelda: Ocarina of Time*, built on Longshot 3.0.0. This repository currently
holds the Phase 0 proof of concept: prove we can reliably read live game state
from Longshot on Windows.

## Status

- Phase A/B/C research is complete and verified against the Longshot `v3.0.0` tag
  (see `docs/00-research-findings.md` and `docs/control-protocol.md`).
- The .NET console PoC builds and runs (no NuGet dependencies).
- **Live game-state reception is verified end-to-end on Windows:** process
  detection, bridge `status`, the `oot` subscription, a real rupee change, and
  disconnect/reconnect/re-subscribe. See `docs/00-research-findings.md` §6-7 and
  the raw captures `docs/phase0-test-1.md` / `docs/phase0-test-2.md`.

## What the PoC does

| Command | Purpose |
|---|---|
| `detect [--wait]` | Find the Longshot game process (PID, path, build id). |
| `status` | One-shot bridge `status` + Ocarina of Time snapshot. |
| `watch` | Subscribe to sampled `oot` state and print changes. |
| `quit` | Ask the bridge to close the game. |

## How the control bridge works (verified)

Longshot ships a control bridge (`host/control.c`) that is **off unless the
`HOS_CONTROL` environment variable is set**. On Windows it listens on a local
named pipe (`\\.\pipe\<name>`); the wire format is newline-delimited JSON
(`protocol:1`). It is read-only by default: `status` and `subscribe` (with an
`oot` event) are exposed without enabling guest-memory access.

## Requirements

- Windows 11, Longshot 3.0.0 installed and playable.
- .NET SDK (built and tested with 10.0).

## Usage

1. Start the game with the bridge on — this sets `HOS_CONTROL` then opens
   RecompLauncher (click Play there):

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\launch-recomp-with-control.ps1
   ```

   If RecompLauncher is already running, close it first so the new instance
   (which inherits `HOS_CONTROL`) is the one that starts the game.

2. In a second terminal, from the project root:

   ```powershell
   dotnet run --project src/SwitchAchievements.Longshot.Poc -- detect
   dotnet run --project src/SwitchAchievements.Longshot.Poc -- status
   dotnet run --project src/SwitchAchievements.Longshot.Poc -- watch
   ```

`watch` prints sampled OoT state (`map`, `age`, `loading`, `life`, `rupees`,
position, watched flags and changed flags). Do something in-game (enter a door,
pick up a rupee, take damage) to see a value change.

## Project layout

- `src/SwitchAchievements.Longshot.Poc/` — .NET PoC (protocol, client, CLI).
- `scripts/launch-recomp-with-control.ps1` — bridge-enabling launcher helper.
- `docs/` — research findings and a verified protocol reference.
- `logs/` — local diagnostics (gitignored).

## Scope / restrictions honored

No DLL injection, no memory modification, no save modification, no achievement
unlocking, and nothing is written to the Longshot install. The PoC is read-only
over the bridge (`HOS_CONTROL_MEM` is never set).
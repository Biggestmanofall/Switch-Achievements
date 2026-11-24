# Longshot control bridge — verified protocol reference

Source of truth: `docs/control.md`, `host/control.c`, `host/oot_state_tap.c`,
`scripts/longshot_ctl.py` at tag `v3.0.0`
(git.magicbean.dev/MagicBeanDev/Longshot). Protocol version `1`.

## Transport

- Linux: Unix domain socket at the `HOS_CONTROL` path.
- Windows: a local named pipe. `HOS_CONTROL=name` -> `\\.\pipe\name`;
  a bare name gets that prefix. Never TCP; local-only; the pipe DACL grants the
  current user (plus the game's AppContainer SID and an optional
  `HOS_CONTROL_PEER_SID`). Remote clients are refused.

## Enablement

- The bridge is off unless `HOS_CONTROL` is set when the game starts.
  When on, the log says: `control: listening on <path> (HOS_CONTROL: ...)`.
- `HOS_CONTROL_MEM=ro|rw` separately enables raw guest-memory read/write
  (`mem_read`, `mem_read_many`, `mem_write`). The PoC never sets it.

## Framing

- Newline-delimited JSON. One JSON object per line. Requests <= 64 KB.
- Each reply carries the request's `id` (any JSON value) and the current
  `frame`, plus `protocol:1`. Replies may be out of order; match by `id`.
- Subscription events carry an `event` key and no `ok` key.

## Errors

`{"id":<id>,"ok":false,"frame":<n>,"error":"<code>","message":"..."}`
Codes: `bad_json`, `too_long`, `bad_request`, `unknown_cmd`, `timeout`, `busy`
(>64 pending requests, >256 queued inputs, >8 clients), `unavailable`.

## Commands used by the PoC

### status

Request: `{"id":1,"cmd":"status"}`

Reply (additive fields allowed; ignore unknown):

```json
{"id":1,"ok":true,"frame":5321,"protocol":1,"fps":59.8,"state":"play",
 "stalled":false,"draws":2410.3,"replayed":5320,
 "window":{"width":1280,"height":720},"uptime":95.2,"clients":1,
 "input":{"queued":0,"exclusive":false},"map":"KokiriForest_Child",
 "game":{"event":false,"loading":false,"age":"child"},
 "version":"3.0.0-...","renderer":"Vulkan: ..."}
```

`state` is a draw-count guess: `boot`, `intro`, `menu`, `loading`, `play`,
`unknown`. `game` is a read-only OoT snapshot from Link's update.

### subscribe (the client's "watch")

Request:

```json
{"id":11,"cmd":"subscribe","events":["oot"],"every":6,
 "flags":["PlayerStatus.Life","0x31ab5580"],"changes":true}
```

- `events`: `present`, `state`, `log`, `map`, `oot` (default `present`,`state`).
- `every`: frames between samples (default 60; 1..216000).
- `flags`: up to 64 names (letters/digits/`_`/`.`) or `0x`+8 hex. Lookup order
  Bool, Int, Float, Enum, String64, UInt; absent/unreadable -> `null`. Enum ->
  `"#"` + 8 hex (MurmurHash3 of the value's name).
- `changes`: `true` reports changed Bool/Int/Enum/UInt flags by hash
  (default `false`). First sample reports `{}`; `more` counts omitted changes.

`unsubscribe` (or disconnect) ends the stream.

Ack: `{"id":11,"ok":true,"frame":6000,"events":["oot"],"every":6}`

Then events:

```json
{"event":"present","sub":11,"frame":5600,"fps":59.9,"state":"play","draws":2405.0}
{"event":"state","sub":11,"frame":5720,"state":"loading","from":"play","draws":5.0}
{"event":"log","sub":11,"frame":5721,"line":"[hos ...] ..."}
{"event":"map","sub":11,"frame":5900,"map":"HyruleField_Child","from":"KokiriForest_Child"}
{"event":"log_dropped","sub":11,"lines":40}
{"event":"oot","sub":11,"frame":6002,"game":{...}}
```

### The OoT snapshot (`status.game` / `oot` event `game`)

| field | meaning |
|---|---|
| `t`, `frame` | monotonic seconds + present at sampling |
| `map` | sampled map name, or null |
| `pos` | Link [x,y,z] in metres, or null |
| `facing`, `camera` | yaw in degrees |
| `event`, `loading`, `age` | event playing, map loading, `"child"`/`"adult"` |
| `life`, `max_life`, `rupees` | quarter-hearts and rupees |
| `prompt`, `music` | A-button action and music names |
| `flags` | watched values: `{nameOrHash: value}` |
| `changed` | changed flags: `{hash: value}` (`{}` first sample) |

Notes: samples stop on the title/pause/loading (whenever Link is not updated);
compare `game.frame` to the reply `frame` before steering. A `status` request
asks for one fresh snapshot, shared by requests waiting together. A subscriber
keeps <=32 pending changes; a fuller queue sets `more` (client must re-read its
flags).

## Reconnect / lifecycle

- Up to 8 clients; each connection is independent; disconnect ends that
  subscription. Reconnect = open a new pipe and re-`subscribe`.
- `quit` closes the game; `relaunch` restarts it (Windows: a new process on the
  same `HOS_CONTROL` pipe name).

## Windows caveat (from the docs)

CI has no Windows runner, so the named-pipe bridge, `launch` on Windows and
remote-client refusal are only exercised by a manual Windows tester. The Python
client is Windows-aware; a C# client talks to the pipe directly via
`System.IO.Pipes.NamedPipeClientStream`.
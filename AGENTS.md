# Agent Instructions — Switch Achievements

These instructions apply to **every coding agent** working in this
repository, regardless of vendor or model. This file is the shared
source of truth; do not create a competing `CLAUDE.md` unless the user
explicitly requests agent-specific instructions.

## 1. Start by understanding the repository state

Before doing work:

1.  Read this file and the relevant sections of `README.md` and
    `docs/PROJECT_STATUS.md`.
2.  Inspect `git status --short`, the current branch, and `git diff` for
    files relevant to the task.
3.  The repository may contain substantial user-owned, uncommitted work.
    Treat every pre-existing modification and untracked file as
    intentional user work.
4.  Never assume a modified file is disposable, generated, or yours
    merely because it is uncommitted.
5.  If the requested task overlaps existing changes in a way that risks
    overwriting them, stop and explain the conflict before proceeding.
6.  Inspect the relevant existing code before proposing or changing
    architecture. Preserve established ownership boundaries.
7.  Distinguish verified facts from assumptions in plans and reports. Do
    not invent APIs, types, methods, file paths, or test results.

Do not run cleanup commands or broad formatting operations as a
shortcut.

## 2. Authorization and task boundaries

- A request to investigate, explain, review, or plan is **not**
  permission to implement.
- Implement only the work explicitly authorized by the user.
- Keep changes within the requested scope. Do not bundle opportunistic
  refactors, renames, formatting, dependency upgrades, or unrelated
  fixes.
- If a requirement is ambiguous and materially affects architecture,
  data safety, security, or user-visible behavior, ask one focused
  question before implementation.
- Do not perform live game tests, modify the Longshot installation,
  write `longshot_env.txt`, alter environment variables, or change
  Windows registry settings without explicit authorization for that
  specific live operation.
- Never silently broaden a task from a documentation change into
  source-code changes. Documentation-only authorization does not
  authorize source, test, or project-file changes.
- Ask before expanding scope. If you notice an unrelated problem, report
  it; do not fix it silently.
- Prefer small, targeted changes. Avoid speculative abstractions.

## 3. Git safety — strict rules

- Stay on the existing `development` branch. Do not switch to `main`,
  create branches, or change branches unless explicitly instructed.
- Do not commit, push, force-push, amend, rebase, merge, reset, clean,
  stash, or discard changes unless the user explicitly asks for that
  exact operation.
- Do not overwrite, revert, stage, or remove unrelated user changes.
- Do not use destructive Git commands.
- At the end, report the branch, commit, files changed, and whether any
  pre-existing changes were touched.
- If the user asks for a change to a file that already has uncommitted
  edits, inspect those edits first and preserve any relevant content.
  Make the smallest safe targeted update.

## 4. Architecture boundaries

The solution is divided into three principal projects:

### `SwitchAchievements.Core`

- Keep Core platform-neutral and integration-independent.
- Do not add WPF, Windows APIs, Longshot references, process-enumeration
  implementation, named-pipe implementation, or filesystem-specific
  integration logic to Core.
- Keep Core contracts and domain policy testable without a running game.
- Preserve the generic identity contracts (`IGameIdentityProvider`,
  `IIntegrationIdentityFormat`, `GameIdentityResolver`, `IdentifiedGame`)
  and the integration-specific provider model: each integration supplies
  its own evidence provider and format; Core resolves evidence against
  the catalog.
- Game identification must use explicit evidence and deterministic
  resolution. Do not infer identity from a launcher profile, display
  name, or a single weak signal.
- Preserve the distinction between Unknown, Unsupported, Conflict, and
  Identified outcomes.
- Preserve process identity using the process fingerprint (PID plus
  start time), not PID alone.
- Preserve snapshot null semantics: `null` means no value was sampled,
  not zero, false, or an empty string.

### `SwitchAchievements.Longshot`

- Keep Longshot-specific and Windows-specific behavior here.
- Implement integration-specific identity providers and formats through
  Core contracts.
- Keep process selection conservative. Never select the first candidate
  as a fallback. Never resolve ambiguity using parent/child
  relationships or experimental pipe-owner observations.
- Exclude only positively identified Longshot helper processes. Unknown
  or unreadable classifications must not silently discard a candidate.
- Keep executable identity parsing bounded and validate binary layouts
  before reading fields.
- Do not persist or log full process command lines. Command-line
  evidence must be parsed and discarded according to the existing
  privacy design.
- Keep the control bridge on the local named pipe. Do not introduce TCP
  or enable guest-memory access.

### `SwitchAchievements.App`

- Own WPF UI, application composition, settings, tray behavior,
  startup/shutdown, and presentation.
- Keep business policy and reusable domain contracts out of code-behind
  where the existing architecture already provides services or view
  models.
- Preserve coordinated lifecycle and disposal behavior. Avoid
  introducing background work that outlives the service or process it
  belongs to.
- Keep UI presentation truthful about monitoring, identification, bridge
  availability, and snapshot freshness.

### Catalog and persistence

- The developer-managed catalog under `catalog/` is the source of truth
  for supported game metadata and definitions.
- Catalog JSON is embedded into the application at build time.
- The planned SQLite database is for mutable player-specific state, not
  a replacement for the developer-managed catalog.
- User-importable achievement packs are not part of the current product
  direction.

## 5. Product scope and explicit decisions

Current supported target:

- Longshot 3.0.0.
- The Legend of Zelda: Ocarina of Time Switch 2 recompilation.
- Catalog game ID: `zelda-ocarina-of-time`.
- Integration ID: `longshot`.

Explicitly deferred:

- Switch 1 support.
- Eden.
- Emulator integrations.
- Generic ROM hashing as a substitute for integration-specific identity.
- User-importable achievement packs.

Do not add any of these without a new explicit product decision from the
user.

## 6. Longshot bridge safety

- `HOS_CONTROL` enables Longshot’s local control bridge and must remain
  opt-in.
- The only approved application setup flow is the existing explicit
  confirmation flow in the dashboard.
- Do not silently enable the bridge at startup, during game detection,
  or as a side effect of monitoring.
- `HOS_CONTROL_MEM` is separate and must remain disabled. Do not enable
  raw guest-memory access.
- The pipe is local and restricted to the current Windows user, but
  other processes running as that same user may be able to issue
  game-control commands. Preserve the security warning and user consent.
- The user’s real `longshot_env.txt` is intentionally enabled. Never
  remove or change it, or anything else in the user’s Longshot
  installation, without explicit authorization.
- Do not enable live tracking through global or persistent
  environment-variable changes (User or Machine `HOS_CONTROL`).
- Automated tests must use temporary directories, never the real
  installation.
- A live acceptance test that writes configuration or launches/restarts
  a real game requires separate user approval.

## 7. Testing and verification

- Follow existing coding conventions. Use asynchronous operations
  appropriately and preserve cancellation, disposal, process-lifecycle,
  and concurrency guarantees.

- Add or update deterministic tests for behavior changes.

- Prefer fakes, injected dependencies, temporary directories, and
  explicit synchronization.

- Do not introduce arbitrary sleeps to make asynchronous tests pass. Use
  the existing `StateTransitionObserver` pattern or another
  deterministic synchronization mechanism, with bounded waits.

- Test failure, cancellation, disposal, process replacement, and
  repeated lifecycle behavior where relevant.

- Run the narrow relevant test set first, then the full suite when
  practical.

- Use the repository’s .NET 10 SDK and solution:

  ``` powershell
  dotnet build SwitchAchievements.slnx
  dotnet test SwitchAchievements.slnx
  ```

- The application and test projects target Windows (`net10.0-windows`).
  If the current environment cannot run Windows-targeted tests, state
  that limitation clearly; do not claim tests passed.

- Also run `git diff --check` for source/documentation changes.

- Report exact commands and actual results. Do not claim a build, test,
  live test, or manual UI check that was not performed.

## 8. Documentation expectations

- Keep `README.md` user-facing and concise. It describes what exists now
  and clearly separates implemented functionality from planned
  functionality.
- Keep milestone history, detailed evidence, limitations, and design
  rationale in `docs/PROJECT_STATUS.md` or the relevant focused
  document.
- Update documentation when an authorized implementation changes
  architecture, supported scope, setup, or milestone status.
- Do not describe planned achievement functionality as implemented.
- Do not remove historical research evidence merely because it is no
  longer the current product overview.

## 9. Working efficiently across agents

- Treat this file as vendor-neutral instructions for Claude Code, Codex,
  Cline, and other agents.
- Prefer focused repository inspection over repeated broad scans.
- Do not repeat planning that has already been completed unless new
  evidence or a changed requirement justifies it.
- Be cost-conscious: avoid unnecessary tool calls, redundant full-suite
  runs, and large rewrites.
- Before handing off, summarize the exact files changed, tests/builds
  run, unresolved risks, and the next concrete step.
- Implementation completion reports must include: files changed;
  behavior changed; build and test verification (exact commands and
  results); limitations or unverified behavior; Git state (branch, HEAD,
  status, and confirmation that pre-existing work was preserved); and
  remaining concerns.
- Do not claim that a task is complete while required verification
  remains outstanding.

## 10. Current starting point

For the detailed current state, read `docs/PROJECT_STATUS.md`. At the
time that status document was prepared, the next proposed work was
**M5.9 — Achievement Definition System**, beginning with investigation
and planning only. Do not begin M5.9 implementation without explicit
authorization.

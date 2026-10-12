# Workspace sessions & recovery (feature/agent-x-workspace-recovery)

Project-aware session identity plus guarded process lifecycle, on top of
merged PR #4. One global `agent-x-main` session becomes N isolated
workspaces; a dead Pi process becomes a Reconnect button, not lost work.

## Identity

- `AgentXWorkspace`: one project root → one slug → one session id
  (`ax-<readable-tail>-<8-hex-hash>`, hash over lowercased canonical path so
  case aliases share identity). Pi's working directory IS the project root
  (legacy keeps managed `agent-cwd`): relative tools execute in the selected
  project with the user's OS authority — isolation BETWEEN projects, NOT a
  sandbox (stated in GUI detail text on every connect). Pi session files stay
  in the shared managed `sessions/` dir; Pi groups by working directory, so
  files stay distinct without moving anything. Links/junctions resolve to
  their target in `CanonicalizeProjectRoot`, so aliases share one identity.
- Agent X's own files (registry, session files) never go into project roots;
  only the user-chosen Pi process runs there. Relative execution is proven
  live: Pi reads pre-seeded project files and its relative writes land in
  the project (asserted on disk), never cross-workspace.
- Legacy preserved: `agent-x-main` / `agent-cwd` addressable as
  `AgentXWorkspace.Legacy`, paths byte-equal to `PiSessionOptions`, first
  item in the GUI list, never migrated or deleted.
- `WorkspaceRegistry` (`workspaces.json`): idempotent per root (case- and
  alias-insensitive), loud `InvalidOperationException` on slug squatting.
  `Load` validates every entry fail-closed (slug charset, `ax-<slug>`
  session consistency, reserved `agent-x-main`/`legacy` names, rooted
  canonical projects, no duplicate slugs/session ids/roots) and never
  modifies the file; corrupt or hostile files throw `InvalidDataException`
  byte-preserving. The GUI enters recovery mode on unreadable registries:
  registration disabled, legacy still connects. Atomic saves.

## Lifecycle

- `PiConversation(Func<PiRpcClient>)`: factory-built clients so
  `ReconnectAsync` retires the previous writer FIRST (detach + dispose)
  and only then connects the replacement — two live Pi processes never
  share a session, even briefly. Restored history is kept until the refresh
  lands; a failed reconnect throws and never wipes known history; no receipt
  is issued — interruption is never reported as success (interrupted runs
  keep `EndAssistantTurn(seq, null)`: no receipt, `LastReceipt` untouched).
  `ValidateSessionMatch` refuses foreign histories at connect/reconnect.
- `ConnectionLost` forwards transport death; the GUI records one deduped
  error entry and offers Reconnect (retried manually, never auto-masked).
- `PiRpcClient.RunBashAsync`: typed immediate shell execution (no LLM run)
  for tool-path verification and disposable test seeding.
- GUI: workspace picker (legacy default) + Add project + explicit
  Reconnect. One controlled transition: while a conversation object exists
  (connected OR dead) only Disconnect or Reconnect may change state, and a
  `_connecting` flag blocks re-entrant connects; stale event posts from a
  previous workspace are dropped by sender check. Connect-while-connected
  to a *different* workspace is refused. Reconnect is refused while a run
  is in flight or a connection is underway.

## Pi behavior found (2026-10-12, verified live)

- Bare `bash` RPC executes and shows in live `get_messages` but is NEVER
  persisted: no session file is created until an agent run settles. So
  cross-process test seeds use one tiny exact-echo prompt per workspace
  and assert on persisted USER text, never model wording.
- Session files are `<utc-timestamp>_<session-id>.jsonl` in `--session-dir`;
  `get_state.sessionId` echoes the requested `--session-id` (asserted).

## Evidence (this slice)

- `dotnet test`: 87/87 (44 carried + 37 workspace/session incl. an 8-case
  hostile matrix, link identity, catalog recovery, no-recreate + 6 real-Pi session tests).
- Real-Pi disposable sessions (temp roots, unique ids, shell-agnostic
  relative file roundtrips + 3 tiny exact-echo prompts total, ~6 s each):
  isolation across 4 sequential connects (relative reads/writes land in the
  right temp projects, histories disjoint, `get_state.sessionId` verified
  against `ValidateSessionMatch`), 5 sequential reconnects under a healthy
  process with marker count stable (single-writer evidence), legacy
  `agent-x-main` still opens read-only from a temp managed root, failed connect/reconnect loud with
  clean state. Full timings in test output.
- Terrorbats packs reviewed read-only (reopen-idempotency, crash-replay,
  false-success patterns informed the tests); installation untouched,
  nothing destructive run; Antigravity owns it.

## Not covered (GUI smoke gate)

- Interactive click-through: add project → connect (authority note shown) →
  prompt → tool run → disconnect → switch workspace → reconnect → copy →
  reopen/restore; corrupt-registry recovery mode (Add disabled, legacy
  connects); stale-callback drop on fast workspace switching.
- Real process-kill mid-run (dispose-based reconnect + failed-connect
  covered; `ConnectionLost` wiring minimal + deduped).

Interactive click-through: add project → connect → prompt → tool run →
kill/reconnect → copy → disconnect → switch workspace → reopen/restore.
Transport-kill mid-run (real process death, not dispose) is untested —
`ConnectionLost` wiring is 3 subscribed lines plus the dedupe above.

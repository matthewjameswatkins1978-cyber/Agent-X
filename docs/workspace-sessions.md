# Workspace sessions & recovery (feature/agent-x-workspace-recovery)

Project-aware session identity plus guarded process lifecycle, on top of
merged PR #4. One global `agent-x-main` session becomes N isolated
workspaces; a dead Pi process becomes a Reconnect button, not lost work.

## Identity

- `AgentXWorkspace`: one project root → one slug → one session id
  (`ax-<readable-tail>-<8-hex-hash>`) → one managed working directory
  (`<root>/workspaces/<slug>/cwd`). Pi session files stay in the shared
  managed `sessions/` dir; Pi groups by working directory, so files stay
  distinct without moving anything.
- Agent X never writes into the project root (asserted: project dirs stay
  empty after connects, prompts and tool runs).
- Legacy preserved: `agent-x-main` / `agent-cwd` addressable as
  `AgentXWorkspace.Legacy`, paths byte-equal to `PiSessionOptions`, first
  item in the GUI list, never migrated or deleted.
- `WorkspaceRegistry` (`workspaces.json`): idempotent per root, loud
  `InvalidOperationException` on slug squatting, corrupt file throws
  `InvalidDataException` with the file left in place, atomic saves.

## Lifecycle

- `PiConversation(Func<PiRpcClient>)`: factory-built clients so
  `ReconnectAsync` can swap a dead process for a fresh one on the same
  persisted session. History is kept until the refresh lands; a failed
  reconnect throws and never wipes known history; no receipt is issued —
  interruption is never reported as success (interrupted runs keep
  `EndAssistantTurn(seq, null)`: no receipt, `LastReceipt` untouched).
- `ConnectionLost` forwards transport death; the GUI records one deduped
  error entry and offers Reconnect (retried manually, never auto-masked).
- `PiRpcClient.RunBashAsync`: typed immediate shell execution (no LLM run)
  for tool-path verification and disposable test seeding.
- GUI: workspace picker (legacy default) + Add project + explicit
  Reconnect. Connect-while-connected to a *different* workspace is refused
  with "Disconnect before switching workspaces — histories never mix."
  Reconnect is refused while a run is in flight.

## Pi behavior found (2026-10-12, verified live)

- Bare `bash` RPC executes and shows in live `get_messages` but is NEVER
  persisted: no session file is created until an agent run settles. So
  cross-process test seeds use one tiny exact-echo prompt per workspace
  and assert on persisted USER text, never model wording.
- Session files are `<utc-timestamp>_<session-id>.jsonl` in `--session-dir`;
  `get_state.sessionId` echoes the requested `--session-id` (asserted).

## Evidence (this slice)

- `dotnet test`: 64/64 (44 carried + 15 workspace unit + 5 session tests).
- Real-Pi disposable sessions (temp roots, unique ids, echo-only bash +
  3 tiny exact-echo prompts total, ~6 s each): isolation across 4
  sequential connects, 3 sequential reconnects with stable history, failed
  connect/reconnect loud and state-clean. Full timings in test output.
- Terrorbats packs reviewed read-only (reopen-idempotency, crash-replay,
  false-success patterns informed the tests); installation untouched,
  nothing destructive run; Antigravity owns it.

## Not covered (GUI smoke gate)

Interactive click-through: add project → connect → prompt → tool run →
kill/reconnect → copy → disconnect → switch workspace → reopen/restore.
Transport-kill mid-run (real process death, not dispose) is untested —
`ConnectionLost` wiring is 3 subscribed lines plus the dedupe above.

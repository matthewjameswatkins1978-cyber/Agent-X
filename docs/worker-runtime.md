# Independent Worker Runtime (C3) — Core contract for Agent TWO

Runs multiple genuinely independent Pi workers at once — separate
processes, sessions, feeds and lifecycles — like separate `pi` processes
in Windows Terminal. Switching the selected worker changes only the VIEW;
background workers are never interrupted. Core only: no GUI changes in
this PR.

## Public contract (AgentX.Core)

```csharp
var manager = new PiWorkerManager();            // real managed root; tests pass a temp root
PiWorker worker = manager.CreateWorker(projectRoot, label: "Agent ONE"); // persists workers.json
worker = manager.SelectWorker(id);              // view pointer only; raises SelectionChanged
IReadOnlyList<PiWorker> manager.Workers;        // identity-keyed, ordered
PiWorker? manager.Selected;
await manager.RemoveWorkerAsync(id);            // dispose + unpersist
await manager.DisposeAsync();                   // kills every worker process (no orphans)

await worker.ConnectAsync(ct);                  // first connect (or safe reconnect after death)
await worker.ReconnectAsync(ct);                // process swap, session kept, feed preserved
var (text, receipt) = await worker.SendAsync(prompt, progress, ct); // one run per worker
await worker.CancelAsync(ct);                   // cancel THIS worker's wait + best-effort abort
await worker.GetStateAsync(ct);                 // fresh engine state
var models = await worker.GetModelsAsync(ct);   // probed; per connection
var confirmed = await worker.SetModelAsync(provider, modelId, ct);      // validated + CONFIRMED
var levels = await worker.GetThinkingLevelsAsync(ct);                   // per current model
confirmed = await worker.SetThinkingLevelAsync(level, ct);              // validated + CONFIRMED

worker.Feed;                     // THIS worker's ConversationFeed (render whichever is selected)
worker.Snapshot();               // WorkerSnapshot { …, Version, Capabilities, LastConfirmedState, ProcessId, Verified }
worker.History;                  // restored session messages
events: WorkerAdded / WorkerRemoved / SelectionChanged   (manager)
        ActivityChanged / ToolActivityChanged / ConnectionLost   (worker)
worker.Version; worker.Capabilities;                        // after connect
```

Errors are loud and typed: missing project → `DirectoryNotFoundException`
(never recreated); foreign session → `PiRpcTransportException`; duplicate
session owner → `InvalidOperationException`; cross-process session owner → `SessionLockedException` (refused before any Pi launch); unsupported operation →
`NotSupportedException` (probed); accepted-but-not-applied state →
`InvalidOperationException`. Nothing silently falls back.

Connection lifecycle (failure-path rules): a worker is only USABLE after
identity validation AND capability probes succeed — `Snapshot().Verified`
reports it. Identity proof is the engine-reported session id; when it is
missing, Pi's own session-file name must carry the expected id, otherwise
the connection is refused as unverified (`PiRpcTransportException`). Any
failed validation/probe QUARANTINES the connection (process disposed; if
retirement itself fails the reference and claim are kept for retry — never
a silent orphan) and ConnectAsync re-verifies instead of taking a
shortcut; a failed reconnect clears capabilities and confirmed state before
attempting the swap. Disposal is bounded: the lifecycle-gate wait defaults to 10s (test seam); a connect holding the gate past it makes DisposeAsync throw `TimeoutException` having torn NOTHING down — claim and references retained for a retry — and a connect queued behind a completed disposal re-checks disposal inside the gate and never starts or publishes a process. 
`SendAsync` closes the visible turn on cancellation,
command rejection AND transport failure — never leaves a streaming row
open, never fabricates a receipt. Persistence is transactional: create
rolls back to the on-disk bytes on save failure; remove retires the Pi
process FIRST and only then unpersists, so a failed save leaves a
dead-but-owned worker (a retry completes it) — never an unreachable live
process; manager disposal attempts EVERY worker and throws an
AggregateException listing any it could not retire (those stay owned for a
retry).

### GUI integration instructions (Agent TWO)

1. Keep the existing workspace/single-connection path untouched; workers
   are additive. Do NOT route worker events through
   `WorkspaceSessionCoordinator` (its "connected pins the tab" rule is for
   the single-connection model and would refuse worker switching).
2. On startup create one `PiWorkerManager` per app. Tab strip =
   `manager.Workers`; bind the transcript to `manager.Selected.Feed`.
3. `SelectionChanged` → rebind the transcript to the new
   `Selected.Feed` and resubscribe its `Changed` event (same
   `FeedEventCoalescer` + `Dispatcher.UIThread.Post` pattern as today).
   Selection never touches processes.
4. Subscribe per-worker `ActivityChanged`/`ToolActivityChanged`/
   `ConnectionLost` and marshal with `Dispatcher.UIThread.Post` — worker
   bookkeeping already writes the feed on the RPC thread (the feed is
   lock-protected, exactly like the bridge rows today).
5. Prompt path mirrors the existing one (AddUser/BeginAssistantTurn are
   done inside `worker.SendAsync`; the host just streams `progress` and
   renders receipts). Cancel button → `worker.CancelAsync()`.
6. Model/thinking pickers: populate from `GetModelsAsync` /
   `GetThinkingLevelsAsync`, apply via the Set methods, display
   `LastConfirmedState` (never the picker value alone).
7. The launcher path stays hardcoded (`D:\AgentTools\bin\pi.cmd`); no
   preflight or installer work is in this PR.

## Identity and storage

- `workers.json` in the managed root (atomic saves, fail-closed load —
  corrupt file throws and is preserved). Completely separate from
  `workspaces.json`; workers never read/write/migrate workspace or legacy
  registrations.
- Session ids are `axw-<8hex>`, disjoint from workspace (`ax-<slug>`) and
  legacy (`agent-x-main`) sessions by construction (tested).
- Project directory ↔ session is fixed at creation. Changing directories =
  remove worker + create new one; an active process is never retargeted.
- Process-wide single-writer claims: one live Pi process per session id.
  A second worker on the same session is refused (`PiSessionClaims`).
  Cross-process (two Agent X instances) is protected by
  `CrossProcessSessionLock`: an OS-held exclusive file handle on
  `sessions/<sessionId>.lock`, acquired before any Pi launch. A competing
  instance gets `SessionLockedException` (an `InvalidOperationException`)
  and starts nothing; the kernel drops the lock when the holder dies, so a
  crash can never strand ownership, and the file itself is never unlinked
  (it proves nothing by existence — the open handle is the lock). Path
  resolution happens in NTFS, so equivalent spellings of the same managed
  root converge on one lock. Scope note: session locks do NOT serialize
  `workers.json` writes — two instances sharing one managed root can still
  race registry saves (last writer wins). That is a separate, pre-existing
  registry risk, reported here and not addressed by this mechanism.

## Model / thinking operations — verified semantics (Pi 1.1.0 and 1.2.0)

Probed empirically with disposable processes (no LLM calls for state ops):

- `set_model` validates properly (`Model not found: provider/id`) and takes
  effect; confirmed via `get_state` in both versions.
- `set_thinking_level` **silently accepts invalid levels** (no error) and
  can clamp valid-but-unsupported levels to another level (e.g. `minimal`
  → `low` when the current model lacks `minimal`). Levels are per-model and
  change after `set_model`. Therefore: pre-validate against
  `get_available_thinking_levels`, then confirm via `get_state`; mismatch
  throws. The compatibility contract asserts the version-tolerant rule:
  state must stay inside the available list (never "must throw").
- **Persistence: none.** Neither `set_model` nor `set_thinking_level`
  writes `settings.json` (byte-hash before/after: unchanged), does not
  affect other processes (fresh process with same agent dir starts at
  defaults), and is not restored when the same session is reopened
  (reconnect → startup defaults). Operating one worker therefore cannot
  change another worker's model or thinking — verified on both versions.

## Version-tolerant compatibility boundary (`PiRpcCompat`)

- `DetectVersionAsync(exe)` — runs `<exe> --version`, parses best-effort.
  INFORMATIONAL ONLY; a null version is fine. Behavior is never gated on a
  version number.
- `ProbeAsync(client)` — classifies against a strict BROKEN vs UNSUPPORTED
  line: get_state / get_messages are REQUIRED baseline commands — a
  rejection or probe timeout throws `PiRpcTransportException` (a broken
  connection is never reported as "unsupported"). All other commands are
  OPTIONAL: rejection or 10s timeout → `false` (fail closed; error wording
  is never matched). Transport faults always propagate. Probes are
  side-effect free: no-arg queries plus idempotent sets re-applying the
  state reported by `get_state`.
  Probed: get_state, get_messages, get_available_models, set_model,
  get_available_thinking_levels, set_thinking_level.
- Not probed (inference cost or session writes): prompt, abort, bash —
  baseline protocol; failures surface as typed exceptions at use time.
- Workers probe at connect AND reconnect (a Pi upgrade between the two must
  not be assumed compatible) and gate operations on the probed surface.

## Tested versions

| Version | How | Connect/state | Models+set | Thinking+set | Prompt | Tool-call id | Cancel | Session restore | Reconnect |
|---|---|---|---|---|---|---|---|---|---|
| 1.1.0 | global shim `D:\AgentTools\bin\pi.cmd` | PASS | PASS (confirmed) | PASS (confirmed; garbage silently accepted → state stays in list) | PASS (healthy echo) | PASS (start/end paired by call id) | PASS (aborted receipt) | PASS (markers restored) | PASS (history kept) |
| 1.2.0 | isolated install (npm prefix; global untouched) | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS |

Research: 1.1.0→1.2.0 `rpc-commands.md`, `rpc.md` and `message-types.md`
are byte-identical; live probes confirm identical behavior including the
silent-invalid-thinking quirk. 1.2.0's changelog has no RPC command
changes (OpenRouter lists, schemas, clipboard, codemode, extension
events). Model LIST contents differ per environment (97 vs 98 entries;
per-key OpenRouter lists in 1.2.0) — lists are queried per connection,
never cached.

Contract tests: `PiCompatibilityContractTests` run against every
discovered executable (global shim + `AX_PI_UNDER_TEST_EXES`, semicolon
list). Provision another version with
`scripts/Provision-PiUnderTest.ps1 -Version X.Y.Z` (isolated npm prefix;
**never** updates the global install); rollback = delete the prefix.

## Measured cost (same machine, Pi 1.1.0, idle CPU ~0.00%)

| Configuration | Parallel connect wall | Concurrent prompt wall | Pi process WS (sum) | Harness WS |
|---|---|---|---|---|
| Baseline (single PiConversation) | 3666 ms | 1202 ms | 9.5 MB | 37.3 MB |
| Worker runtime n=1 | 4125 ms | 1365 ms | 9.5 MB | 40.4 MB |
| Worker runtime n=2 | 4007 ms | 1296 ms | 18.9 MB | 43.4 MB |
| Worker runtime n=4 | 4432 ms | 1774 ms | 37.9 MB | 49.7 MB |

Connect wall is nearly flat through 4 workers; per-worker memory is linear
(~9.5 MB per idle Pi process); the manager adds ~3 MB at n=1 and ~2-3 MB
per additional worker on the host side. No background polling (idle CPU
0.00%). No thresholds invented — these are observations.

## Live matrix evidence (WorkerRuntimeLiveTests, real Pi, disposable roots)

Two workers, distinct sessions: concurrent prompts with zero cross-leak of
history/feed markers; selection switch while connected; model change on A
confirmed while B's state is proven unchanged; one-run-per-worker refusal;
cancel of A leaves B untouched. Kill of A's process mid-run → typed
transport fault + ConnectionLost, B continues healthy, A reconnects to the
same session with history and prompts again. Duplicate-session impostor
refused. Manager disposal kills every worker process (PIDs verified gone).

## Limitations (unchanged or by design)

- Launcher path still hardcoded `D:\AgentTools\bin\pi.cmd` (documented; no
  setting or preflight in this PR).
- One run per worker at a time (second send throws; Pi's own queueing is
  untouched). Cross-session prompts from one worker are not supported.
- Single-writer claims are per process and paired with a cross-process
  session lock (OS-held file handle): two Agent X instances can no longer
  hold one session concurrently. A crashed instance's orphaned Pi child
  is not covered by the lock its death released (no supervisor, by
  design) — a pre-existing hazard, unchanged. Concurrent `workers.json`
  saves across instances remain unprotected (registry risk, reported
  separately).
- Workers do not disconnect explicitly (dispose or process death).
- No GUI yet: the contract above is what Agent TWO consumes; interactive
  click-through and live status polling remain NOT VERIFIED until the GUI
  work lands.

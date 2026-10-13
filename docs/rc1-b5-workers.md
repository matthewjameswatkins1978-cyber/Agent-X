# RC1 B5 — genuine multiworker GUI integration

Each coloured worker tab is one real, independently running Pi process
(`PiWorkerManager`/`PiWorker` per `docs/worker-runtime.md`), with its own
persistent session, working directory, conversation feed, draft, execution
state and cancellation. Selection is a view pointer: switching tabs never
suspends, reconnects or cancels anything. App-only; Core untouched.

## Architecture (additive — the workspace path is preserved)

- One `PiWorkerManager` per app (`ManagedRoot`). Corrupt `workers.json`
  fails closed: worker UI disables, the file stays byte-identical, the
  workspace path works alone (verified live).
- Tab strip mixes both kinds: workspace entries first (legacy startup
  unchanged), worker tabs appended. A tab Tag is exactly one of
  `CatalogEntry`/`PiWorker` — tooltips and automation names carry the
  identity, so a legacy tab is never presented as a worker.
- `SelectedWorker`/`ActiveFeed` resolve live; `ActiveFeed` is the selected
  worker's own feed else the workspace feed. Every view reader
  (sync, rebuild, map, copy/export, expand, overview) goes through it.
- Feed `Changed` handlers drop background traffic by worker id (no focus
  steal, no scroll disturbance); the full rebuild on selection shows
  everything. A UI-thread mirrored id (`_selectedWorkerId`) keeps RPC
  threads off Avalonia controls — this exact violation crashed the first
  smoke build and is fixed.
- Worker switching bypasses `WorkspaceSessionCoordinator` (its
  connected-pins-the-tab rule is single-connection by design). Outgoing
  view state saves under `_lastViewKey` (worker ids namespaced
  `worker/<id>` via `WorkerViewKey`; workspace keys passthrough, no
  migration). Prune allowlists union catalog keys + worker ids, and only
  against healthy sources.
- `LoadWorkspaceTabs` suppresses the whole rebuild (existing B3 rule also
  covers the worker-tab auto-select shadow); `RebuildWorkerTabs` touches
  only worker items and moves the view solely when the viewed worker
  vanished (falls to the manager selection, else legacy).
- Run ownership is per worker id (`_busyWorkers`, `_connectingWorkers`,
  per-run sinks/seqs). `worker.SendAsync` owns its turn — the GUI never
  duplicates AddUser/BeginAssistantTurn/tool events/receipts. Footer and
  status update only while still selected; dots always refresh.
- Shared 100 ms flush tick stops only when nothing streams anywhere.
- Connect/Reconnect/Send/Cancel route by selection to the worker's Core
  methods. Workers have no disconnect in Core: the Disconnect button
  disables on worker tabs (with tooltip) and Remove worker retires the
  process then unpersists (refused while busy/connecting; session files
  stay on disk).
- Create reuses the Add picker's staged confirm panel ("Create worker
  here"); picker staging itself remains display-off NOT VERIFIED.
- Engine display reads the selected worker's `LastConfirmedState`
  (model/thinking/messages genuine, per worker); selection stays pending
  per the adapter — no functional picker.
- Drafts persist per worker id across restarts; `OnClosed` saves the
  visible view and disposes the manager (no orphaned Pi processes —
  verified: zero `axw-*` node processes after close).

## Known limitations (honest)

- Footer `PiRunStatus` snapshots per tab: a run completing in the
  background leaves the tab's restored line older than the feed truth
  (feed rows, dots and receipts are always current). Same characteristic
  as the workspace path, not a worker defect.
- 5+ tabs at 720 px overflow the strip: selection scrolls (verified —
  Epsilon reachable and fully rendered), arrows walk tabs, mouse users
  widen the window. No scroll buttons (would redesign navigation).
- Registering a workspace while viewing a worker moves the view to the
  new tab (existing programmatic-reselect rule; draft saved, nothing lost).

## Verification (disposable projects, real Pi)

- Two workers connected: distinct sessions (`axw-f0a374b7`,
  `axw-8a80e10e`) on distinct node PIDs (3032, 34216).
- Concurrent long runs with mid-stream tab switches: both Healthy,
  transcripts isolated (zero cross-hits both directions), tool blocks and
  receipts per worker.
- Ctrl+Enter byte-identical routing: `HeLLo WoRLD 123!@# ALPHA-MARKER`
  exact in Alpha's session file, absent in Beta's.
- Escape cancels only the viewed worker (Alpha cancelled, Beta Healthy).
- Menu Stop mid-run cancels with a truthful no-health receipt.
- Rapid double-chord during a run: single run (+3 feed entries).
- Kill Alpha's process: Beta sends Healthy mid-outage; Alpha reconnects
  to a new PID with session intact (reconnect #1, 6 messages) and sends
  Healthy again.
- Drafts distinct per worker and across restart (`worker/<id>` keys in
  view-prefs); copy-full exports the selected worker's feed only
  (clipboard proof: Alpha content, zero Beta bytes).
- 10 rapid tab switches then send: landed in the correct session file.
- Remove worker: tab gone, process gone, session files kept, view falls
  back, registry clean; remove-while-busy refused (worker survives).
- Legacy connect/disconnect and tab freedom intact; corrupt workers.json
  startup degrades honestly with the file preserved.
- 4 workers connected: app 162 MB WS / 118 MB private (flat vs single
  session), 4 idle Pi 371 MB WS / 328 MB private (~82 MB private each —
  node runtime baseline; differs from Core's 9.5 MB table, likely a
  different counter — flagged, not reconciled). 8 tab switches in 3.9 s
  (probe overhead dominates).
- Layouts: 720 px usable (strip scrolls to selection), 1080/1440 px
  clean with 5 tabs. 4K + native picker confirm + tall-scroll perf NOT
  VERIFIED (display-off session).

## Review corrections (Lucy, PR #19)

- **P1 worker-removal threading:** `WorkerRemoved` can fire on a
  ThreadPool thread (`ConfigureAwait(false)`). The handler now marshals
  via `Dispatcher.UIThread.Post` and no-ops once `_closed` is set in
  `OnClosed`. Selection repair itself is pure
  (`WorkerDisplayPolicy.RepairTarget`) and tested; removal of a connected
  worker verified live with no violation and correct fallback.
- **P1 refused workspace switch:** the early-return refusal left the
  worker's feed/draft under the reverted workspace tab (prompt
  misrouting). It now restores the kept workspace view (save outgoing,
  reselect, rebind, restore, dots/directory/engine/working/footer)
  before the refusal stays the final word. Verified live: tab, draft,
  feed and session-file routing all coherent; post-refusal send lands in
  the kept session only.
- **P1 engine attribution:** `ActiveEngine` returns
  `EngineForWorker(confirmed-or-all-null)` for worker tabs — an
  unconnected worker shows unknown, never legacy's values. Round-trip
  verified live both directions. Found while verifying: footer engine
  and connection segments went stale across tab switches (only some
  paths refreshed them); every switch path now refreshes them plus the
  connection/activity line from the viewed identity.
- **P2 working indicator:** `RefreshWorkingRow` derives visibility from
  the viewed run only (`ShowWorking`, tested); background runs keep
  their tab dots. Verified live with concurrent runs: idle tab shows no
  indicator while the busy tab's dot burns amber, and vice versa.
- **Close during a live run:** graceful `CloseMainWindow` mid-run
  retired every Pi child (zero `axw-*`/`agent-x-main` node processes
  after). No shutdown redesign; force-kill orphaning remains out of
  scope (true of any process).
- **One-off unknown (recorded, not reproduced):** during corrections
  smoke an app instance vanished and an unfamiliar one was running, with
  no crash log. Cause unknown (attended machine, other processes
  active). Re-ran heavier concurrent switching under watch with zero
  recurrence; no evidence implicating the new code. Carried as an open
  observation, not a claim.

## Files (App-only)

- `src/AgentX.App/MainWindow.axaml(.cs)`: manager wiring, selection
  core, per-worker run state, routed flows, worker Overview/Settings/
  footer branches, Remove/Create buttons. Workspace code paths intact.
- `src/AgentX.App/WorkerViewKey.cs`: id namespacing, labels, tooltips,
  prune union, removal gate (pure, tested).
- `tests/AgentX.App.Tests/WorkerViewKeyTests.cs`: 16 tests.

# RC1 B3: Pi-faithful multiworker GUI (App-side)

Reference: five Pi CLI screenshots (`OneDrive\Pictures\Agent X`, 2026-10-13 —
NOT committed, never copied into the repo). B2's Live visual grammar is the
starting point and is preserved; this slice adds native GUI conveniences
around it. **App-only: `src/AgentX.App` + this doc. No Core changes.**

## Layout (matches the agreed direction)

- **Top:** worker tabs — stable identity tint per position (crimson/amber/
  violet/slate, terminal-tab idea), connection dot (green connected, amber
  working, grey idle), label + detail tooltip, quick switching. State is
  never colour-alone: footer text and tooltips carry it.
- **Left rail:** Live (default) / Overview / Settings-anchored-at-bottom.
  Collapsed 56px (default), expanded 180px with labels. One expand/collapse
  control, no hover-expand, no animation, instant switch. Preference persists
  in `view-prefs.json`. Pages are an enum (`AppPage`); a future Stats page
  plugs in as index 3 + one rail item — no placeholder now.
- **Centre:** the Pi conversation, unchanged B2 grammar with Pi-chrome
  deltas (below). Rail replaces the old top page strip, so the transcript
  gains that row back (~+60px at 720p, measured).
- **Bottom:** composer (unchanged), blue Working row when busy, Pi footer:
  clickable cyan directory (line 1) + status + connection; usage
  (`messages N`, genuinely from get_state) + clickable model/thinking
  (line 2, Pi's `(provider) model • level` shape).

## Feed chrome deltas (Pi reference vs B2)

- Visible `[You #n]` / `[Pi #n]` / `[system #n]` / receipt headers removed;
  headers survive only as UIA automation names (screen readers, scripts).
- Tool header is Pi-format: bold white name + dim `· state #seq`
  (`read · finished #4`) — state word keeps failure readable colour-free.
- Body/mono 13→14px; Markdown headings near body size (16/15/14.5, Pi's
  headings are bold+yellow, not large); square box corners (0).
- `[context summary]` rows render in Pi's violet compaction box.
- Receipts stay (honesty, not in Pi) as quiet dim rows.

## Per-worker-tab view state (App-owned)

Transcript memory stays in Core's coordinator. New `WorkerViewState` per
catalog key: composer draft (persisted in `view-prefs.json`, pruned to
known workspaces), selection by feed Seq, scroll offset (restored
post-layout), StatusText/PiRunStatus snapshot. Connection and engine
identity stay global (one live worker under today's contract).
Save-on-switch-away, restore-on-switch-to; drafts also saved on close.
While connected the coordinator still pins the tab (Core rule) — a
background multiworker view needs Agent ONE's worker-manager contract.

## Engine segments + adapter boundary

`IEngineSettingsRuntime` (App) is the ONLY path for model/thinking
list/selection. `PendingEngineSettingsRuntime` reports `CanConfigure=false`
with an explicit reason; the footer flyouts show today's genuine values
(from get_state) plus that reason — nothing appears functional that
isn't. When Core ships the verified operations, one construction site in
MainWindow swaps the implementation. Directory reuses the existing
guarded workspace flow (explicit confirm in Settings); while connected,
busy or connecting it refuses with a message — the picker never moves a
running agent. Usage shows `messages N` or `messages —`; token/cost
context does not exist here and is omitted, never fabricated.

## Fixes found by GUI testing this slice

- **Ctrl+Enter never sent** (never tested before): the TextBox consumes
  Enter in class handling, so bubble-phase KeyDown never sees the chord.
  Fixed with `AddHandler(KeyDownEvent, …, handledEventsToo: true)` —
  XAML cannot attach tunnel events (AVLN2000). Verified live with a full
  run + receipt. Plain Enter still inserts a newline (multiline kept).
- **Flyouts stacked per click** (3 clicks = 3 UIA copies, screen-reader
  noise): one Flyout instance per button, content refreshed on open.
- **Toolbar clipped at 720px** (pre-existing): row is a WrapPanel now —
  wraps instead of clipping; no overflow at 720/1064/1424 in either rail
  state (measured via UIA geometry, offscreen=False everywhere).
- Native folder picker **hangs with no dialog and no status** in a
  display-off session (verified by handler-entry probe): picker
  confirm/cancel stay NOT VERIFIED here; the composer now says "Opening
  folder picker…" so the state is visible instead of silent. No timeout —
  a timeout would abandon a legitimately long browse.

## Verification (real Windows GUI, disposable sessions)

- Rail collapsed/expanded at 720/1064/1424 (+screenshots while display
  lived); transcript +64px at 720 vs B2 baseline; working set ~162→171MB
  with 100k transcript loaded; 100k import end-to-end ~10.5s wall;
  expansion bounds unchanged (unit) + B2 live evidence stands.
- Per-tab draft/selection/scroll/status round-trips verified (incl.
  across process restart from persisted prefs); background run while on
  Overview settles with receipt + milestones; disconnect→reconnect
  restores; guard refuses Directory while connected; honest flyouts show
  real values + pending reason; tab arrows move selection; rail Space
  activates pages; focusability audit: all chrome keyboard-focusable.
- Core suite untouched: 181/181. No new dependencies.
- Automation caveat: repo-agnostic `ax*.ps1` helpers match windows by
  name prefix and also hit Explorer/Edge titles — B3 probes used a
  process-handle-resolved helper instead; one earlier rail-toggle
  anomaly traced to that race, deterministic since.

## Integration-pending (explicit, not functional)

Model/thinking selection (adapter ready, Core op pending); background
multiworker viewing (coordinator pins the connected tab by design —
Agent ONE's manager changes the ownership rule); token/cost usage
(no Pi source). No placeholders or dead buttons for any of these.

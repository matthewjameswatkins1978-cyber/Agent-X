# Desktop shell verification (real Windows GUI smoke, 2026-10-12)

First interactive verification of the Agent X GUI, driven through UI
Automation (buttons, combo, prompt box, list selection, clipboard) plus
screenshots. Disposable temp project + disposable sessions only; the real
`agent-x-main` history was never written (bare connects do not persist).

## Verified live

1. **Project add + connect.** Workspace registered (seeded via the real
   `WorkspaceRegistry` code path after the native folder dialog proved
   flaky under automation — see gaps), selected in the picker, Pi
   connected: model line, message count, execution directory and
   user-authority note all visible (screenshots).
2. **Execution directory + authority.** `Pi runs in <project> with your
   user authority` shown in detail text and status on every connect.
3. **Prompt + tool op.** Live prompt created `hello-ax.txt` /
   `shell-ax.txt` via the write tool; tool start/end + healthy receipt
   rendered with role colors.
4. **Relative paths.** Files landed in the temp project (on-disk proof);
   session jsonl persisted in the managed sessions dir with the workspace
   session id.
5. **Switch + restore.** Disconnect → legacy → connect (empty, no leak) →
   disconnect → smoke → connect: `Restored ... with 4 message(s)`,
   full persisted detail (user/tool/assistant rows).
6. **Copy.** Selected message byte-exact (`-ceq True` vs the sent string);
   full conversation contains all rows (receipts correctly absent from
   restored history — they are live-feed-only).
7. **Corrupt registry.** `[null]` file: window opens, legacy-only list,
   Add disabled, exact reason shown, file byte-identical afterwards;
   legacy connects; backup restored after.
8. **Mid-run kill.** Pi process killed 2.5 s into a 12-tool task: single
   `Connection to Pi lost: Pi RPC stdout closed unexpectedly` error entry
   (handler/catch dedupe held), 44-char partial preserved, NO receipt for
   the interrupted run, honest status. Reconnect → Connected, history
   restored (`reconnect #3`, 27→28 messages), exactly 1 backend process
   (no duplicates).

## Gaps and anomalies (honest)

- **Add-dialog submit not driven end-to-end.** The native folder dialog
  opens reliably, but its `Folder:` row stayed un-automatable in-session
  (shell-loading flakiness under automation) and repeated Add clicks stack
  multiple modal pickers. Guarded in this shell by a re-entrancy flag, but one manual Matthew confirmation is still needed. Registration itself went through the real
  tested code path (seed program).
- **Phantom Documents registration (unexplained).** During early dialog
  fumbling, `workspaces.json` gained a `Documents` entry without an
  observed dialog submit, around two spontaneous window minimizes. No data
  impact (removed immediately), but Matthew should confirm the Add dialog
  manually once: folder selection must never commit without explicit
  confirmation.
- **Spontaneous minimizes (2x)** during automation; cause undetermined
  (possibly Win32/modal focus interaction). Restored via ShowWindow each
  time; no state lost.
- **UIA child order is reverse-visual** in the virtualized conversation
  list (index 0 = newest); visual order is chronological top-down
  (screenshots). Automation caveat only.
- **ListItem accessible names were class names** — fixed in this shell
  (`AutomationProperties.Name` = role header).

## Cost

6 live prompts total across both shells (disposable sessions), plus
reconnects (no inference). Temp project, sessions and registry entries
removed afterwards; `%LocalAppData%/AgentX` restored to pre-smoke state
(`agent-cwd` untouched, sessions empty, no `workspaces.json`).

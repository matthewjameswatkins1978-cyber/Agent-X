# RC1 B1 navigation verification (real Windows GUI, 2026-10-12)

Workspace tabs + Overview/Live/Settings on the accepted desktop shell.
Disposable temp projects/sessions only; `%LocalAppData%/AgentX` restored
pristine afterwards (agent-cwd untouched, sessions empty, no registry, no
preferences file). Screenshots unavailable session-wide (display off);
evidence is measured UIA geometry + functional assertions + clipboard proofs.

## Ownership repair (PR #10 follow-up, Lucy's P1s)

Programmatic tab reconstruction and the initial selection now flow through
`WorkspaceSessionCoordinator`, the single authoritative transition (guard +
feed-memory sync), instead of suppressed bare selection:

- Unit: 7 coordinator regression tests (initial seeding, Legacy-B-Legacy
  with live-only receipts/notices, refusal while connected, disconnect
  then switch, same-key no-op, RefreshMemory history pickup, blank keys).
- Live: Legacy to temp-B round trip preserved the full live feed including
  the live-only run receipt; refusal while connected reverted selection
  with the histories-never-mix status and Idle state intact; corrupt
  `[null]` registry still degrades to legacy-only tabs with the file
  byte-identical.
- Add-while-connected keeps the connected tab (code path reviewed; native
  picker submit remains NOT VERIFIED — manual script stands).

## Verified live

- **Tabs + pages render.** Workspace tabs (Legacy, ax-nav-a, ax-nav-b —
  accessible names fixed), Overview/Live/Settings tab items, all drivable
  via UI Automation SelectionItemPattern.
- **Connect + run on a tab.** ax-nav-a connected; prompt drove a write tool;
  feed intact across a mid-run page switch (run settled on Overview).
- **Overview is real-only.** Identity, connection+model, session file path
  with byte size (`..._ax-...jsonl`, 19,962 bytes), counts (entries,
  errors, changed files with real paths, heuristic tests), last receipt,
  milestone buttons; honest empties ("No session file yet", "No milestones
  yet"). Bottom status strip visible on every page.
- **Milestone jump.** Clicking `[#5 prompt]` navigated to Live with
  `[You #5]` selected and scrolled into view.
- **Workspace guard.** Clicking ax-nav-b while connected to ax-nav-a:
  refused (`Disconnect ... — histories never mix`), selection reverted to
  ax-nav-a, state stayed Idle, feed untouched.
- **Feed memory.** Disconnect → tab B (own empty state) → back to A: full
  5-entry feed restored from snapshot; no mixing.
- **Composer preservation.** Draft survived Live→Settings→Live (read back
  byte-identical with no intervening write).
- **Settings persist.** Verbose toggled + Dark theme selected →
  `preferences.json` written; restart reloaded both (Dark shown).
- **Geometry.** 720px floor holds: conversation dominates centre
  (358px at 720x561, 1542px at 1080p, 2510px at 1440p); composer usable
  at every size (218px at 720). Collapse/expand measured 822→1126→822.
- **Copy guarantees intact.** Selected message byte-exact in paged shell.

## Not verified (carried + new)

- PR #8 manual checks (native picker submit, physical splitter drag,
  Ctrl+Enter chord) — still outstanding; picker E2E has a precise manual
  script in docs/desktop-shell-verification.md.
- Keyboard arrow navigation of the tab strips (native TabStrip behavior,
  no input queue to drive it here) — code uses the platform control.
- Resize extremes beyond 720–1440p widths; 4K.
- Milestone jump with a streaming (incomplete) entry — jump targets
  settled entries in practice.

## Cost

4 live prompts in disposable sessions (2 workspaces), plus reconnects.
All artifacts removed; user sessions never written (bare connects only).

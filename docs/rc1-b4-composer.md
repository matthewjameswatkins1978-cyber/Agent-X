# RC1 B4 — keyboard-first composer

The text box is the interface. Permanent Send/Cancel buttons removed; the
composer is a full-width `PiPromptBox` with dark Pi styling and the
`Ask Pi anything… (Ctrl+Enter to send)` hint. No new execution paths.

## Behaviour

- **Ctrl+Enter:** submits via the existing `SendPrompt_OnClick` (busy guard,
  blank refusal, draft retention all unchanged).
- **Enter:** inserts a newline at the caret.
- **Escape:** interrupts the active run via the existing
  `CancelPrompt_OnClick`; idle Escape never touches the draft.
- Chord/Escape handling subscribes on the **tunnel** route, so Ctrl+Enter
  sends exactly the box bytes (no leaked newline) and plain Enter still
  inserts its own. Cancellation reporting is unchanged: `Cancelling…` now,
  `Cancelled` only once Pi confirms or the pending operation terminates —
  a finished run never earns a false cancel, a cancelled run never earns a
  health signal.
- Focus: chords never move focus; menu actions return focus to the box.

## Accessible alternative (no permanent icons)

`TextBox.ContextFlyout` (`MenuFlyout`): Cut / Copy / Paste / Select all
(standard `TextBox` methods — the menu replaces nothing the mouse had) plus
**Send message** (`Ctrl+Enter`) and **Stop current run** (`Escape`), enabled
by the UI-free `ComposerActionPolicy` (connected + idle + non-blank to send;
busy to stop). One `ContextRequested` hook serves mouse right-click and
keyboard menu keys (`Shift+F10`); the menu is shown at the control with
`ShowAt` so the positioner flips it above the composer with every item
visible — a pointer-anchored flyout grows down-right off-window and clips
Send/Stop. Verified: `InputGesture="Esc"` is not a valid Avalonia gesture
(runtime `KeyGesture` parse); the item uses `Escape`.

## Files (App-only; Core untouched)

- `src/AgentX.App/MainWindow.axaml`: buttons out, full-width box, flyout.
- `src/AgentX.App/MainWindow.axaml.cs`: tunnel subscription, Escape branch,
  `ComposerMenu_OnContextRequested` + `ComposerSend/Stop/Cut/Copy/Paste/
  SelectAll_OnClick` (all delegate to the existing handlers/methods).
- `src/AgentX.App/ComposerActionPolicy.cs`: UI-free enablement contract.
- `tests/AgentX.App.Tests/ComposerActionPolicyTests.cs`: 10 tests.

## Verification (disposable `ax-b4-test` project, real Pi)

- Buttons absent from the UIA tree; box spans the composer width.
- Shift+F10 opens the full 7-item menu; right-click opens the same menu
  above the composer; enablement observed live (Send greyed when
  blank/idle, Stop lit mid-run and vice versa).
- Menu Send invoked (empty box → `Type or paste…`, no inference) and with
  text → Healthy run; Ctrl+Enter chord → Healthy 8-char run, focus on the
  box; Escape mid-run → `Last run: cancelled`, partial text kept;
  menu Stop mid-run → same cancelled receipt; rapid double-chord during a
  run → single run (+3 feed entries); disconnect + chord → `Connect to Pi
  first`, draft kept byte-identical; reconnect restores history
  (`Restored … with N message(s)`); restart preserves the draft.
- Menu Select all / Copy / Paste doubles box text; idle Escape and plain
  Enter behave; footer Working row truthful mid-run; composer geometry
  unchanged across run boundaries.

# Title-bar brand duplication — focused proposal (repair gate, no code changed)

Visible today: the native Windows title bar reads `Agent X`, and the
app header directly beneath it reads `Agent X` again. Matthew wants
one brand in the app header with native-feeling window controls.
Deliberately NOT done by clearing `Window.Title` (taskbar, Alt+Tab
and accessible name all key off it) or by overlaying the native
frame (hit-testing, snap and DPI would regress).

## Proposed mechanism (Avalonia 12.1.4, Windows only)

Extend the client area into the decoration while keeping system
chrome (VS Code style), instead of full custom chrome:

- `Window.ExtendClientAreaToDecorationsHint = true`
- `Window.ExtendClientAreaChromeHints = PreferSystemChrome`
  (system keeps drawing Min/Max/Close; title text becomes ours)
- `Window.ExtendClientAreaTitleBarHeightHint` sized to the existing
  header row, so the brand cell absorbs the title area
- `Window.Title` stays `Agent X` (taskbar identity, accessible
  name, Alt+Tab all preserved)
- Pointer handling on the title region: `BeginMoveDrag` for move,
  double-click toggles maximize; right-click must still raise the
  system menu where the platform offers it

What stays native and safe: caption buttons (real buttons, correct
DPI scaling, Win11 snap layouts, keyboard access), window frame,
resizing, Win+arrow snapping, taskbar grouping and thumbnails.

## Why this is a proposal, not a patch

The sharp edges are all at window extremes, exactly where Luna
cannot afford a regression:

- Maximized windows: client content slides under the monitor edges
  unless per-state insets are applied; Avalonia does not expose
  caption metrics per DPI reliably, so the padding needs measuring
  on 100/125/150% and both Win10/Win11 chrome geometries.
- Win10 vs Win11 caption-button geometry differs (glyphs, hover,
  snap-flyout affordance); the title-region layout must not assume
  either.
- Drag/restore hit-testing on the custom title region, including
  touch and multi-monitor DPI moves, needs manual verification.
- Fallback if any of the above misbehaves in testing: revert the
  three hints (one-line each) and keep today's duplicate.

## Suggested acceptance (post-Luna pilot)

1. Behind the current shell, no layout changes elsewhere.
2. Verify on Win10 + Win11 × 100/125/150%: normal, maximized,
   minimized-to-taskbar restore, snap left/right/corners,
   system menu, Alt+Tab icon, screen-reader window name.
3. Screenshot each state; any state that cannot be made correct
   keeps native decorations (duplicate brand) rather than shipping
   a broken frame.

No B3, PR, merge or release rides on this note.

# Live visual grammar (B2): Pi roles to Agent X rows

Source of truth: Pi 1.1.0 built-in dark theme
(`dist/modes/interactive/theme/dark.json` in the installed package),
the JSONL event shapes (`docs/json.md`), and renderer behaviour read from
the shipped bundle (edit header bg states, expandable tool results with
remaining-line counts, partial-vs-complete output, Markdown component).
Agent X hexes below are sRGB readings of Pi's OKHSL roles — same hues and
lightness intent, not claimed bit-exact terminal output.

## Palette (Pi dark role → Agent X hex)

| Pi role | Hex | Used for |
|---|---|---|
| text 89% | E4E7EE | body text |
| muted 67% | 9AA3B5 | secondary text, tool output |
| dim 56% | 82889A | headers, quotes, rules |
| violet | C792EA | inline code, bullets, technical refs |
| blue | 82AAFF | links, user accents |
| green | 7FDBA8 | success, fenced code |
| red | F07178 | errors |
| yellow | E5C07B | warnings, headings, receipts |
| cyan (syntaxVariable) | 6FC3DF | reserved |
| userMessageBg 24% | 232F4A | user packet blocks |
| toolPendingBg 24% | 22262E | running tool blocks |
| toolSuccessBg 25% | 1D3A2F | finished tool blocks |
| toolErrorBg 25% | 3D2422 | failed tool blocks |

## Rows

- **User packet:** blue block, exact submitted text (never trimmed),
  selectable, wrappable. Small dim header keeps the `#seq` evidence link.
- **Assistant:** unboxed Markdown flow — yellow bold headings that scale with
  the feed size (body+2/+1/+0.5; exactly 16/15/14.5 at the 14px default),
  violet code spans, green fenced code, violet bullets / numbered lists with
  source indentation kept, blue underlined links with the target shown muted
  beside the label (no navigation), muted italic quotes (consecutive `>` lines
  are ONE block), muted rules, strikethrough, bold-italic. Parsed once when
  the turn settles; streaming shows plain text.
- **Tool operation:** ONE coherent block per call, paired by Pi's observed
  tool-call ID (order-independent, so interleaved same-name calls cannot
  swap outcomes). Ends without a proven start — unknown ID, ambiguous
  no-ID close, or a start dropped at a session boundary — stay as separate
  outcome rows marked "(unverified)", never merged into an unproven
  invocation. Grey while open, green on success, red on failure. Body is
  monospace with diff tinting (`+` green, `-` red, `@@` yellow; `+++`/`---`
  headers exempt). Post-run, bodies reconcile to persisted history ONLY
  under a demonstrable identity contract: exact call-ID identity where
  both sides carry the observed ID (wire and session both derive it from
  `toolCall.id`), else a (name, outcome) group with exactly one row and
  one body — but ONLY when identity evidence is genuinely absent: a key
  holding any leftover with a known (conflicting, duplicated or
  unmatched) ID is blocked from fallback, so a name match can never
  override explicit identity disagreement. Duplicate IDs, two
  same-name/same-outcome leftovers, unparseable bodies and note-less
  rows without an ID keep their existing text; when those differ from the
  leftovers, one truthful marker records the refusal (stack-proof).
  Over-cap output shows an explicit `Show full output (N chars)` action; inline expansion itself is
  bounded (2000 lines / 100k chars) with exact totals in the notice — copy
  always uses the full text, so nothing is silently lost.
- **Dispatch:** feed notifications collapse through a coalescer (at most
  one sync ever queued; each run observes the latest state). Tool/activity
  events mutate lock-protected feed state on the event thread with no
  per-event UI post; derived views (map, milestones) rebuild only on
  structural ticks, never per keystroke or per tool event.
- **Bridge detail rows:** muted mono system rows (`[detail shortid]`,
  `[result shortid]` with duration/exit per the observability contract).
- **Receipts/errors:** one compact muted (receipt) or red-tinted (error)
  row each. Healthy is never presented as task acceptance.

## Honesty rules kept

- Copy always uses the authoritative full text (markdown source for
  assistant rows), never the rendered subset.
- Truncation always carries an explicit preserved notice + char count.
- Through real Pi 1.1.0, the largest single persisted tool body observed
  is ~51KB (~1.1k lines — Pi caps persisted output well below 100k
  lines); it expands inline fully and responsively. The expansion bound
  guards larger restored/hand-made histories and any future cap change.
  A true 100k-line input bounds in unit tests with exact totals.
- `Failed`/`Settled with N problems` wording preserved; no green success
  is ever shown for an unsettled, aborted or errored run.
- `GetRetainedText` stays unwired at UI level: full-output inspection
  flows through history-reconciled feed text instead. Per-call-id retained
  lookup needs a protocol-surface change, which is Agent ONE's domain.
- Usage telemetry IS bound on the Overview: Core refreshes
  FeedBridge.LatestStats on every confirmed settle, and the dashboard
  reads it (context %, tokens, tool calls, Pi-reported cost). No new RPC:
  the workspace path needed conversation.AttachBridge to complete the
  designed binding (workers already had it). Without a snapshot the card
  says it is awaiting the first settled run.
- Markdown subset documented in `MarkdownLite` — a deliberate subset, NOT
  full CommonMark: strikethrough, `__strong__` / `***bold italic***`,
  backslash escapes, double-backtick code spans, nested-list depth from an
  indent stack, merged blockquotes. Emphasis uses simplified whole-run
  flanking: `*` only opens against non-whitespace, `_` only at a word
  boundary on both sides, and an unmatched delimiter run stays literal as a
  whole (`foo__bar__baz` and `** bold**` never emphasise; `foo*bar*baz`
  still does). Unknown constructs (tables, footnotes, HTML, LaTeX) stay
  literal, never dropped; links underline without navigating.
- Glyph fallback (PR2): the bundled Geist/Monaspace faces are Latin-only, so
  missing glyphs fall back in a fixed order — Segoe UI Symbol (arrows, box
  drawing, blocks), Microsoft YaHei (CJK), Malgun Gothic (hangul); see
  `AxFontFallback`. Screenshots and `docs/feed/feed-pr2.md` carry the evidence.

## Layout grid and Inspector (B2)

- Shared geometry lives in `AxLayout` (App, pure, tested): 8px grid,
  12/10/12/8 window margins, 48px rail, 40px nav buttons, 18px icons,
  8px content gutter. ContentLeft = 68 (12+48+8): cards strip, cards
  divider, feed, composer and footer share both vertical edges exactly.
  The 48px header brand cell centres the rabbit over the rail icons AND
  starts brand text on the content datum — one width, both alignments.
- The developer Layout Inspector (F12, default off, hit-test invisible)
  draws datum guides plus a PASS/FAIL readout with pixel deltas
  (`LayoutAudit`, pure, tested; 0.5 DIP tolerance). It refreshes on
  toggle, resize and page switch — never continuously. Verified:
  footer gutter, rail padding, header cell, overlay timing and the
  structural Send/input centring (all Δ0.0; see `docs/gui-ckpt-b2`).
  The capped-panel fill check (`panel-fill`) guards Overview/Settings
  width at and above the 900px cap; below it stretch rules apply.
  Hidden subtrees are excluded by ancestor walk (Avalonia keeps
  `IsVisible` true on children of hidden panels with stale bounds).
  Right-hand metadata never crosses the datum (`clip-*`, INFO-free
  PASS lines). Icon ink centres report as INFO (`ink-*`), never
  verdicts: they justify — or refuse — an optical nudge inside the
  drawing. Shared column geometry is never bent for a symbol.
  Icon ink centres also report as INFO (`ink-*` with the runtime art
  box); the global `rail-ink` verdict compares rendered ink centres
  across the rail and fails loudly on drift.
- Navigation rail holds destinations only (Overview, Feed, Settings).
  All former rail actions relocated without loss: Connect on the
  disconnected card's menu and the empty-state button; Reconnect and
  Disconnect beside them on the card menu; Stop on the composer
  button (Escape kept); Copy selected/full on the feed context menu
  (opened programmatically at the list, composer pattern); Auto-
  scroll lives only in Settings. Card menus select-then-act through
  the existing handlers with per-open enable states; a dropped
  connection with history also offers Reconnect beside the composer
  status line (never menu-only). The card strip always merges both
  populations through one tested order (catalog entries, then workers:
  `WorkerDisplayPolicy.StripOrder`) — a catalog rebuild after a
  workspace registration can never drop a worker tab again. Selecting
  a card scrolls it into view on selection change only (posted
  `BringIntoView`; busy ticks never fight the user's scroll); the
  strip's own scrollbar owns the return journey. Card and feed menus
  carry full constraint adjustment so they stay inside the window.
- The footer context gauge is a flat-green bottom-filling disc with a
  thin outline: green outline + fill for known percentages (sub-6%
  fills are subpixel on the 16px disc — tooltip and text carry the
  exact value), hollow grey for unknown (never a measured zero),
  amber outline only for genuine alert keywords. Same control anchors
  the Usage card at 20px with its own tooltip.
- The rabbit anchor is redrawn artwork (two capped ears, round head,
  symmetric about its centre, 20px in the 48px cell). MEASURED
  finding: geometry renders top-left-fitted in its box (Uniform
  scales to fit but does not centre — full-bleed icons hide this,
  narrow art hugged the left by ~4px). `AxIcon` now centres every
  icon at the system level (`CenteringOffset`, tested; null transform
  for full-bleed), render-only so slots and datums never move. F12
  models the same fitted-then-shifted truth (`rail-ink` PASS means
  pixels agree). Section anchors (mission target,
  model CPU, verification check/warn, usage gauge) stay in the icon
  family at 14px; verification tint follows state.
- Bridge detail rows (`[detail]`, `[result]`, `[progress]`) render as
  quiet attachments: tight rhythm, one step smaller, muted ink
  (`MarkdownView.Dim`, scales with feed size; ANSI colours and diff
  verdicts survive). Evidence preserved, visually grouped.
- The Add confirmation flyout right-aligns to the + button (which
  sits at the window's right edge) and grows leftward into the
  window, with full constraint adjustment as the narrow-window
  fallback; keyboard focus lands on the first action on open.
- A run that settles while another view is selected still retires
  its sent draft under its own key (`VacateSentDraft`, guarded by
  sent-text equality so newer edits survive) — the sent text can
  never resurrect as a phantom draft on return.
- Feed rhythm is group-aware (`GroupMargins` from neighbour roles,
  tested): prompts and receipts keep full turn air; rows inside one
  run join tight; detail rows always tight. No negative margins.
  Restored-session notices ride the footer transient, never the
  composer; a new run vacates stale status when work begins.
  Selection stays charcoal (Fluent focus indication untouched).
- Overview attention is latest-word: unhealthy receipt warns (amber),
  error receipt fails (red), healthy quiets earlier errors (muted
  normal — never success-green), bare errors warn only with no receipt.
  Historical errors ride along in Verification, never discarded.
  Footer and Overview share one snapshot at F1 precision.
- No mission authority exists anywhere in the repo (researched), so the
  mission card stays `No mission connected`; tool work groups under
  Recent Activity by run association (prompt → rows → receipt), with
  outcome dots (green/amber/red/teal/grey), a joining guide rail and
  an earlier-runs affordance. The composer Send/Stop button and Ctrl+Enter
  share one guarded path; routine success leaves no composer row.
- Fluent theme findings (12.1.4, verified against its shipped source):
  the TextBox :focus edge and the checked checkbox come from
  variant-scoped accent keys no post-load override can reach; an
  Application.Resources SystemAccentColor entry crashes startup, and a
  keyed ControlTheme cannot live in Styles (AVLN3000). Working fix:
  implicit BasedOn ControlThemes in Application.Resources (benign keys
  are safe), treatment-live via DynamicResource. Focus edge and check
  glyph are now charcoal/green; #0078D4 was pixel-sampled before.
- Limits, stated plainly: geometry is DIPs (scales linearly with
  RenderScaling); system DPI 100/125/150 could not be varied in this
  harness, so scaling evidence is 720/1200/wide windows only.

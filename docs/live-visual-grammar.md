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
- **Assistant:** unboxed Markdown flow — yellow bold headings (17/15/13.5),
  violet code spans, green fenced code, violet bullets / numbered lists,
  blue underlined links (no navigation), muted italic quotes, muted rules.
  Parsed once when the turn settles; streaming shows plain text.
- **Tool operation:** ONE coherent block per call, paired by Pi's observed
  tool-call ID (order-independent, so interleaved same-name calls cannot
  swap outcomes). Ends without a proven start — unknown ID, ambiguous
  no-ID close, or a start dropped at a session boundary — stay as separate
  outcome rows marked "(unverified)", never merged into an unproven
  invocation. Grey while open, green on success, red on failure. Body is
  monospace with diff tinting (`+` green, `-` red, `@@` yellow; `+++`/`---`
  headers exempt). Post-run, bodies reconcile to full persisted history
  text ONLY under an identity contract (positional name + error agreement
  per pair; identical texts need no proof). Refusal records one explicit
  marker row instead of silently skipping. Over-cap output shows an
  explicit `Show full output (N chars)` action; inline expansion itself is
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
- No usage/cost figures anywhere: no host poll site exists (adding RPC
  surface is Agent ONE's domain), so none are shown rather than invented.
- Markdown subset documented in `MarkdownLite`: unknown constructs stay
  literal, never dropped; links underline without navigating.

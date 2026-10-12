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
- **Tool operation:** ONE coherent block per call (LIFO start/end match in
  `ConversationFeed`; orphan ends fall back to separate rows, never lost).
  Grey while open, green on success, red on failure. Body is monospace with
  diff tinting (`+` green, `-` red, `@@` yellow; `+++`/`---` headers exempt).
  Post-run, bodies reconcile to full persisted history text. Over-cap
  output shows an explicit `Show full output (N chars)` action (never
  claims availability it lacks); `Show less` folds back.
- **Bridge detail rows:** muted mono system rows (`[detail shortid]`,
  `[result shortid]` with duration/exit per the observability contract).
- **Receipts/errors:** one compact muted (receipt) or red-tinted (error)
  row each. Healthy is never presented as task acceptance.

## Honesty rules kept

- Copy always uses the authoritative full text (markdown source for
  assistant rows), never the rendered subset.
- Truncation always carries an explicit preserved notice + char count.
- `Failed`/`Settled with N problems` wording preserved; no green success
  is ever shown for an unsettled, aborted or errored run.
- `GetRetainedText` stays unwired at UI level: full-output inspection
  flows through history-reconciled feed text instead. Per-call-id retained
  lookup needs a protocol-surface change, which is Agent ONE's domain.
- No usage/cost figures anywhere: no host poll site exists (adding RPC
  surface is Agent ONE's domain), so none are shown rather than invented.
- Markdown subset documented in `MarkdownLite`: unknown constructs stay
  literal, never dropped; links underline without navigating.

# Beautiful Feed PR2 — fidelity evidence

Mission: improve and verify feed rendering fidelity on top of merged PR1
(`2ed97c1`), without redesigning the UI. This note records what changed, what
was measured, and the honest limits of each method. Lucy reviews the PR; no
merge/release is implied by this file.

Machine for every number and image below: `MATMUS69-SCUMPU`, Windows NT
10.0.26220, 24 cores, Release builds, .NET 10.

## What changed (renderer only)

`src/AgentX.Core/MarkdownLite.cs`

- Strikethrough (`~~del~~`) and `***bold italic***` inline kinds; `__strong__`
  parses as bold.
- One delimiter run pairs as a whole; `_` emphasis never opens or closes inside
  a word (`snake_case`, `file_name_with_underscores` stay literal), `*` keeps
  CommonMark's looser intraword rule.
- Backslash + ASCII punctuation escapes (`\*not emphasis\*`).
- Double-backtick code spans (`` ` `` inside a span).
- Autolinks require a word boundary (`xhttps://…` is text).
- Consecutive `>` lines merge into one blockquote (CommonMark continuation).
- Nested list depth from an indent stack, so 2-space and 4-space nesting styles
  both keep their levels (capped at three visual levels for narrow windows).

`src/AgentX.App/MarkdownView.cs`

- Heading steps scale with the adjustable feed size: body+2 / body+1 /
  body+0.5, which is exactly the accepted 16/15/14.5 at the 14px default —
  13px and 16px now keep the same hierarchy instead of fixed pixels.
- Inline code keeps its violet role but inherits block size/weight (code in a
  heading is heading-sized); bold-italic uses the voice face; strikethrough
  renders with a rule; links show their target muted beside the label (Pi's
  `mdLinkUrl`); nested list bullets indent by depth.

`src/AgentX.App/AxFontFallback.cs` + `Program.cs`

- Explicit ordered glyph fallback for the bundled Latin faces: Segoe UI Symbol
  (arrows, box drawing, blocks) → Microsoft YaHei (CJK) → Malgun Gothic
  (hangul).

`src/AgentX.App/MainWindow.axaml.cs`

- `AGENTX_MANAGED_ROOT` (rooted paths only) redirects the app's managed state
  for isolated evidence runs. Unset — the normal case — is exactly
  `PiSessionOptions.Default()`. This exists so automated captures never touch
  the real `%LocalAppData%/AgentX` profile.

## Automated evidence

`dotnet test AgentX.sln -c Release`: **447/447 green** (140 App + 307 Core;
baseline was 129 + 293). New coverage:

- `tests/AgentX.Core.Tests/MarkdownLiteFidelityTests.cs` — 14 parser contracts
  (strikethrough, bold-italic, `__strong__`, intraword underscores, escapes,
  double-backtick spans, autolink boundaries, both nesting styles, quote
  merging, content-preservation properties).
- `tests/AgentX.App.Tests/MarkdownViewTests.cs` — 9 renderer contracts asserted
  on the real `MarkdownView` inline tree (heading steps at 13/14/16, code in
  headings, strike decoration, muted link URLs, list indentation, bold-italic
  voice face, prose/mono size split, diff tint).
- `tests/AgentX.App.Tests/AxFontFallbackTests.cs` — the ordered fallback chain.

Controls construct without a render platform (existing convention, see
`AxDesignTests`); pixel truth stays with the screenshots below.

## Screenshot method (and its limits)

Captured from the genuine running application by
`scripts/Capture-FeedEvidence.ps1`:

- **Window capture: `PrintWindow(PW_RENDERFULLCONTENT)`.** Screen capture
  (`Graphics.CopyFromScreen`) fails in this session ("handle is invalid"), so
  every image is a PrintWindow capture of the real window handle — not a
  rendered mock.
- **Driving: UI Automation** (rail buttons via TogglePattern, combos via
  ExpandCollapse + SelectionItemPattern, buttons via InvokePattern).
  Input injection (SendKeys) is unavailable in this session, so no prompt is
  typed: the transcript is a **real Pi session created outside the app**
  (`pi -p "@corpus" --session-dir … --session-id agent-x-main`, two genuine
  `git` tool calls plus a verbatim Markdown reply) and restored by the app on
  Connect. Prompt source: `scripts/feed-corpus-prompt.md`.
- **Isolation:** the app runs with `AGENTX_MANAGED_ROOT` pointed at a
  disposable profile; Matthew's real `%LocalAppData%/AgentX` was never opened
  for writing.
- **Framing:** the UIA ScrollPattern is a no-op on this virtualized ListBox and
  the first row is ~640px tall, so "show the assistant and tool rows" is done
  by window height (tall shots, clamped at 1460px by this 1440p display).

### Inventory (`docs/feed/`)

| File(s) | What it proves |
|---|---|
| `pr2-studio-{13,14,15,16}.png`, `pr2-operator-{13,14,15,16}.png` | Both appearances at every feed size, identical transcript |
| `pr2-{studio,operator}-{13,16}-720.png` | 720px window: long paths/SHAs wrap, footer reflows, nothing clips |
| `pr2-{studio,operator}-14-tall.png` | Assistant corpus row + two real `git` tool rows + headers/edges |
| `baseline-{studio,operator}-14.png`, `baseline-{studio,operator}-14-tall.png` | Same transcript rendered at baseline `2ed97c1` for the semantic-colour comparison |
| `zoom-pr2-*-identity.png` | 3× crops: `0Oo1lI|` stays distinguishable; arrows and CJK render |
| `pr2-settings.png` vs `baseline-settings.png` | Byte-identical (SHA-256 `BB7B…0867`): chrome/settings untouched by PR2 |
| `pr2-copy-footer.png`, `baseline-copy-footer.png` | Copy status line after a byte-exact copy |
| `pr2-evidence.json`, `baseline-evidence.json` | Timings, row counts, copy result per run |

Identical-transcript deltas visible in the baseline ↔ PR2 pairs: baseline shows
literal `~~struck text~~`, no muted link target, two separate quote blocks with
their own `▍` markers, flush (unindented) nested bullets and mis-paired
`***bold italic***`; PR2 renders all of them as intended. Tool rows, headings,
violet code/SHA/paths and diff-tinted output are unchanged in both — the
layout, colours and chrome are otherwise identical.

**Copy/transcript behaviour:** both runs report
`Copied message #1 (1,222 chars) byte-for-byte.` and the clipboard compared
`-ceq` exact against the session JSONL message (`copyCheck.byteExact: true` in
both evidence JSONs). Rendering never feeds copy: copy still reads the Core
feed text.

### Honest limitations

- The corpus session contains 14 entries (16 reported messages); the tall
  viewport realises the user, assistant, two tool rows and the corpus reply —
  the later mission-chatter rows stay below the fold because scrolling could
  not be driven.
- Korean `한국어` in the baseline also resolved on this machine, so the visible
  CJK/arrows evidence does **not** prove baseline tofu; the fallback chain's
  claim is explicit, ordered, deterministic coverage — screenshots confirm
  no regression in either build.
- Streaming was measured Core-side (bench below); live streaming in the app
  could not be driven without input injection.

## Performance — same-state before/after

Harness: `tests/AgentX.FeedBench` (UI-free; Avalonia controls construct without
a render platform). Identical binary source ran at baseline `2ed97c1` (plus the
`AGENTX_MANAGED_ROOT` seam so it could use the isolated profile — no rendering
code) and at this branch's head. Content identical: 500 markdown messages,
1,125 feed entries, 40 realised rows, 9 iterations, medians. Raw:
`perf-baseline-500msg.json`, `perf-pr2-500msg.json`.

| Scenario | Baseline median | PR2 median | Verdict |
|---|---|---|---|
| `parse-cold` (500 messages) | 5.6 ms | 7.3 ms | +1.7 ms for the richer emphasis engine |
| `parse-warm` (500 messages) | 5.3 ms | 6.9 ms | same; no cache added (no speculative optimisation) |
| `rebuild-view` (1,125 entries → items + markdown) | 8.2 ms | 10.1 ms | ≈ +2 ms, follows parse |
| `inline-rows` (40 `MarkdownView.Rebuild`) | 20.3 ms | 20.0 ms | unchanged |
| `appearance-sim` (fonts + rebuild + inlines) | 31.4 ms | 30.2 ms | unchanged within noise |
| `streaming` (2,000 deltas + 40 snapshots) | 28.6 ms | 30.6 ms | unchanged within noise (first run: 41.6 vs 26.6 — variance overlaps) |

First-run (5 iterations) numbers are in the round-1 JSONs kept outside the
repo; conclusions are the same. In-app, the appearance/size switch handler
itself measured 3.7–5.8 ms for the 14-entry session (23 ms first switch, font
load; `pr2-evidence.json`), with popup/automation overhead excluded.

**Conclusion:** no material regression; no optimisation was introduced.

## Reproduce

```powershell
dotnet build AgentX.sln -c Release
dotnet test  AgentX.sln -c Release
dotnet run -c Release --project tests/AgentX.FeedBench -- 500 9 40 out.json
powershell -File scripts/Capture-FeedEvidence.ps1 -AppPath src\AgentX.App\bin\Release\net10.0\AgentX.App.exe `
  -ManagedRoot <disposable-profile-with-restored-session> -OutDir docs\feed -Tag pr2 -TallHeight 1460
```

The disposable profile needs a session under `<root>/sessions/*.jsonl`
(created with `pi -p "@scripts/feed-corpus-prompt.md" --session-dir <root>/sessions
--session-id agent-x-main` in a seeded git working directory) — see the corpus
file for the exact prompt.

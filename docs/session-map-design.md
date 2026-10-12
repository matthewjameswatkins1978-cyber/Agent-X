# Session Map — design (optional panel, first slice shipped)

The Session Map answers "where are we?" without re-reading the whole
conversation: current activity, important milestones, tests, changed files,
decisions and evidence links. It is an **optional, derived view** — the
conversation feed stays authoritative and the map never fabricates.

## Principles (bind to the mission)

- Simple in the front. Clever in the back: the map is one collapsible panel
  fed by pure Core derivation (`ConversationFeed.BuildMap()`).
- Infer only when obvious or previously decided: every row is observed Pi
  activity or an explicitly labeled heuristic. Decisions are **never
  inferred** — there is no `decision` milestone kind.
- Never hide errors, fabricate progress or lose information: errors are
  counted and surfaced, heuristic rows say so, and every milestone carries
  its feed entry number (`#seq`) as the evidence link.

## What the shipped slice derives (all observed, all tested)

| Row | Source | Honesty label |
|---|---|---|
| Current activity | latest `agent_start` / tool / retry event | transient; "idle" when quiet; cleared automatically when a turn settles (`EndAssistantTurn`), so render order cannot strand a stale "working" state |
| Prompt milestones | user entries | verbatim first line |
| Tool milestones | `tool_execution_start`/`_end` | name + outcome; failures flagged |
| Changed files | `edit`/`write` start args with an explicit path | "observed via call, not verified on disk"; `read`/`grep` paths excluded (a read is not a change) |
| Test commands | `bash`/`powershell` commands matching known runners | "heuristic, outcome unknown" — never a pass/fail claim |
| Errors | tool failures, extension errors, stream errors, unhealthy receipts | count + review prompt |
| Run receipts | `agent_settled` per run | settled/aborted/error wording; never "verified" |
| Compaction notices | `[context summary]` system entries | verbatim |

## Explicitly not inferred (future, needs user/extension input)

- **Decisions**: require an explicit mark (user pins a message, or an
  extension records one). No heuristic promotes text to a decision.
- **Test outcomes**: pass/fail needs the command's exit code / output
  parsed by a test-aware tool, not substring matching.
- **Verified file state**: "changed" means Pi called edit/write; on-disk
  truth needs a Garden-verified check (different authority).
- **Milestone importance ranking**: currently chronological; ranking needs
  a stated policy.

## Evidence links

Each milestone stores its feed `Seq`. The GUI shows `[#seq kind] title`,
and Copy actions always use the full entry text, never the truncated
display. Click-to-scroll from a milestone to its entry is the natural next
slice (feed seqs are stable within a session view).

## Verbosity

`SessionMapSnapshot.ToDisplayText(verbose)` — compact by default (titles
only), detail lines on demand. Mirrors the mission's "verbose detail is
welcome but adjustable". The per-message display caps (2,000 chars history,
32,000 live) behave the same way: bounded view, explicit notice, full text
one copy away.

## Performance contract and evidence precision

`BuildMap()` is O(entries) with small constants and runs only on discrete
events (connect, tool lifecycle, run completion) — never per streamed
delta. Live deltas flow through `StreamingTextAccumulator` at a 10 Hz UI
flush. The benchmarks are deliberately scoped micro-benchmarks, not GUI
measurements: the accumulator test covers string batching only (1913 ms
naive per-delta rebuild vs 1 ms batched for 20,000 deltas / ~520 KB), and
the feed-path test covers accumulator flush plus feed concatenation
(50 flushes x 10 KB = 500,000 chars in 6 ms, Core only). Neither includes
Avalonia layout, selection, clipboard, or visual smoke — those stay behind
the human smoke gate (connect → send/stream → copy selected/full →
cancel/reopen/restore) and must not be claimed as verified until exercised.

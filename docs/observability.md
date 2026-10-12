# Agent X observability: what Pi 1.1.0 actually exposes

Established 2026-10-12 by disposable experiments against installed Pi 1.1.0
(model `meta/muse-spark-1.3-contributor`, `--no-session --no-extensions
--no-skills`, neutral temp cwd). Evidence: a raw 88-record JSONL capture of
two tool-using prompts plus model-free RPC probes. Nothing below is inferred
from docs alone; doc-shaped claims are marked "documented, not observed".

## Visibility levels

- **Compact**: significant progress and results. Run started/settled,
  per-tool one-liners (`Tool powershell: ok in 976 ms`), errors, completed
  assistant text. No deltas, no arguments, no diagnostics.
- **Detailed** (default): actual operations, live. Everything in Compact plus
  tool starts with argument summaries, streaming text, exit codes, timings,
  session totals. This is the level the GUI binds.
- **Full trace**: deeper diagnostic evidence and available protocol details.
  Raw record previews, chunk byte counts, tool-call ids, zero-usage flags,
  unknown shapes preserved verbatim. Nothing is dropped silently.

## Directly available (observed on the wire)

- Lifecycle: `agent_start`, `agent_end` (`willRetry`, full `messages`),
  `agent_settled` (`aborted`), `turn_start`, `turn_end` (`message`,
  `toolResults`). User messages carry millisecond timestamps.
- Tools: `tool_execution_start` (`toolCallId`, `toolName`, complete `args`
  object), `tool_execution_update` (`partialResult`), `tool_execution_end`
  (`isError`, `durationMs` monotonic, `result.content[].text`,
  `result.structuredContent`). Observed tool: `powershell` with the full
  command text in `args.command`; durations 976 ms and 5221 ms.
- Streaming: `text_delta` live; `toolcall_start` (id, name) then
  `toolcall_delta` (serialized args) then `toolcall_end` (full ToolCall).
- Direct RPC `bash`: per-chunk `bash_execution_update` (id-correlated; 5000
  lines arrived as 3237 chunks / 208,893 bytes), final response
  `{output, exitCode, cancelled, truncated, fullOutputPath}`.
- Pollables: `get_state` (model descriptor, counts, flags),
  `get_session_stats` (message/tool counts, tokens, cost, contextUsage%),
  `get_messages` (full history), `get_commands` (prompt-command catalog).

## Derivable with verification

- Operation timing: `durationMs` (tool clock) plus client receipt stamps.
  Tool events carry **no wire timestamps** — the tap stamps ObservedAtUtc and
  labels it as such.
- Command exit status: `structuredContent.exit_code` (tool contract) or
  `exitCode` (RPC bash). **Not the same as `isError`**: a wrapped failure
  (`exit 42` run as `bash -c 'exit 42'; echo …`) completes with `isError:
  false` and `exit_code: 0`. Exit status must be read, never assumed.
- Full large outputs: live chunk stream (bounded buffer) or the
  `fullOutputPath` spill file. The inline `output` truncates (observed at
  51,197 chars with `truncated: true`).
- Working directory: launcher-known (Agent X spawns Pi), **not
  protocol-observed**. No `cwd` field appeared in any tool event.
- File inventory: only inside tool args/result text, tool-specifically.
  Generic path extraction is heuristic, not verified — the tap keeps args
  verbatim and summarizes only `command:` / `path:` scalars.
- Live cost/tokens: only via `get_session_stats` polling.

## Unavailable

- Thinking content: blocks arrive empty with an encrypted signature.
- Per-delta usage: the `usage` object rides every `message_update` but reads
  all zeros on this provider (35/35 deltas, max total 0).
- Tool catalog: no command lists executable tools; inventory is emergent from
  observed `tool_execution_start` records.
- Per-tool stderr, cwd, and timeouts on the wire; `partialResult` replace-vs-
  extend semantics (per-tool contract).
- Retry, queue, compaction, and extension shapes in small runs: documented in
  `json.md`, not observed here. The tap preserves them as `OtherEvent` when
  they appear.

## Prototype: PiEventTap (AgentX.Core)

`PiEvents.cs` (typed records + `VisibilityLevel` + per-level `Render`) and
`PiEventTap.cs` (subscribe, decode synchronously before document release,
bounded store of 5000 with counted overflow, per-subscriber fault isolation,
never throws). Decode rules encode the map above: exit-code key spellings,
`isError` ≠ exit status, zero-usage policy, verbatim args, capped previews.
Tests (`PiEventTapTests`, 8) run against a 12-record verbatim fixture taken
from the live capture (`Fixtures/obs-sample.jsonl`).

## GUI binding contract

The panel binds `PiEventTap.Captured`, renders `Render(level)` lines, and
offers the three levels with Detailed default. Deltas append to the live
view; `ToolEnded` lines persist with timing and exit status; `StatsSnapshot`
feeds a status strip (tokens, cost, context %). The GUI must never present
`RunHealthy` as task acceptance (existing receipt rule stands) and must label
client-stamped times as observed, not Pi-reported.

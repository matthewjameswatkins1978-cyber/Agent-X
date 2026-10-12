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
  Redacted content previews with stored/omitted counts, chunk byte counts,
  tool-call ids, zero-usage flags, unknown-shape metadata (never raw values).
  Nothing is dropped silently, and stored is never claimed for dropped data.

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
  Generic path extraction is heuristic, not verified — the tap keeps redacted
  args (4KB cap) and summarizes only `command:` / `path:` scalars.
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

## Capture contract (hardened)

The tap sees STREAMED session events automatically. Correlated poll
responses (`get_session_stats`, direct `bash`, `prompt`, `get_state`) bypass
the subscriber inside `PiRpcConnection` and are captured ONLY when the host
passes them to `PiEventTap.RecordResponse` — proven by a test with a
genuinely pending request. The tap never claims pollables it was not handed;
`StatsSnapshot` in the GUI contract requires the host to record its polls.

## Redaction policy (fail-closed, decode-time)

Every retained string is redacted before storage or broadcast, so all event
properties, subscriber payloads, and render levels are already safe:
bearer/basic values, api_key/secret/token/password key=value pairs
(shell, ENV_CASE, and JSON-quoted forms), private-key blocks, and
GitHub/OpenAI/Slack/AWS token prefixes. Metadata fields (tool names, call
and session ids, dispositions, model ids, type tokens, property names) are
redacted then kept only on identifier shape, else withheld. Unknown shapes
keep metadata only (gated type, gated field names, size) — never raw
values — and unparseable records keep kind and size only.
Patterns run in linear time with a keyword fast-path (a 100KB benign field
decodes instantly). Limits, stated plainly: secrets split across stream
deltas, bare high-entropy tokens with no scheme or key, and novel schemes
are NOT detected; complete detection is not promised.

## Retention bounds and evidence contract (measured)

At most 2000 events and 512KB of retained text (args 4KB, tool output
16KB retained / 2000 preview, deltas 4KB, partials 2KB retained / 1000
preview, bash heads 1000 chars, message windows 400 chars); overflow
evicts oldest-first over an O(1) queue and counts every loss. Four lengths
stay distinct per event: source length (wire total), captured length
(decode kept), stored length (event holds), visible preview length (render
shows). Markers are honest about each layer: `[truncated]` means cut by
the wire or the capture cap (data unavailable); `[+N chars omitted]`
means not retained; `[+N chars stored]` means retained in the event and
accessible through the Events API — and is only ever emitted when the
content is genuinely stored. Tool results and partials retain their full
captured text; chunk heads, message windows, and bash previews retain only
the head and say omitted for the rest. Char counts are labeled chars with
separately computed UTF-8 bytes. Measured burst: 8000 mixed events through
a real connection with hostile and slow subscribers in 4.1s (~1960
events/sec end-to-end), store exactly at bounds, managed heap delta ~5MB
during the burst. Exit status renders as `completed, exit unknown` when
unverified — never confirmed success.

## Prototype: PiEventTap (AgentX.Core)

`PiEvents.cs` (typed records + `VisibilityLevel` + per-level `Render`) and
`PiEventTap.cs` (subscribe, decode synchronously before document release,
O(1) queue capped at 2000 events AND 512KB retained text with counted
eviction, per-subscriber fault isolation, never throws). Decode rules encode
the map above: exit-code key spellings, `isError` ≠ exit status, zero-usage
policy, redacted args (plain, JSON-quoted, and shell forms), metadata
identifier gating with fail-closed withholding, evidence-contract marking
(wire-truncated / omitted / stored with exact counts), and windowed message
retention (true totals, bounded transient). Tests
(`PiEventTapTests`, 8, plus `PiTapHardeningTests`, 15, plus
`PiTapMetadataTests`, 10 metadata/boundary cases) run against a 12-record
verbatim fixture taken from the live capture (`Fixtures/obs-sample.jsonl`).

## GUI binding contract

The panel binds `PiEventTap.Captured`, renders `Render(level)` lines, and
offers the three levels with Detailed default. Deltas append to the live
view; `ToolEnded` lines persist with timing and exit status; `StatsSnapshot`
feeds a status strip (tokens, cost, context %). The GUI must never present
`RunHealthy` as task acceptance (existing receipt rule stands) and must label
client-stamped times as observed, not Pi-reported.

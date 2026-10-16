# Pi Feed Fidelity — Core contract for Agent TWO

Goal: Agent X shows what Pi would show in its terminal — better presented,
nothing important silently lost. All claims below were verified against Pi
1.3.0 itself: its bundled RPC source, recorded wire (`obs-sample.jsonl`),
a real 41-message session file, and isolated no-inference RPC probes
(`--session-dir` in temp, `--session-id` probes, one copied-session replay).
No inference was spent; live prompt/streaming runs remain unverified.

## Capability matrix (wire → Core → feed)

| Pi 1.3.0 wire | Core model | Feed row |
|---|---|---|
| `agent_start` / `agent_settled(aborted)` / `agent_end(willRetry)` | `RunStarted` / `RunSettled` / `AgentRunEnded` | receipt + status (existing) |
| `turn_start` / `turn_end` | `TurnStarted` / `TurnEnded` | Full only (existing) |
| `message_update` + `text_delta` | `TextDelta` → streamed turn, reconciled to authoritative text | conversation path (existing) |
| `message_update` + `toolcall_start` | `ToolCallAnnounced` | Full only (existing) |
| `message_update` + `usage` | `UsageObserved` (standalone only; per-delta usage reads zero on this provider) | Full only (existing) |
| `message_update` + anything else | `OtherEvent` metadata (type + fields + size) | Full only (existing) |
| `message_end` assistant | `AssistantMessage` (preview + true length) | conversation path (existing) |
| `message_start` (user echo) | `OtherEvent` — host already adds the user row; rendering it would duplicate | none by design |
| `tool_execution_start(args)` | `ToolStarted` (args redacted, 4 KB cap + summary) | Detailed detail row (existing) |
| `tool_execution_update(partialResult)` | `ToolProgress` (latest snapshot, never concatenated) | Full only (existing) |
| `tool_execution_end(result, isError, durationMs, exit)` | `ToolEnded` (exit unknown ≠ success; missing result marked) | Detailed result row (existing) |
| `bash_execution_update` | `BashChunk` | conversation path (existing) |
| `auto_retry_start` | **`AutoRetryStarted`** (attempt/max/delayMs + Pi's error text) | **NEW: Detailed notice** |
| `auto_retry_end` | **`AutoRetryEnded`** (success/attempt/finalError; missing success → metadata, never a fabricated verdict) | **NEW: Detailed notice; failures are error rows** |
| `compaction_start` | **`CompactionStarted`** (reason token) | **NEW: Detailed notice** (status line already showed it) |
| `compaction_end` | **`CompactionEnded`** (aborted/reason/error) | **NEW: Detailed notice; reported failures are error rows** |
| `session_info_changed` | **`SessionRenamed`** (name redacted/flattened/capped; missing name → metadata) | **NEW: Detailed notice** |
| `extension_error` and everything else | `OtherEvent` metadata | Full only (existing; extension errors also surface as Full error rows) |
| `get_session_stats` (polled) | `StatsSnapshot` — the only honest token/cost source | status binding, no rows (existing) |
| `get_state` / `prompt` / `bash` (polled) | `EngineStateObserved` / `PromptAccepted` / `BashResult` | status / Full / conversation (existing) |
| restored `bashExecution` | `ConversationMessage` **+ outcome markers** (below) | tool row (existing) |
| restored assistant `stopReason:error` | **`IsError: true`** + marker when the body is empty | error row, counts in map (**NEW**) |

## Corrections implemented (this slice, Core-only)

1. **Restored bash outcomes kept** (`PiConversation.FormatBashExecution`):
   observed `[cancelled]`, `[exit N]`, `[truncated]` markers. Success-shaped
   and field-absent rows render byte-identically to before.
2. **Restored failed/aborted turns kept** (`ParseAssistant`): Pi persists
   them with an empty body and the story in `errorMessage` (verified in Pi's
   own constructors). Empty body + error/abort stopReason now restores as
   `[Pi error: …]` / `[Pi aborted: …]` (redacted, capped); non-empty bodies
   are never rewritten. `stopReason:error` sets `IsError`, so a restored
   failure counts exactly like a live one; `aborted` stays non-error, exactly
   like live aborts.
3. **Live retry/compaction/rename rows** (tap decodes + bridge Detailed
   routes, failures as error rows). Failed compactions also count in run
   receipts (`CountRunError`), matching Pi's own error treatment.
4. `docs/observability.md` "Unavailable" bullet updated for the above.

No changes to the Pi contract surface, redaction policy, retention bounds,
pairing rules, or receipt semantics. No GUI, registry, lifecycle, or
Agent TWO files touched.

## What Agent TWO can bind

- New `AgentXEvent` subtypes (all carry `ObservedAtUtc`; renders above):
  `SessionRenamed.Name`, `AutoRetryStarted/Ended`,
  `CompactionStarted/Ended`. Same `FeedBridge` + visibility-level rules as
  existing detail rows — no new binding pattern.
- `FeedEntry.IsError` is now also true for restored failed assistants and
  failed retry/compaction rows; `BuildMap().ErrorCount` includes them.
- Restored bash rows may carry ` [exit N]`, ` [cancelled]`, ` [truncated]`
  suffixes; restored failed turns may read `[Pi error: …]`. These are
  Core-authored markers, never Pi prose — render them as arrived.

## Remaining limitations (genuinely unavailable, not dropped)

- **Thinking content**: persisted blocks arrive empty (encrypted signature
  only); no thinking deltas were observed on the RPC wire. Nothing to
  restore; unknown future shapes degrade to Full metadata, never silently.
- **Model/thinking-change history**: Pi records `model_change` /
  `thinking_level_change` entries in the session FILE but `get_messages`
  omits them (proven by replay: 44 file lines → 41 messages). The current
  selection is always available via `get_state`; its history is not.
- **Per-message timestamps**: persisted on all 41 replayed messages but the
  Core model carries none; ordering is preserved, timing is not.
- **Pre-first-turn durability**: Pi wrote no session file for bare connects,
  direct-bash, or renames (even after graceful exit); a bash-only session
  does not survive a process restart. Durability begins at the first
  assistant turn. `FindSessionFile` returning null means exactly this.
- **Aborted restores** surface a marker but no error flag (mirrors live).
- **`fullOutputPath` spill bytes** are referenced, never read, by Core.
- **Live inference runs** (real prompt → stream → settle with tools) were
  deliberately not spent here; loopback/protocol tests plus the probes above
  cover the machinery. One real metered run remains the honest end-to-end
  proof and belongs to a session where someone accepts the cost.

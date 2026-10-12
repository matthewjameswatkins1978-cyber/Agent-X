# Agent X architecture and decisions

## Product shape

Agent X is an AI-first, native and dependable agent environment. Its resident AI operates the environment through typed capabilities. The managed Garden owns toolchains and plugin admission/testing. Pi is the protected, initially unmodified agent kernel. Plugins integrate with Agent X contracts, not Pi internals. Human and AI clients invoke the same operations and receive the same verifiable results.

## Current vertical slice

`Avalonia UI → TranscriptImporter.Import(TranscriptImportRequest) → TranscriptDocument → virtualized line view`

The core owns the exact imported string, UTF-8 byte count, and compact line start/length indexes. It does not depend on Avalonia. The UI asks the core to import clipboard text, presents lines through a `ListBox`, and copies or exports the original full text rather than the rendered subset. Clipboard transfer awaits the platform, and parsing/indexing runs on a worker task before the UI binds the result. The ListBox uses a VirtualizingStackPanel over a lazy, read-only IList source, so line text is materialized on demand. Larger-size behavior still needs measurement.

## Decisions

1. **Prototype Avalonia 12.1.4 on .NET 10.** Avalonia is the requested framework, its latest stable release at bootstrap is 12.1.4, the package supports net10.0, and the framework is MIT-licensed. Keep the version pinned. Reassess startup cost, accessibility, text rendering, and distribution after measuring the native build.
2. **Keep domain data and operations outside the GUI.** `TranscriptImportRequest` is the first typed operation. A future agent transport should call this same application boundary rather than automate a separate UI-only workflow.
3. **Protect Pi behind an Agent X boundary.** Do not patch Pi or make plugins depend on its private internals. Kernel integration remains a later decision after Pi behavior and license/provenance are reviewed.
4. **Treat rendered rows as a view.** The immutable original text is authoritative; line entries are lazy slices and the UI must not become the data store.

## Page navigation (feature/agent-x-rc1-b1-navigation)

Top workspace tabs (scrollable overflow, keyboard arrows, connection dots)
plus Overview / Live / Settings pages replace the sidebar. Live is default
with the full-height feed and anchored composer; page switches are
presentation-only (scroll, selection, composer and feed survive). Tabs never
retarget a live Pi process (disconnect-first rule with revert); each tab
keeps an in-memory feed snapshot while disconnected. Overview shows only
observed facts (identity, connection, session file, counts, milestone
buttons that jump to Live+select). Settings holds only working controls
(auto-scroll, verbose map, theme, managed folders, engine/app info) backed
by persisted `AgentXPreferences`. Live keeps an inviting empty state with
composer-filling suggestions instead of a wizard. Verified live; details in
docs/rc1-b1-navigation.md.

## Native desktop shell (feature/agent-x-desktop-shell, superseded by tabs below)

The original sidebar prototype (collapsible navigation rail beside the
conversation) was replaced by workspace tabs with Overview/Live/Settings
pages. The virtualized feed, anchored composer, accessible row names and
packet tools survive in the new layout. Verified live with UIA-driven smoke
(see PR evidence). Details in docs/desktop-shell-verification.md.

## Pi-quality Live feed (feature/agent-x-rc1-b2-live)

Assistant narrative renders as unboxed Pi-grammar Markdown (headings,
bold, code, lists, links) in Pi dark hues; user packets sit in blue
blocks; each tool operation is one coherent grey/green/red block with
diff-tinted monospace output and an explicit full-output expander; bridge
detail rows flow at Compact/Detailed/Full (Detailed default) via a
re-attaching FeedBridge; tool bodies reconcile to persisted history
post-run. Copy stays byte-exact from source text. No usage/cost figures
(no host poll site exists). Details in docs/live-visual-grammar.md.

## Workspace sessions and recovery (feature/agent-x-workspace-recovery)

Project-aware session identity replaces the single global session: one
project root maps to one `ax-<tail>-<hash>` Pi session id, and Pi runs IN
that project root (user's OS authority — isolation between projects, not a
sandbox), with the legacy `agent-x-main` / `agent-cwd` session preserved
untouched and selectable. A JSON registry tracks roots idempotently,
validates every entry fail-closed, refuses slug sharing loudly, and never
modifies a corrupt file (GUI enters recovery mode instead). `PiConversation` builds clients from a factory so
`ReconnectAsync` retires the previous writer before connecting its
replacement (single-writer rule) on the same
persisted session; `ConnectionLost` plus a GUI Reconnect button (with
run-in-flight and wrong-workspace guards) complete the loop. Details and
live evidence in `docs/workspace-sessions.md`.

## Conversation feed and session map (feature/agent-x-conversation-ux)

The Pi panel renders a structured `ConversationFeed` (Core, UI-free) instead of
one rebuild-per-character text box: restored history plus the live turn, tool
activity, errors and per-run receipts, each row selectable with byte-exact
copy. Streamed deltas buffer in `StreamingTextAccumulator` and reach the
screen on a 10 Hz flush; completion reconciles against the authoritative
returned text. `SessionMapSnapshot` derives current activity, milestones,
changed files (edit/write calls only, unverified on disk), heuristic test
commands and error counts — decisions are never inferred. Design details in
`docs/session-map-design.md`. The transcript import path is untouched.

## Pi persistent sessions (feature/pi-persistent-sessions)

The GUI now connects to a Pi-native persistent session (`agent-x-main`) in an
Agent X-managed directory (`%LocalAppData%/AgentX/sessions`, stable `agent-cwd`
working directory so Pi's project-session lookup is deterministic). `--session-id`
opens the exact session or creates it; `--no-session` remains available via
`PiRpcOptions.Default()` for ephemeral use. `PiConversation` (Core, UI-free)
restores history with `get_messages` on connect, streams prompts, keeps tool/error
activity on a compact status line, and issues a `PiRunReceipt` per run so idle
(no automatic work left) is never confused with healthy (settled, not aborted,
no observed errors) — and healthy is never presented as passing acceptance
checks. Large messages are preserved byte-for-byte in the session;
only display truncates, with an explicit notice.

## Pi RPC integration (feature/pi-rpc-integration)

First live connection to the Pi 1.1.0 engine over its documented JSONL RPC protocol.
`AgentX.Core` owns a small client (`PiRpcClient`), a framing/correlation layer
(`PiRpcConnection`, `PiRpcFraming`) and typed models (`PiEngineState`). Pi runs as a
separate process launched with `--mode rpc --no-session --no-extensions --no-skills`;
provider authentication flows from the existing environment — Agent X never copies
credentials and never modifies Pi's installation. Prompt acceptance (`started`/`queued`/
`handled`) is never treated as completion; completion is only `agent_settled`. The
Avalonia shell adds a minimal conversation panel (prompt box, streamed output,
connection state) and stays independent of the agent process: all I/O is async and
cancellation (client CTS + `abort`) never blocks the UI.

## Explicitly deferred

Garden lifecycle and isolation, resident AI/provider routing, plugin manifests and test admission, durable transcript persistence, streaming log append, cancellation/backpressure, operation authorization, and the client protocol. Do not infer their details from this demo.

## Next milestone

Run the large-packet UI cases from README, including a 25 MiB paste and a 1,000,000-line document. Capture timings and working set, verify full-text export equality, and tune the virtualized view only if measurements show a problem. Then define the smallest streamed-log operation and its typed result before building Garden or plugin infrastructure.
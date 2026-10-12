# Extension Audit Verdicts (Pi 1.1.0, 2026-10-12)

> **Acceptance status (factual).** The verdicts below are the auditor's
> measured findings. Source acceptance is now recorded: Studio PR #2 merged
> as `9fd3f70` and QA PR #1 merged as `b5ccd8e`; reported evidence preserved
> (Studio deterministic suite 114/114, QA 209 tests / 208 pass / 1 skipped).
> A source merge is not runtime proof: live installation behavior and the
> interactive GUI click-through remain separately outstanding and are not
> claimed here. This audit (PR #5) itself remains pending Lucy's merge decision.

Mission: audit and harden the three remaining default-load extensions.
Standard applied: no extension keeps default-load status merely for being installed.
All fixes are on isolated branches with PRs; Lucy merges. No Garden work started.

## Runtime truth (measured, neutral temp cwd, --no-session, no prompts)

Time to first RPC response (2 runs where shown) and `get_commands` inventory:

| Config | Run 1 | Run 2 | Commands |
|---|---|---|---|
| bare (`--no-extensions --no-skills`) | 3.6s | — | 10 (user prompt templates only) |
| full ordinary load | 5.2s | 4.9s | 71 (10 prompt + 11 extension + 50 skill) |
| adapter only | 3.0s | — | 18 (mcp-adapter, mcp-auth, mcp, 4× basic-memory + 1 skill) |
| qa only | 4.5s | 4.7s | 12 (`qa`, `keeper`) |
| studio only | 3.5s | 2.9s | 11 (`studio`) |

Memory: system-wide peak RSS read 415 MB in every config — method too crude for
per-extension attribution; reported as inconclusive, not as evidence.
Session evidence (204 files, read-only): `studio_checkpoint` 180 calls,
`studio_status/shp/complete/blocked/context` 35–51 each, `keeper_context`/
`keeper_why` 39 each, `mcp__basic_memory` 50, `mcp__lantern` 37, `/qa verify`
in 5 sessions. Nothing registered is dormant; nothing is duplicated
(namespaces `qa/keeper`, `studio/studio_*`, `mcp__*` disjoint; zero
cross-references between QA and Studio state in either direction).
Installed pi-mcp-adapter is 3.2.0 vs npm canonical 5.2.0 (breaking changes
between: lazy-startup default, JSON-string args); upgrade assessed, not done.

## Phase 2 — Matthew Way Studio: HARDENED (PR #2)

Reproduced ordinary-packet activation live in a disposable repo + local bare
remote with real Pi. Found and fixed: unprotected push path (now refuses
main/master/foundation/release*), bare `git add -A` (removed; explicit
pathspec commits + staged-list ledger record + `paths` param), acceptance
items never bound to evidence (now `accepts:[n]` tags plus human-gated items forced to PARTIAL (in-process confirmations are never recorded — no verified human-turn boundary exists)), stale `pushed_sha` + `verify_push=false`
passing COMPLETE (now bound to final revision), dirty worktree completing (now
blocked), silent below-threshold inaction (now explicit idle section), jobless
ship looking tracked (now labeled). Follow-up round: pathspec-restricted
commits (pre-staged unrelated files stay staged), protected destinations
including `bootstrap/*` plus configured targets with pre-write refusal and no
HEAD-to-other-ref retargeting, caller revisions bound to the live checkout,
and agent dogfood citations stripped of covering power. Live fire repeated
through the hardened gates: COMPLETE with ledger, receipt and remote agreeing
(76f8c2f) and the unrelated config preserved. Deterministic suite 114/114. Final-acceptance round (986ad57): exact-file scope gate with :(literal) pathspecs plus a committed-containment check; human_confirmed removed as evidence (fail-closed PARTIAL); hostile scopes refused pre-write with HEAD and worktree preserved; live Pi probe refused a wildcard and committed the exact file alone.
**Verdict: KEEP default-load** — earned by measured daily use plus the
hardened, tested gates. Preserved the in-flight Threadmoth boundary rule.
## Phase 3 — PiToRuleThemAll: HARDENED (PR #1 comment)

Verified the core claims hold: frozen-id uniqueness enforced on parse,
fabricated PASS (missing/duplicate/unknown ids) yields INCONCLUSIVE+SUSPECT,
verifier spawns genuinely isolated (fresh pi, no extensions/mcp/tools/skills/
session, narrative withheld until after), spawn failure/timeout/unparsable
output fail safe, builder claims reconcile against observed evidence only,
receipts validate on save and load. Fixed: `/qa receipt` now reports
FRESH/STALE/UNKNOWN vs live git (stale PASS can't masquerade as current);
LIVE sg smoke follows the repo live-test convention instead of hard-failing.
Follow-up round (c8dbf6c): `/qa receipt --json` emits a `{receipt, freshness}` envelope (exact-output JSON.parse test), and unreachable previously-verified remotes report UNKNOWN instead of implying FRESH. Deterministic suite 209 tests / 208 pass / 1 honestly skipped. Two live
`/qa verify` cycles with real isolated verifier spawns completed fail-safe
(INCONCLUSIVE on toy specs, integrity OK; lantern READY, tethers READY,
reusery/telltail correctly NOT_NEEDED). Production history shows real
inspector-PASS outcomes from deeper reviews.
**Verdict: KEEP default-load** — ~1s startup, two tools, always discoverable
mid-session by the model (on-demand loading would hide it exactly when
autonomous verification needs it). Natural invocation stays `/qa`, `/keeper`;
packet-style QA invocation assessed as future work, not built.
## Phase 4 — MCP adapter: KEEP, do not migrate

Head-to-head on the same Lantern server (bridge verified healthy standalone:
initialize + tools/list return 8 tools; service 127.0.0.1:4317 live):
- Adapter: tools surfaced immediately, lazy-connected on first use
  ("not listening" → "1 connected"), `lantern_status` returned service
  ready (Lighting 0.1.0, SurrealDB 3.3.0) in a 3-call turn. Cached metadata,
  60s failure backoff, OS-keyring secrets, consent/approval scaffolding.
  Ignores native `mcp.json` by design (no double-start).
- Native Pi 1.1.0 MCP: project `mcp.json` needs interactive trust; tools
  never surfaced to the model (it burned 8 powershell calls working around);
  `mcp connect lantern` hung the full 180s; untrusted files get honest
  static analysis only. No migration target today.
- Secrets: both use OS keyring; adapter additionally supports bearer
  commands/files — wider surface, unused by the current minimal config.
  Cancellation/timeout: adapter has backoff + recovery paths; native hang
  above is the counter-evidence.
**Verdict: KEEP** the working adapter and both server configs untouched.
Re-evaluate native when headless trust/connect works. Assess (don't execute)
the 3.2.0 → 5.2.0 upgrade separately — breaking changes require migration care.

## Remaining limitations

- Interactive GUI click-through of a Studio mission still needs a manual pass.
- Per-extension memory attribution inconclusive (method limitation).
- Agent-supplied dogfood citations never cover acceptance; in-process human
  confirmations are refused because no verified human-turn boundary exists.
  Human-only acceptance remains PARTIAL or NEEDS_HUMAN.
- Real-model PASS on a substantive QA spec not yet demonstrated live.
- sg/ast-grep binary absent, so the live structural-search smoke stays skipped.
- `mcp connect` hang and native headless trust are Pi-core issues, out of scope.

## Adversarial-testing candidate: Terrorbats (not yet run)

Terrorbats (`The-Terror-Bats` repo, known packs `packs/first-flight.yaml` and
`packs/species.yaml`, separate Antigravity review ongoing) is the candidate
for subsequent acceptance work — e.g. a species Bat falsifying "ship stages
only selected paths" with process isolation and content-addressed evidence.
Its local installation and packs have NOT been dynamically verified in this
audit (deterministic node:test hostile cases carry the current evidence), and
nothing here interferes with Antigravity's review.

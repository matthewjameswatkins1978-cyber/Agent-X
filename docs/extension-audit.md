# Extension Audit Verdicts (Pi 1.1.0, 2026-10-12)

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
items never bound to evidence (now `accepts:[n]` + `#n` dogfood citations),
stale `pushed_sha` + `verify_push=false` passing COMPLETE (now bound to final
revision), dirty worktree completing (now blocked), silent below-threshold
inaction (now explicit idle section), jobless ship looking tracked (now
labeled). Live fire proved end to end: mission seeded, probe2.txt created,
feature branch pushed and remote-verified, tagged checks, COMPLETE with
ledger/receipt/remote all agreeing (44f178a) and main untouched; the
unrelated harness config stayed uncommitted. Deterministic suite 104/104.
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
Deterministic suite 208 tests / 207 pass / 1 honestly skipped. Two live
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
- Out-of-range `#n` dogfood citations are ignored as external refs by design.
- Real-model PASS on a substantive QA spec not yet demonstrated live.
- sg/ast-grep binary absent, so the live structural-search smoke stays skipped.
- `mcp connect` hang and native headless trust are Pi-core issues, out of scope.

# Pi 1.1.0 Clean Baseline (2026-10-12)

Environment objective: known-good Pi 1.1.0 before Agent X-managed capabilities.
Pi core, authentication, sessions, AGENTS.md, shared skills, Threadmoth 1.13.0,
Codex MCP processes and unrelated software were preserved. No Garden work started.

## Inventory (before cleanup)

Registered extension packages (`~/.pi/agent/settings.json` → `packages`):

| Source | Author | Verdict |
|---|---|---|
| `npm:pi-free@2.8.4` (global npm) | apmantza (third-party) | REMOVED — free-model providers; core already serves all enabled models |
| `npm:pi-mcp-adapter@3.2.0` | third-party | KEPT — serves active `mcp-adapter.json` (basic-memory, lantern); shared `@modelcontextprotocol` deps |
| `npm:@tmustier/pi-raw-paste@0.1.3` | tmustier (third-party) | REMOVED — `/paste` helper, legacy |
| `npm:@tmustier/pi-code-actions@0.1.6` | tmustier (third-party) | REMOVED — legacy |
| `D:\Projects\PiToRuleThemAll` | first-party personal | KEPT — Matthew's own layer, not third-party; under review for Garden phase |
| `D:\Projects\matthew-way-studio` | first-party (Studio) | KEPT — Pi Studio addition; `extensions/matthew-way-studio` holds only disabled/bak files (no double load) |
| `D:\Projects\rabbit-pi-compactor-v2` | MIT, pins Pi SDK 1.0.0 | REMOVED — obsolete pin plus a startup warning (host package in `dependencies`, duplicate-module risk) |
| `…\pi-extensions-capability-repair\packages\pi-codex-compact` | narumitw (third-party) | REMOVED — Codex MCP OS processes are separate and untouched |

Other paths: `extensions/` (studio remnants only), `skills/` (10 `bailian-*`, `matthew-way`,
`threadmoth` — all preserved, none deregistered), `prompts/` (10 files + 1 disabled),
`scripts/` (`read-opencode-key.ps1`), `sessions/` (43 MB, untouched),
`~/.pi/agent/npm` (managed tree now rooted at `pi-mcp-adapter` only).

Running processes left alone: `codex` ×3, `codex-code-mode-host`,
`codex-computer-use-swift`, `codex-windows-sandbox-service`.

## Changes

1. Backup: `C:\Users\Matmus\.pi\agent-backup-20261012-clean-baseline`
   (settings, auth, models, mcp-adapter, AGENTS.md, APPEND_SYSTEM.md,
   studio-models, prompts, skills, scripts, extensions).
2. `pi remove` ×5 (registrations only, native path).
3. `npm uninstall -g pi-free` (86 packages).
4. Deleted orphaned `~/.pi/agent/npm/node_modules/@tmustier` (no shims existed).
5. Project directories on disk were NOT deleted; sessions were NOT copied (size).

Could NOT safely be removed (kept, reasons above): `pi-mcp-adapter` + its shared
dependency tree (incl. `@modelcontextprotocol`, `@narumitw` orphans),
`PiToRuleThemAll`, `matthew-way-studio`, all skills.
No `mcp.json` servers exist (`pi mcp list` empty); MCP flows via the kept adapter.

## Measurements

- `pi -p "Reply with exactly: BEFORE OK"` (8 extensions): 22.5s, exit 0,
  with a rabbit-pi-compactor peerDependencies warning on stderr.
- `pi -p "Reply with exactly: AFTER OK"` (3 extensions): 10.9s, exit 0,
  byte-exact output, zero warnings.
- `pi --list-models meta`: all 5 Muse Spark models present (core providers only).
- `threadmoth --version`: 1.13.0. Default tools unchanged
  (read, powershell, edit, write, grep, find, ls); default model
  `meta/muse-spark-1.3-contributor`.
- Agent X RPC (`PiRpcClient`, ephemeral): connected in 3.7s, prompt returned
  byte-exact `CLEAN OK`, `aborted=False`, 7.4s total.
- Agent X suite on merged HEAD: 28/28 pass.

## Recovery procedure

1. Restore registrations: copy `settings.json` back from the backup dir
   (or re-add one entry with `pi install <source>`).
2. Restore files: `npm install -g pi-free@2.8.4`; Pi reinstalls `npm:` packages
   on next launch from the restored `packages` list.
3. Verify: `pi list`, `pi --list-models meta`, one `pi -p` smoke prompt.
4. Sessions, auth and skills were never touched; no recovery needed for them.

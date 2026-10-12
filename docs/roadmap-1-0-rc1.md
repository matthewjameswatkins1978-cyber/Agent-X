# Agent X: Road to 1.0 RC1

**Status:** agreed planning baseline, not evidence that features have shipped.  
**Prepared:** 12 October 2026.  
**Target release:** `v1.0.0-rc.1` (GitHub prerelease).  
**First supported platform:** Windows 11 x64.  
**Build:** Avalonia 12.1.4 / C# / .NET 10, unmodified Pi 1.1.0 over JSONL RPC.  
**Canonical branch today:** `bootstrap/agent-x-foundation`. No branch rename is a prerequisite.  
**Release principle:** *Simple in the front. Clever in the back.*  
**Live rule:** *Don't narrate the interface. Let the interface communicate.*

This is a **feature-complete, installable and genuinely usable Windows release candidate**, not a promise of every long-term Agent X capability. A release candidate invites focused testing of a controlled feature set. Correctness, session preservation, runtime honesty and a cohesive Pi-quality interaction matter more than quantity of features. Milestones below are **dependency gates, not invented calendar deadlines**.

## 1. RC1 product definition and hard scope

A person can install/open Agent X, select or create a project, get a working Pi session, send exact multiline instructions, watch readable and genuinely detailed tool work, switch between project workspaces without confusing their sessions, inspect evidence and progress, recover after process failure, and resume later. The UI does **not** require knowledge of RPC internals or a swarm of sidebars.

### Required experience
- **Workspace tabs (top row):** each tab is a project identity, visibly distinguishable, overflow searchable, with guarded switching. Changing a tab never silently retargets a live Pi process. One user-authorized workspace at a time is acceptable for RC1; don't claim parallel running agents.
- **Three fixed workspace pages (second row):** **Overview / Live / Settings**, not a permanent 60/40 split. Navigation does not stop a run or lose session/scroll/selection. Opening an existing workspace defaults to Live; a first-use or empty-session state offers a clear first prompt or Open Project, not an obligatory recurring splash screen.
- **Live:** nearly all available area is Pi-inspired chronological conversation. Blue user/large-packet blocks; unboxed assistant Markdown; grey pending, green tool-completed and red failed-operation blocks; purple only for genuine special events; measured status in a quiet shared location. Use Pi CLI v1.1.0 as the behavioural **and typography** reference: coloured blocks, bold headings, magenta technical references, muted tool output, compact spacing, real commands and results. No repeated "Agent says", timestamps, "Tool finished" or waiting spam. Don't pretend green proves task correctness. Full-width technical content, exact original-user-text copy, anchored multiline composer, predictable keyboard control and stable scroll under streaming.
- **Visibility:** Compact / **Detailed (default)** / Full trace, backed by **observed, redacted** events. Detailed means the actual useful work in-line by default: tool calls, safe arguments, output, code/diffs and exits where observed, failures and retries. Large outputs fold with explicit hidden/truncated counts and safe inspection, not silent replacement by summaries. Full trace adds available IDs/metadata, never private model chain-of-thought or unredacted secrets.
- **Overview:** useful factual active-workspace state, ongoing/last operations, attention, changed-file **attempts vs on-disk verification**, tests **observed vs verified**, recent milestones and inspectable evidence links. No invented cards, fake PASS, compulsory dashboard or large empty panels.
- **Settings:** small, functional preferences: visibility level, typography/theme/accessibility, workspace information and verified Pi/provider/model configuration or clear external setup instructions. Do **not** create pretend switches for unsupported Pi capabilities.
- **Lifecycle:** distinct project-aware persisted Pi-native sessions; legacy session preserved; meaningful disconnect/reconnect, single live session writer, no auto-create of disappeared project folders, fail-closed registry recovery, reliable cancel/abort, honest partial output and terminal errors. Working in a project uses **the user's OS authority** and is **not a sandbox**.
- **Distribution:** a reproducible, integrity-checked Windows x64 prerelease with a clean-machine onboarding/diagnostic path; simple rollback and no secret migration or destructive upgrade.

### Explicitly outside RC1 (unless a release-blocking need is demonstrated)
An entire IDE/code editor, Garden/plugin marketplace, custom toolchain manager, autonomous multi-agent orchestration, Tethers/Permission Slip integrations, local LLM distribution, actual source+conversation rewind, elaborate visual dashboards, cloud sync, collaboration, macOS/Linux/ARM packages, a paid model/provider layer, native AOT, and automatic updater. Reuse Pi's built-in read/bash/edit/write and existing proven Core instead. Don't turn a release gate into an excuse to build a second platform.

## 2. What is genuinely accepted today (snapshot, 12 October 2026)

- The accepted foundation is `1e9f391ff4d4e141ea95a252962f5819b3244ccd`. PRs #1-#6 covered RPC, native sessions, docs, virtualized conversation/Session Map, extension audit, and workspace recovery.
- PR #7, `feature/agentx-observability`, at `df6bf1e`: **OPEN**. Agent ONE reported 51/51 tests and real-Pi redaction probes, but Lucy's review comment `6119731266` still blocks metadata redaction and silent preview truncation; no merge and no GUI wiring claim.
- PR #8, `feature/agent-x-desktop-shell`, at `ba72e39`: **OPEN**. Agent TWO reported 87/87 tests and real UIA Windows kill/reconnect/screenshot evidence, but Lucy's review comment `6119932325` still blocks 720px responsiveness/splitter, exact multiline composer, and end-to-end folder picker confirmation; no merge.
- The foundation README currently describes an older transcript-only stage and wrongly says Pi integration is unimplemented. Refresh documentation as code is accepted, not before.
- As of this snapshot the repository has **no `.github/workflows` directory**, **no published GitHub releases**, and no verified distribution package. These are release work, not existing capability.
- Reported suites and live-smoke evidence are **agent reports** until Lucy or an independent gate actually reproduces them. Distinguish code review, automated tests, real GUI tests and user acceptance.

## 3. The dependency path (finish each gate before the next major scope)

### Gate A: close the active foundation PRs (no new competing packets)

**A1, Agent ONE:** finish PR #7 review: redact ALL captured strings including metadata/nested engine state before publication; make every capture limit vs preview fold explicit; bound transient allocations; re-run synthetic canaries, true correlated RPC boundary and real-Pi tests; update documentation. Lucy independently reviews and merges only once blockers close.

**A2, Agent TWO:** finish PR #8 review: restore functioning 720px minimum/resizable collapse; preserve exact whitespace in a bounded multiline composer and test real submission; native Add-confirm/Cancel must not silently register a default Documents folder; investigate modal focus/minimizing; run real Windows GUI checks. Lucy independently reviews and merges only once blockers close.

**A exit evidence:** exact merged commits, current `dotnet test AgentX.sln` result on new foundation, baseline cold desktop start/connect, real successful/failed tool operation screenshots, explicit known gaps. No accidental cross-file conflict with concurrent agent branches. No new mission until the agent's preceding result is finished and reviewed.

### Gate B: finish the product navigation and Pi-quality Live presentation

**B1, workspace/page navigation:** Implement top project tabs with overflow and page tabs Overview/Live/Settings using existing Core identity. Persist last workspace/session/page choice sensibly; default Live on opening, welcome/first prompt only when empty. Switching page is presentation only; swapping workspace must follow existing controlled connection lifecycle. Maintain navigation via keyboard and at 720px narrow width.

**B2, Live visual grammar:** Reference real Pi 1.1.0 rendering code and equivalent captures, **not a generic chat UI template**. Style a single tool operation as one coloured updating block; typography/Markdown headings, bold, code, path emphasis and failure formatting match Pi's information hierarchy. Distinguish pasted packets visually. Keep no unnecessary message chrome, fixed tool-status banners or timestamp columns. Preserve stream order and entire safe available evidence. Search/selection/copy/expanded output improve on terminal limitations.

**B3, Overview and Settings:** Ship intentionally modest useful screens with supported operations. Overview must link or navigate to the underlying real event/receipt. Settings must actually change and persist the supported preference; unsupported controls are absent or labelled unavailable.

**B exit evidence:** real side-by-side Pi CLI and Agent X screenshots for matching user packet, bash search, successful edit, failed edit and finished report; clicks/keyboard tests, 720/1080/1440/4K responsive captures; no page switching losses; zero fabricated success indicators. Review with Matthew before declaring UX frozen.

### Gate C: integrate observability and make reliability boring

- **Single source of observed facts:** connect accepted typed tap to feed without a second competing log; correlate tool IDs and retain **bounded redacted** output. Explicit host recording for correlated poll responses, with accurate "not captured" when absent. No bare, unredacted secret retention in Full trace or exports. Preserve Pi native user/session data separately from diagnostic capture; don't confuse exact-copy of user-authored text with safe, redacted diagnostic export.
- **Real lifecycle:** slow network/provider failure, cancelled prompt, real Pi kill mid-stream, reconnect failure/retry, stale events after switching, duplicate process guard, unknown shell exit, truncated log, corrupt registry, disappeared project, shutdown/relaunch. No false task receipt or wrong-project history.
- **Fidelity:** legitimate tool output and assistant text not duplicated by delta/end reconciliation, explicit preview folding/expansion, exact multiline prompt on disk/in Pi session, correct Markdown and filenames, permission/authority notices where needed.
- **Resource bounds:** at least 100k-line transcript and a 25 MiB import with byte-exact copy/export, sustained tool burst with measured CPU/RAM/retained event caps, no frozen GUI during streaming, safe backpressure. Run 1M-line stress as a recorded scalability test; if it fails, make a deliberate documented RC1 scope decision rather than hiding the failure. Baseline app launch, first connection, response latency and UI responsiveness; choose explicit regression budgets from the measured baseline before feature freeze, not guessed performance claims.
- **Security/accessibility:** redaction canaries across all event fields and exports; managed directory protections and no silent project writes on malformed registry; keyboard-only core journey, focus indicators, accessible names/roles and colour-independent error cues; Windows Narrator/UI Automation/high-contrast and text scaling smoke.

**C exit evidence:** adversarial matrix with PASS/FAIL/NOT OBSERVED, deterministic tests on a clean checkout, real Windows smoke signed off by evidence, no P0/P1 defect remaining. If a suspected unauthorized project registration or history cross-over recurs, **stop RC1**.

### Gate D: produce a real Windows build and onboarding path

- Create a minimal, reproducible **win-x64 self-contained .NET Release publish** and a zipped portable prerelease first. Verify the published executable works on a **clean Windows 11 x64 user profile without a development SDK**. Packaging decisions use actual startup/memory/size data, not fashion.
- Detect/diagnose the **external Pi 1.1.0** launcher, its RPC handshake, compatible Node version **>=22.19.0** when the npm Pi installation is used, authenticated provider/model availability, writable user-managed session location and permissions. On failure give concise actionable instructions, not a dead or fake connected screen. Do not copy API keys into Agent X or silently change the existing Pi installation.
- Pi is **MIT** under its v1.1.0 upstream package; any eventual bundled Pi or Node requires dependency-license/provenance review and intact notices. RC1 may require a separately installed Pi rather than bundling it; label the dependency clearly. Preserve user files and existing sessions on install, upgrade, uninstall and rollback. A clean-room profile check is mandatory.
- Generate a sha256 manifest, version/about dialog, source commit ID, third-party notices, install/readme/troubleshooting and known limitations. Publish a zip first; Windows installer and code signing are optional **only after** users can use the portable build and packaging is worth it. Avoid automatic updates for RC1.
- Add proportionate CI: fast restore/build/test and static hygiene on relevant PRs, cancel obsolete PR runs; preserve foundation/release checks; run slow Windows GUI/integration/packaging gates deliberately or on release candidate, recording runner and outcome. Don't exhaust Actions minutes with long every-push workloads.

**D exit evidence:** hash-verified artifact, reproducible publish command/environment, clean-machine screen recording/smoke, Pi preflight success/failure, no dependency surprise, rollback restoring prior usable version, license and provenance record.

### Gate E: freeze, candidate, and release

1. Declare **feature freeze** after B/C/D gates and reconcile every outstanding PR; release branch only if needed for bounded fixes. No late UX architecture rewrite.
2. Run the RC acceptance matrix below from a clean tree, on the **exact candidate commit**. All release-blocking rows have traceable proof. Mark unknowns as NOT OBSERVED, not PASS.
3. Triage: **P0/P1 = block** (wrong project, data corruption/leak, exposed credentials, false command/test pass, duplicate session writers, crashed/unusable first-run, corrupt upgrade, key flows broken); P2 = decide and publish known limitation; cosmetic is not a blocker if useful layout and accessibility remain intact.
4. Matthew personally drives one honest end-to-end session (open project, paste a substantial packet, run/read/edit, inspect coloured output and Overview, switch tabs/page, cancel, restart and restore) and judges whether it **feels like Pi with more room**, not a generic chat GUI. Final acceptance cannot be replaced by agent optimism.
5. Tag **`v1.0.0-rc.1`** on the exact accepted commit; create a **GitHub prerelease**, portable Windows artifact + SHA256 + checks/tests + changelog + limitations + setup/troubleshooting. Test the uploaded asset, not just a local build. Lucy decides tag/merge only with verified gates; never invent a release.
6. Collect RC feedback and issue `rc.2` for serious fixes. Call `1.0.0` stable only after RC usage validates the key journeys and release blockers are gone.

## 4. Mandatory RC1 evidence matrix

| Gate | Real proof required |
|---|---|
| Fresh start | Clean Windows 11 x64 profile, install/extract, Pi preflight, first prompt, clear missing-dependency/auth errors |
| Workspace identity | Add+confirm, Cancel=no registration, safe overflow/switching, no wrong project/session, real cwd clearly disclosed |
| Pi lifecycle | Genuine tool execution, abort, process kill and reconnect, one process per session, preserved history/partial work, honest failures |
| Conversation | Full-height Live, 720px/1080p/1440p/4K, long packet exact submission/copy, stable scroll under streaming |
| Pi visual parity | Side-by-side source-derived colours, operation blocks, Markdown/report typography, errors and no repeated narration |
| Visibility | Compact/Detailed/Full work without losing underlying evidence; bounded expansion and explicit truncation; correct exit status |
| Overview & Settings | Real evidence links and functional persisted options, no invented tests, context or controls |
| Privacy & access | Synthetic secret sweep on stored/subscriber/exported events; keyboard-only, UIA/Narrator, high-contrast/text scaling |
| Performance | Named baseline host, measured cold UI/start/connect, 100k + 25 MiB exact-copy, burst memory, 1M stress disposition |
| Distribution | Reproducible published zip, SHA256, clean run, safe rollback/uninstall, licenses, known-issues notes |

**Evidence format per gate:** exact candidate SHA, machine/OS, commands/UI steps, automated vs manually observed, test counts, screenshots/receipts and PASS/FAIL/NOT OBSERVED. An agent's written completion summary is never the only proof.

## 5. Roles, sequencing, and stop rules

**Lucy:** product acceptance, research, independent PR reviews and exact-SHA guarded merges, cross-PR integration, release gate judgement. **Agent ONE:** Core/protocol/observability and high-value adversarial verification. **Agent TWO:** Avalonia navigation/Live/GUI journey and Windows smoke. Use one active bounded packet **per agent**: do not write a replacement/next packet while an agent's previous assignment is underway; wait for the finished return and review it. Integration tasks can be assigned only after both changes are accepted. Reuse Threadmoth for guarded edits and existing test harnesses where proportional.

**No merge** with unresolved P0/P1, unverified scope/target, fabricated tests, leaking diagnostics or destructive data changes. Always distinguish *implemented*, *agent-reported tests*, *Lucy independently reviewed*, *GUI observed* and *RC tested*. No forced branch rewrites or releases as a side effect of working on this roadmap.

## 6. Open decisions, deliberately deferred until evidence

1. How Pi is obtained on a clean computer: external prerequisite first vs licensed bundled copy if packaging/lifecycle evidence warrants it.
2. Installer/MSIX/signing only if the portable release creates a real usability obstacle; unsigned prereleases must say so.
3. Final measured startup/RAM/stream responsiveness budgets after a baseline, before feature freeze.
4. Supported keyboard send/newline shortcuts, once tried with long packets in real GUI; make them visible on demand, not repeated instructions.
5. Model/provider controls: expose only what Pi's pinned RPC version demonstrably supports; external authentication can stay in Pi.
6. Whether simultaneous background workspaces or native tab tear-off add demonstrable value **after RC1**. Don't claim concurrency from tabs.

## 7. Reference evidence, not requirements to rewrite

- [Pi 1.1.0 RPC contract](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/rpc.md) and [Pi package/Node requirement](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/package.json).
- [Pi v1.1.0 interactive rendering, themes and tool components](https://github.com/earendil-works/pi/tree/v1.1.0/packages/coding-agent/src/modes/interactive).
- [Microsoft .NET publish modes](https://learn.microsoft.com/en-us/dotnet/core/deploying/) and [single-file deployment trade-offs](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).
- [Avalonia Native AOT constraints](https://docs.avaloniaui.net/docs/deployment/native-aot): optional optimization, not a shipping requirement.
- [Windows accessible-app guidance](https://learn.microsoft.com/en-us/windows/apps/develop/accessibility): keyboard, UI Automation, Narrator, contrast/text scaling.
- [Current Agent X architecture](architecture.md), [workspace recovery](workspace-sessions.md), and [source/extension admission audit](extension-audit.md).
- Basic Memory Cloud **Agent X** project notes: *Live Feed Visual Grammar and Screen Economy*; *Agent Packet, Review and Merge Discipline*.

**Rule of thumb:** RC1 passes when a user can safely complete a real programming task, inspect what actually happened in a spacious Pi-quality Live view, leave, come back, and trust that their project, work and evidence are still correct.

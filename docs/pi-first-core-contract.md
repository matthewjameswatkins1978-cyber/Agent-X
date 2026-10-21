# Pi-First Core contract (Agent ONE → Agent TWO)

Small, typed, Pi-native. Agent X invents no agent runtime and no config
system: Pi stays the kernel, the project folder stays the authority, and
every contract below is UI-free Core the GUI binds without guessing.

## Capability audit (2026-10-16, Pi 1.3.0, `feature/pi-first-core`)

| Mission item | Verdict | Evidence |
|---|---|---|
| Open existing folder, discover Git state, use as Pi cwd | **NEW in Core** (shell already parsed branch in GUI only) | `ProjectGit.ReadState` + `AgentXWorkspace.WorkingDirectory` (= project root); 8 new tests |
| Fresh local folder, generated name, `git init`, no remote/commit | **NEW in Core** | `NewProject.Create`; tests assert `.git` present, no `refs/remotes`, unborn HEAD |
| Connect + restore correct Pi session | Works | `PiConversation.ConnectAsync` → `get_messages` restore; `ValidateSessionMatch` refuses foreign sessions; 374 baseline tests pass |
| Read/change Pi model + thinking level | Works on Pi 1.3.0 | Live RPC probe: `get_state`, `get_available_models`, `get_available_thinking_levels` (`minimal,low,medium,high,xhigh`), `set_thinking_level:medium` all `success:true`; `PiRpcCompat.ProbeAsync` is the authority, never the version number |
| Long packets, streaming events, Stop/Reconnect truthfully | Works | `SendPromptDocumentAsync`/`PromptAndWaitAsync` (acceptance ≠ completion; `handled` needs no settle wait), `message_update/text_delta` → `agent_settled(aborted)`, `AbortAsync`, single-writer `ReconnectAsync`; byte-level JSONL reader has no line-length limit |

Baseline: `dotnet test tests/AgentX.Core.Tests` → **374/374 pass** (pre-change).
Live Pi 1.3.0 ephemeral probe → all six baseline commands `success:true`.

## Core contracts for GUI work

```csharp
// 1. Open existing project (+ Git state for the header/footer)
AgentXWorkspace ws = AgentXWorkspace.ForProject(managedRoot, pickedFolder);
ProjectGitState git = ProjectGit.ReadState(pickedFolder);
// git: { ProjectRoot, IsGitRepo, TopLevel, Branch?, HeadSha?, IsDirty, StatusAvailable } + ProjectGit.DescribeBranch (single branch label: named, unborn, detached)
// Pi runs IN ws.WorkingDirectory (= ProjectRoot). Nothing is written into it.

// 2. Fresh local project (no remote, no commit)
string root = NewProject.Create(parentDir, nameHintOrNull);
AgentXWorkspace fresh = AgentXWorkspace.ForProject(managedRoot, root);

// 3. Connect + restore (same for open/fresh/legacy)
var conv = new PiConversation(() => new PiRpcClient(ws.ToPiRpcOptions()));
PiEngineState state = await conv.ConnectAsync(ct);
ws.ValidateSessionMatch(state.SessionId); // throws on foreign history
IReadOnlyList<ConversationMessage> history = conv.History;

// 4. Model + thinking (query per connection; accepted ≠ effective)
IReadOnlyList<PiModelOption> models = await conv.GetAvailableModelsAsync(ct);
await conv.SetModelAsync(provider, modelId, ct);
PiEngineState proof = await conv.GetStateAsync(ct); // compare to confirm
IReadOnlyList<string> levels = await conv.GetAvailableThinkingLevelsAsync(ct);
await conv.SetThinkingLevelAsync(level, ct);

// 5. Send / stream / stop / reconnect (acceptance is not completion)
(string text, PiRunReceipt receipt) = await conv.SendAsync(msg, progress, ct);
await conv.AbortAsync(ct);                       // user Stop
PiEngineState back = await conv.ReconnectAsync(ct); // transport died; same session
string? branch = ProjectGit.ParseBranch(raw);    // shared parser; HEAD → null
```

## Pi-native behaviour preserved

- Pi executes with the user's OS authority **in the project root**;
  isolation is between projects (separate session + cwd), not a sandbox.
- Project instructions load natively: Core never passes
  `--no-context-files`, so `AGENTS.md`/`CLAUDE.md` discovery is Pi's own.
  The `--no-extensions --no-skills` launch flags are unchanged from the
  audited baseline (documented here so GUI copy never claims otherwise).
- Credentials are never copied: provider auth flows from Pi's environment.
- Agent X files (registry, session files) live under the managed root,
  never inside the project; a missing project throws instead of recreating.

## Verification (this slice)

- `dotnet test tests/AgentX.Core.Tests --filter ProjectLifecycleTests`:
  8 new tests (non-repo, missing dir, repo dirt, parser parity,
  create+git-init with no-remote/no-commit + workspace bind, hint
  sanitize + collision, missing parent, name rejects).
- Full Core suite re-run required before commit (no App changes; Agent
  TWO's files untouched).
- Live inference runs stay out of automated tests: prompts cost
  inference, so streaming/settle truth is covered by loopback + protocol
  tests plus the no-inference live probe above.

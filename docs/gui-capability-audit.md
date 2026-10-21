# GUI Capability Audit — Pi-First App (Agent TWO, feature/pi-first-app @ dcb9fda)

Date: 2026-10-16. Baseline tests: Core 374/374, App 253/253 (Release, 1 pre-existing CS0108 warning).
Scope: every visible control in `MainWindow.axaml` cross-checked against handlers in `MainWindow.axaml.cs` / `MainWindow.Shell.cs` and Core (`PiWorkers`, `WorkspaceRegistry`, `AgentXWorkspace`, `PiConversation`).

## Genuine (backed by Core/Pi state)

- Rail: Overview / Feed / Settings (`Nav_OnClick`) — page switches only, preserve feed/scroll/composer. Genuine.
- Worker card strip + `Add project` (`AddContextButton` → `AddWorkspace_OnClick` → `AddWorkspaceCoreAsync` → staged confirm flyout): folder picker → `WorkspaceRegistry.GetOrRegister` / `PiWorkerManager.CreateWorker` → `SelectWorkerTab`/`SwitchToWorker`. Duplicate detection via canonical root. Genuine existing-project route.
- Card menu: Connect / Reconnect / Disconnect / Remove (`BuildCardMenu`, `WorkerReconnectAsync`, `RemoveWorker_OnClick` with busy guard). Genuine, per-worker processes.
- Composer: `PiPromptBox` multiline (`AcceptsReturn`, wrap, 28–220px grow), Ctrl+Enter send / Escape stop (`ComposerSend_OnClick`/`ComposerStop_OnClick` via `ComposerActionPolicy`), Send/Stop dual button (`RefreshComposerSendButton`), cut/copy/paste/select-all menu. Send path is `PiConversation` real; long packets supported by wrapping + vertical scroll. Genuine.
- Feed: `ConversationList` virtualized, real `PiConversation` history + `FeedBridge` (user/assistant/thinking/tool-call/tool-result/error/compaction), `ReconnectAsync` restores `History.Count` with count in status. Session persisted on transport death (`ConnectionLost` → "press Reconnect"). Genuine restore path.
- Find: `FindBox` + prev/next/close, copy selected / copy full conversation / copy session map, paste-packet + 100k/25MiB/1M loaders, copy/export full text. Genuine transcript utilities.
- Status/footer: `DirectoryButton` (guarded while busy/connecting, retargets via Add flow), `StatusText`/`PiConnectionState`/`ConnectionDot`, `UsageText` (honest: messages only, "Token/cost context is unavailable"), `EmptyConnectButton` + `ComposerReconnectButton` visibility (`empty && !connected && !busy`). Genuine + truthful disabled reasons.
- Settings: `VisibilityCombo`/`ThemeCombo`/`TreatmentCombo`/`FeedCombo`/`FeedSizeCombo` (persisted `ViewPrefs`), open sessions/registry folders. Genuine (no redesign in this slice).

## Unwired / misleading / partial (fixed in this slice)

1. **Model / Thinking selectors (PARTIAL → FIXED).** `ModelButton`/`ThinkingButton` rendered current `get_state` values but flyout showed only `UnavailableReason` from `PendingEngineSettingsRuntime` (`CanConfigure=false` always, "selection pending the Core operation"). Core contract already landed and tested (`CoreEngineSettingsRuntime` over validated `PiWorker.GetModels/SetModel/GetThinkingLevels/SetThinkingLevel`, `CoreEngineSettingsRuntimeTests` 15/15). App left activation deferred to avoid a chooser-less "Choose:" header. Fix in this slice: swap construction site to `CoreEngineSettingsRuntime(() => SelectedWorker … PiWorkerEngineSettings …)` + native chooser rendering real lists, capability-gated, errors surfaced verbatim, footer tooltip updated from "selection pending" to live reason/current.
2. **Quick Setup (MISSING → ADDED).** No generated-name / new-folder / `git init` route existed; only Existing Project (folder picker). Mission requires both routes leading to same agent experience. Fix: `QuickSetup` helper (name generation, folder creation under user-chosen parent, `git init` when git present else honest "git unavailable" note) + "Quick setup…" button in Add confirmation flyout reusing `WorkspaceRegistry.GetOrRegister` + `PiWorkerManager.CreateWorker`. No mock engine, no duplicate Core.
3. **Search (GENUINE but discoverability note).** `SearchButton_OnClick` toggles find bar; honest. Kept.

## Not in this slice (deferred, no placeholders added)

- Major Settings redesign (explicitly out of scope per mission).
- Stats page (enum reserves index 3 only when implemented; no dead rail item).
- Token/cost meter (Core exposes messages only; footer states this plainly).

## Verification

- `dotnet test tests/AgentX.App.Tests -c Release` + `tests/AgentX.Core.Tests -c Release` green before and after.
- No Pi processes started/stopped by audit; isolated profile via `AGENTX_MANAGED_ROOT` for any manual run.
- This doc is the audit deliverable; code changes are limited to QuickSetup + engine chooser activation.

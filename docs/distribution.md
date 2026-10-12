# Agent X portable distribution (RC1 D1 groundwork)

Packaging groundwork, not a release. No installer, no signing, no
auto-update, no uploads. Everything below was verified on Windows 11
25H2 (build 26220) x64 unless marked NOT VERIFIED.

## Rebuild the package

Requires the .NET 10 SDK and nuget.org access for the first restore.
From a clean tree on `bootstrap/agent-x-foundation` (or a feature branch):

```powershell
dotnet build AgentX.sln -c Release
dotnet test AgentX.sln -c Release --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/New-PortableDist.ps1
```

The script refuses a dirty tree (provenance must match a commit), publishes
`src/AgentX.App` self-contained for `win-x64` (single-file off, trimmed
off), stamps `version.txt`, copies `packaging/` notices, zips the folder's
CONTENTS (so the archive root is the documented layout), writes
`SHA256SUMS.txt`, then re-extracts and re-hashes to verify, including
EXACT-path assertions (`app\AgentX.App.exe`, `README.txt`, `NOTICE.txt`,
`THIRD-PARTY-NOTICES.txt`, `version.txt` at extraction root) plus a guard
against a wrapper-folder layout, so the README instructions always match a
real extraction. Outputs land in `artifacts/dist/` (git-ignored). The
script is plain PowerShell 5.1-compatible — no packaging frameworks.

Each run embeds a build timestamp and machine name, so output is traceable
to a commit but NOT proven bit-for-bit reproducible across builds; a new
hash for identical source is expected until determinism is demonstrated.

Reference build: `AgentX-rc1-d1-20261012-2d03bf8-win-x64.zip` (built from
commit `2d03bf8`; 74,240,724 bytes zipped, 215,795,863 bytes / 230 files
extracted).

```text
SHA256SUMS.txt:
1c828a66f7ffa3f5dc6ec356d5c96df856f54b2e7c3320030858103a238a4431  AgentX-rc1-d1-20261012-2d03bf8-win-x64.zip
```

Verified extraction layout (exact paths at archive root, no wrapper
folder): `app\AgentX.App.exe` (226 files under `app\`), `README.txt`,
`NOTICE.txt`, `THIRD-PARTY-NOTICES.txt`, `version.txt`. The script's
verification asserts these exact paths and fails on a nested-root archive;
the layout was also checked independently against the ZIP entry list and
by extracting and launching `app\AgentX.App.exe` from the extracted root.

## What runs without what

- Transcript viewer (paste/import/copy/export): fully offline.
- Live Pi features: need Pi 1.1.0 + Node.js + provider credentials,
  all external. Agent X never installs, updates, or reconfigures them.

## External Pi requirements (all verified live)

| Requirement | Verified state | Failure message | Recovery |
|---|---|---|---|
| Pi 1.1.0 at exactly `D:\AgentTools\bin\pi.cmd` | Chain `D:\AgentTools\bin\pi.cmd` -> npm `pi.cmd` -> `node cli.js`, `pi --version` = 1.1.0 | Connect fails with `Failed to write a Pi RPC command.` (the real cmd.exe text, `The system cannot find the path specified.`, is swallowed — diagnostic gap, see below) | Install `npm i -g @earendil-works/pi-coding-agent@1.1.0`, create a forwarding `D:\AgentTools\bin\pi.cmd` (see README-portable.txt). No in-app Pi-path setting exists yet. |
| Node.js >= 22.19.0 (Pi `engines` field) | 24.19.0 present, compatible | Exit 1, `'node' is not recognized as an internal or external command` | Install Node.js >= 22.19.0, restart the app |
| Provider credentials (Pi `auth.json` or provider env vars) | Missing-auth run connects fine; the prompt settles unhealthy (`Settled=True, Errors=1, RunHealthy=False`) | `pi auth check --provider <name> --json` -> exit 1, `{"status":"not_ready",...,"reason":"credentials_not_configured"}` | Fix credentials in Pi (`pi auth check` to diagnose). Agent X never touches them. |
| Writable session directory (`%LocalAppData%\AgentX\sessions`) | Agent X-side guard fires before Pi launches | `IOException: Cannot create '...sessions' because a file or directory with the same name already exists.` | Clear/rename the blocking path, check permissions, reconnect |
| Pi agent dir (`~/.pi/agent`, overridable via `PI_CODING_AGENT_DIR`) | Untouched by Agent X; `--no-extensions --no-skills` passed, but agent-dir settings still load | — | Do not point Agent X at a foreign agent dir |

## Known diagnostic gap (follow-up, not fixed here)

A missing Pi executable surfaces as `Failed to write a Pi RPC command.`
because `cmd.exe /c` starts fine and only the inner batch file is missing.
Early process-exit detection with stderr capture in `PiRpcClient` would
report the true cause; out of scope for packaging — documented in
`packaging/README-portable.txt` troubleshooting instead.

## Measured characteristics (reference build)

- Cold launch to window, isolated profile (redirected LOCALAPPDATA +
  USERPROFILE, SDK/toolchain scrubbed from PATH), launching the extracted
  reference build's `app\AgentX.App.exe`: **1569 ms**.
- Working set at window: **~152 MB** (self-contained runtime + Avalonia;
  ~179 MB on the earlier pre-correction build's equivalent probe).
- First launch wrote **zero files** to the fresh profile (no prefs,
  sessions, or registry until used).
- Published `AgentX.Core.dll` connects to real Pi 1.1.0 (ephemeral
  session handshake OK) — the published bits, not just repo bits.

## Rollback

1. Close Agent X. 2. Delete the extracted `app\` folder plus `README.txt`,
`NOTICE.txt`, `THIRD-PARTY-NOTICES.txt` and `version.txt` (or the
containing folder if you unzipped into its own new folder). 3. Optionally
delete `%LocalAppData%\AgentX` (sessions/workspaces). Nothing else was
touched.

## NOT VERIFIED / limitations

- Clean-machine launch (no second PC; Windows Sandbox needs elevation to
  even query — not attempted). Evidence is isolated-profile launch on the
  dev machine, which proves SDK-independence, not clean-OS compatibility.
- Unsigned binary: SmartScreen will warn. No signing service in scope.
- Fixed Pi path (`D:\AgentTools\bin\pi.cmd`, no setting) — the top
  deployment limitation; making it a preference is a Core/GUI follow-up.
- No trimming (package ~216 MB unpacked); trimming rejected for
  Avalonia reflection safety — revisit only with measured evidence.
- Agent X license undeclared (see `packaging/NOTICE.txt`) — must be
  resolved before any RC. No GPL/AGPL components expected; re-check then.
- `PackagingTests` pins direct package references to the notices file;
  transitive licenses are not enumerated (see THIRD-PARTY-NOTICES.txt).

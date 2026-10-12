# Agent X

**Release plan:** [Road to 1.0 RC1](docs/roadmap-1-0-rc1.md) (evidence-gated, Windows-first; roadmap is not a release claim).

Agent X is an AI-first, native agent environment. A resident AI should operate the environment through dependable, typed capabilities; the managed Garden will own toolchains and plugin admission. Pi is the protected, initially unmodified agent kernel. Third-party plugins target Agent X rather than Pi internals. Human and AI clients share the same typed operations and verifiable outcomes.

This repository is a deliberately small bootstrap. The first vertical slice is a native Avalonia desktop transcript viewer: import a large packet from the clipboard, index its lines without duplicating the source text, render the line list through a virtualized control, and copy or export the complete original text.

## Current status

- GUI prototype: Avalonia 12.1.4, .NET 10, C#.
- Framework license: MIT. [Avalonia license](https://github.com/AvaloniaUI/Avalonia/blob/main/licence.md)
- Core import operation and transcript model are independent of Avalonia.
- A protected Pi 1.1.0 JSONL RPC connection, Pi-native sessions, workspace identity/recovery, conversation feed and Session Map are now merged. The Garden, resident AI and Agent X plugin admission runtime remain deferred. See the RC1 roadmap for release gates and the two active PRs (observability and native desktop shell).
- A user-run Windows smoke check loaded the 100,000-line view, scrolled to the final rows, and confirmed the copy/export actions completed immediately. Larger packet sizes still need measured verification; see the performance plan below.

## Build and run

Requires the .NET 10 SDK and access to nuget.org for the first package restore.

```powershell
dotnet restore AgentX.sln
dotnet build AgentX.sln --no-restore
dotnet test AgentX.sln --no-build
dotnet run --project src/AgentX.App
```

In the app, copy a large packet, select **Paste packet**, and use **Copy full text** or **Export full text** to retrieve the untouched original. The **Load 100k-line sample**, **Load 25 MiB sample**, and **Load 1M-line sample** buttons create repeatable UI stress inputs. The 25 MiB sample is exactly 25 MiB of ASCII text (102,400 lines).

## Verification and performance plan

The core tests verify exact-text preservation, CR/LF handling, UTF-8 byte counts, random line access, and indexing a 100,000-line input. Run them with `dotnet test AgentX.sln`.

The UI acceptance run should record machine, OS, .NET runtime, packet byte size, line count, import-to-ready time, peak working set, scroll responsiveness, and copy/export equality. Start with a generated 100,000-line sample, then exercise a 25 MiB clipboard payload and a 1,000,000-line transcript. These are proposed test sizes, not established performance guarantees. A packet passes only when the full exported/copied text is byte-for-byte equivalent after UTF-8 encoding and the window remains responsive while importing and scrolling. Streamed log append, cancellation, and long-running measurements are the next milestone.

## Repository map

- `src/AgentX.Core` — UI-independent typed transcript import and exact source storage.
- `src/AgentX.App` — Avalonia desktop shell and virtualized transcript view.
- `tests/AgentX.Core.Tests` — executable correctness checks for the core contract.
- `docs/architecture.md` — current boundaries, decisions, and next milestone.
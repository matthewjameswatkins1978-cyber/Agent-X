# Agent X

**A native workspace where coding agents do the work and the evidence stays visible.**

Agent X is an experimental desktop environment for working with AI agents. The long-term idea is straightforward: people and agents should use the same dependable operations, the same workspace state, and inspectable results, instead of forcing a human to reconstruct what happened from a wall of chat messages.

Today, the repository is a **Windows-first Avalonia/.NET prototype**, not that complete vision. It began as a fast, accurate transcript viewer and now includes Pi RPC/session foundations, workspace identity and a conversation feed. The planned resident AI, managed toolchain “Garden”, and plugin-admission runtime are **not yet complete**.

The transcript viewer is immediately useful: paste a large packet, scroll its virtualized lines, and copy or export the original source text without losing it. This is also the test bed for making the rest of the environment robust.

[Road to 1.0 RC1](docs/roadmap-1-0-rc1.md) lays out the evidence-gated release plan, not a promise that RC1 is already here.

## Current status

- GUI prototype: Avalonia 12.1.4, .NET 10, C#.
- Framework license: MIT. [Avalonia license](https://github.com/AvaloniaUI/Avalonia/blob/main/licence.md)
- Core import operation and transcript model are independent of Avalonia.
- A protected Pi 1.1.0 JSONL RPC connection, Pi-native sessions, workspace identity/recovery, conversation feed and Session Map are now merged. The Garden, resident AI and Agent X plugin admission runtime remain deferred. See the RC1 roadmap for remaining release gates; PR status should be checked live rather than treated as a permanent README fact.
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
Agent X — portable Windows distribution
=========================================

This folder is a portable (no-install) copy of the Agent X desktop app.
Unzip anywhere and run app\AgentX.App.exe. Nothing is installed; nothing
runs at startup; no administrator rights needed.

WHAT THIS IS
  A packaging-groundwork build for verification only. It is not a release,
  not signed, and SmartScreen will warn on first launch (unsigned binary).
  See version.txt for the exact commit and build provenance.

AGENT X LICENSE
  Agent X source has no declared license yet: treat this build as
  Matthew Way's copyrighted work-in-progress shared for verification,
  all rights reserved unless a LICENSE file states otherwise. Declaring
  the Agent X license is an open item before any RC — do not redistribute
  this build beyond its verification purpose.

PREREQUISITES (all external, never installed or changed by Agent X)
  1. .NET — NOT required. The build is self-contained (runtime bundled).
  2. Pi 1.1.0 — REQUIRED for Live features (Connect, prompts, sessions).
     Install separately (e.g. `npm install -g @earendil-works/pi-coding-agent@1.1.0`)
     and make sure a `pi.cmd` shim exists at exactly:

        D:\AgentTools\bin\pi.cmd

     Agent X launches Pi at that fixed path; there is no in-app setting for
     it yet (known limitation). A shim one-liner that forwards to your real
     it yet (known limitation). Create D:\AgentTools\bin\pi.cmd with:

        @echo off
        call "<your-real-pi.cmd>" %*
        exit /b %ERRORLEVEL%

  3. Node.js >= 22.19.0 — REQUIRED by Pi (pi.cmd delegates to node).
     Check with `node --version`.
  4. Provider credentials — REQUIRED for prompts. Pi reads them from its
     own agent directory (<agent-dir>/auth.json, default ~/.pi/agent) or
     provider environment variables (e.g. GEMINI_API_KEY). Diagnose with:

        pi auth check --provider <your-provider> --json

     Agent X never copies, stores, or passes credentials.

WITHOUT PI
  The transcript viewer (paste/import/copy/export) works fully offline.
  Pressing Connect without Pi at the expected path fails with a transport
  error — see "TROUBLESHOOTING" below. Your sessions and Pi config are
  never modified by the portable app beyond Pi's own normal session files.

DATA LOCATIONS (outside this folder — rollback deletes below only if you choose)
  %LocalAppData%\AgentX\   Agent X sessions, workspaces, preferences
  ~/.pi/agent (or PI_CODING_AGENT_DIR)   Pi's own config, auth, sessions

ROLLBACK
  1. Close Agent X.
  2. Delete this unzipped folder.
  3. Optional: delete %LocalAppData%\AgentX to remove sessions/workspaces.
  Nothing else on the machine was touched by running it.

TROUBLESHOOTING
  - "Failed to write a Pi RPC command." on Connect: Pi did not start —
    almost always a missing pi.cmd at D:\AgentTools\bin\pi.cmd (the real
    cmd.exe message is "The system cannot find the path specified.").
    Recovery: install Pi 1.1.0 and create the shim above, then Connect again.
  - "'node' is not recognized...": Node.js missing from PATH.
    Recovery: install Node.js >= 22.19.0 and restart the app.
  - Prompts settle unhealthy with errors: usually missing provider
    credentials. Recovery: `pi auth check --provider <name> --json`;
    want {"status":"ready"}. Fix credentials in Pi, not in Agent X.
  - "Cannot create '...sessions' because a file or directory with the same
    name already exists.": something blocks the session directory.
    Recovery: clear or rename the path shown, check permissions, reconnect.
  - Window does not appear / immediate exit: read-only unzip location or
    missing Windows 11 x64 WebView-free desktop — run from a writable
    folder on Windows 11 x64; report version.txt + Event Viewer entry.

NOTICES: THIRD-PARTY-NOTICES.txt. PROVENANCE: version.txt.

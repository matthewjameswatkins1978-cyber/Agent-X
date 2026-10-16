# Composer input anomaly — bounded investigation (B2 final pass)

Earlier isolated testing reported unexpected text appearing in a session
and occasional composer-prefix / keyboard-focus anomalies. This note
records exactly what was checked, what was found, and what remains.

## What the code proves

- User feed rows have exactly ONE writer: the composer send paths
  (`SendPrompt_OnClick` → `_feed.AddUser`, `WorkerSendAsync`). No
  background path, import path, or restore path can author a `user`
  row. The unexplained prompt travelled the real send path.
- The send chord (Ctrl+Enter) is tunnel-handled and sends exactly the
  box contents; plain Enter still inserts a newline. The Send button
  and the chord share one guarded path (`ComposerActionPolicy`).
- Drafts persist per view key (`ViewPrefs.Drafts`) and restore on
  every tab switch and launch.

## Defect found and fixed

After a successful send the box was cleared but the persisted draft
was NOT: the next tab switch restored the just-sent text as a
phantom draft — resendable verbatim with one chord press. Both send
paths now persist the vacated box (`SaveWorkerViewState(_lastViewKey)`
inside the clear branch; the key always names the displayed view).
`view-prefs.json` confirms `Drafts` empty after sends.

## Harness contribution (not a product defect)

The test harness sets composer text via UIA `SetValue` and delivers
Ctrl+Enter with `keybd_event`, which requires live window foreground.
When the chord misses, the text stays — and the app correctly keeps
it as a draft. Two earlier sightings fit this exactly:

- A SetValue'd prompt that missed its chord, persisted as a draft,
  restored on a later launch, and sent by a later chord press
  (the "unexpected" prompt).
- Partial "composer-prefix" text: a missed chord after partial
  delivery focus changes.

Verified mitigations: the send-clear only fires when the box still
matches the sent message (a switched tab can never clear or send
another view's draft); failed sends never clear; the button path
(UIA `Send message`) delivers deterministically without foreground.

## Residual (release-blocker watch, not reproduced as product fault)

- `keybd_event` delivery without foreground remains flaky BY DESIGN
  of the harness, not the app. Human keyboard delivery was not
  observed to drop or duplicate during this pass.
- Two-worker draft isolation and cross-restart resend were reviewed
  in code (per-key drafts, coherent key tracking) but not exercised
  live — no second worker was created in the isolated profiles.
  Recommended before release: two tabs, type/switch/send/switch —
  the restored box must be empty.

No external input, injection, or focus theft was found. Nothing was
dismissed: the phantom-draft defect above is the code-grounded
mechanism for "unexpected text", fixed and tested in this pass.

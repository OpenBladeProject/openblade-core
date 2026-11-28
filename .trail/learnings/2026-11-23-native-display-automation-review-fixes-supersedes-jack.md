---
title: "Native display automation review fixes (supersedes the port learning's preempt claim)"
author: Jack
date: 2026-11-23
branch: feat/native-agent-display-automation
touches:
  - tools/openblade-windows-host/src/session_native/display_query/recovery/automatic.rs
  - tools/openblade-windows-host/src/session_native/display_query/write.rs
  - tools/openblade-windows-host/src/session_native/display_query/recovery.rs
  - tools/openblade-windows-host/src/session_native/display_query/automation.rs
  - tools/openblade-windows-host/src/session_native/display_query/automation/target.rs
  - tools/openblade-windows-host/src/session_native/installed_monitor.rs
  - docs/windows-native-agent-display-automation.md
---

## Decided
- This supersedes two claims in the "Native Agent display automation port" learning: that a manual mutation's preempt only cancels the running transition, and that native beginning the manual operation after the canceled transition records is a harmless ordering difference. It is not harmless. The gate is released when `apply` returns, before the transition takes the recovery lock to record, so either can go first, and a transition that records first could unpause, resume, and advance the power generation under the manual write.
- `Recovery::preempt` now marks the manual operation pending (`begin_manual`) as well as canceling, before `manual_turn` waits for the gate. That is managed's `BeginManualOperation` order. A canceled transition then records as under a begun manual operation, whichever of its result and `begin` comes first. `writes_paused()` (paused or manual pending) also stops `observe` from scheduling in the window between preempt and begin. `ManualTurn`'s `Drop` runs `end_manual_turn` while it still holds the gate, so a mutation refused before `begin` leaves nothing pending. The endpoint serves one request at a time, so one flag is enough.
- The native unresolved-field gate no longer drops a target. On refusal the target stays pending with `gated` set. The explicit read that resolves a field calls `resume_gated` and schedules it again, unless automation is paused or held. Any new schedule clears `gated`.
- `Automation::observe(status, admit)` resolves the display members before `admit` lets power admission take the status, so a status managed would refuse is never admitted, even briefly. `target::resolve` decodes all three power displays and refuses a brightness above 100, as managed `ValidProfileDisplays`/`ValidDisplay` do.
- The CI automation lane runs every `display_query` suite, because the manual writes share automation's gate and recovery state.

## Tried and ruled out
- Holding the gate from `admit_transition` until each attempt records, as the other fix. It does not cover the case where a canceled second attempt is Blocked and a manual write then Fails: managed ends paused because `BeginManualOperation` captured the in-flight write, but under a held gate the transition records first and unpauses.

## Watch out
- A locked console is still the active console (`active_console` checks the WTS connect state), so a profile step is refused only for a disconnected or switched-away session. That matches managed.
- Any available snapshot observes refresh, HDR and color (`Snapshot::observes`); only brightness needs a level. A test that wants a read resolving nothing must use an unavailable snapshot.

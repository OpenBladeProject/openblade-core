---
title: Native lifecycle causes outlast refusals and activate the console user
author: Jack
date: 2026-11-23
branch: feat/native-power-lifecycle-reconcile
touches:
  - docs/windows-native-thermal-mutation.md
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeLifecycleReconcileVectors.cs
  - tools/openblade-windows-host/src/windows/scm/installed/lifecycle_triggers.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/request_triggers_lifecycle.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_windows.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
  - tools/openblade-windows-host/tests/data/lifecycle-reconcile-vectors.json
---

Supersedes 2026-11-23-native-lifecycle-tick-reconciles-the-console-jack. A delta review found that its cause budget still dropped causes, and that the fallback it relied on ("the next report") never happens in the default configuration.

## Decided
- A lifecycle cause has no budget of its own (`lifecycle::Pending { owe, tried }`).
  - A refusal before any step (no console user, refused capture, locked session, fenced store, unknown power) spends nothing and waits for the ordinary write epoch to move. That is at most one capture per move.
  - An attempt whose steps leave the debt `Owed` is retried on the next epoch move, whatever the epoch did during the step. The debt's own `RETRIES` in `Reconciled::after` bound it, and only a step with nothing possibly submitted can leave it owed.
  - A fenced store still stops any replay.
- A console user other than the last one activated is a fourth cause and owes every class. This includes the first console user after start. It ports managed `ActivateUserAsync`: `ControlRequestHandler.cs:210-220` runs it at a new active user's first admitted request, and `ServiceControlRuntime.cs:1668-1677` runs ApplyFullTransitionAsync. It is keyed on SID only, as managed's `activeUserSid` is.
  - Managed start-up applies the machine configuration (`ReconcileStartupProfileAsync`); native has no machine profile.
  - Recorded as `userActivatedOnBattery` and `sameUserReactivated` in lifecycle-reconcile-vectors.json.
- The tick reads the console probe only on its first pass, after an epoch move (every WTS notice fences it), or while no user is known. This avoids a token query every 500 ms and still sees a boot logon without a further notice.
- `console_user` classifies its token with the request path's `windows::token_context` and refuses anything but `Logon::Interactive`. A token carrying S-1-5-14 is refused, as a RemoteInteractive request is.
- A tick that finds the mutation gate held still stamps its time, so a long request is not mistaken for sleep. The `SLEPT` threshold is pinned at the gap and 1 s below it.

## Tried and ruled out
- Pending counting its own attempts (`1 + RETRIES`). A locked session sees many unrelated fences (battery-level power notices, other sessions' WTS notices), which spent the attempts before the unlock, so the plugged-in preset stayed applied on battery.
- Ending the cause when an unmoved-epoch transient refusal left the debt owed, for example a USB-C adapter whose 07/8C class is not settled yet. The debt stayed owed, and no report ever came.
- Reading the console user on every tick: a WTSQueryUserToken and token-information round trip twice a second for no gain.

## Watch out
- The activation runs at the first tick, so in `request_triggers_lifecycle.rs` `wired()` now reconciles once. Take test baselines after `wired`/`applied_game`. A rig with no report still needs `rig.get()` first: the status feed has no publisher until a status read.
- `wired` returns a flag that switches the console probe off. The probe must be able to name nobody while a reporter exists, to pin that the reporter is not used in its place.
- The native start-up power sample and `console_user` are compile-only offline.
- Activation also overwrites out-of-band performance or fan changes, because there is no drift tracker. This is the documented divergence from Architecture rule 5.

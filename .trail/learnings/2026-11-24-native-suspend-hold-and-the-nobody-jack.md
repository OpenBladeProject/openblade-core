---
title: Native suspend hold and the nobody-signed-in releases
author: Jack
date: 2026-11-24
branch: feat/native-suspend-hold
touches:
  - tools/openblade-windows-host/src/origin_lifetime/suspend_hold.rs
  - tools/openblade-windows-host/src/origin_lifetime.rs
  - tools/openblade-windows-host/src/scm_host.rs
  - tools/openblade-windows-host/src/windows/scm/installed/low_power_cleanup.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - docs/windows-rust-development-service.md
---

## Decided

- The native Service holds the SCM control callback for a newly accepted PBT_APMSUSPEND generation until `prepare_for_low_power` records an outcome for it, so Windows does not suspend under the keyboard's Normal release. `SuspendHold` (origin_lifetime/suspend_hold.rs) is a Mutex plus Condvar owned by `Lifecycle`. `Runtime::native` arms it once with `MAXIMUM_AGE` (1.5 s) through `Lifecycle::arm_suspend_hold`. `Lifecycle::apply` returns a `PendingHold` only for a new suspending generation while armed, with a deadline of the notice's QPC stamp plus the cap, not "now plus the cap".
- `low_power_cleanup::Recorded` is a drop guard created right after the gate is read, so every exit records the generation: prepared, aged out, already prepared, unadmitted, error, or a dropped future. An unarmed hold, a disarmed hold (the `HoldArm` dropped with the Runtime), a poisoned mutex, an unknown age or a spent cap never waits.
- `scm_host::deliver` copies the hook out of the INGRESS slot, calls it with no lock held, then waits, then runs the ordinary control handler. STOP, SHUTDOWN and SESSIONCHANGE never return a hold. The waiter is injected so tests drive the real `deliver`.
- The preparer's thread id is recorded at arm time and `PendingHold::wait` returns at once on that thread. In the installed product the callback runs on the SCM dispatcher thread and the preparer on the current-thread tokio runtime that ServiceMain builds, so this guard only stops a self-deadlock if that ever changes.
- A Fast Startup ReleaseIntent is not resolved while nobody is signed in. After this process's own release only a client ObserveState read's confirmed 00/84 resolves it (`driver_mode_confirmed_since`), and no client runs at the logon screen. The first read after sign-in resolves it with no console capture. A new process resolves it through its console-captured step.

## Tried and ruled out

- Waiting inside the ingress hook while holding the INGRESS or ENTRY_ORIGIN lock: the cross-thread test `a_held_callback_waits_outside_the_ingress_lock` kills that mutant. Keep the wait outside every lock.
- Timing the cap from when the callback arrives: a late callback then holds past managed's 1.5 s age window. The cap counts from the notice stamp, as the age check does.

## Watch out

- Mutating `fence_for` alone to admit Ordinary cleanup during low power survives, because `authorize_owned_stop` also checks `lifecycle.active()`. The combined fence and epoch mutant is killed by `a_shutdown_while_suspended_refuses_the_release`. Do not delete either layer on the strength of the other.
- `control()` itself is only compile-checked offline; `deliver` carries the tested logic. SetServiceStatus or Unregister during a hold is untested and bounded only by the cap.
- The hold changes no admission, fence or IPC and sends nothing. G3 (an unactivated process refuses cleanup) and G4 remain known limits, and V2-V4 are still pending before packaging.

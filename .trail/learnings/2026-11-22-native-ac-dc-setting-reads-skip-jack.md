---
title: Native AC/DC setting reads skip a duplicate fence
author: Jack
date: 2026-11-22
branch: feat/native-power-setting-notifications
touches:
  - tools/openblade-windows-host/src/origin_lifetime.rs
  - tools/openblade-windows-host/src/scm_host.rs
  - tools/openblade-windows-host/src/windows/scm/installed/power_notifications.rs
  - tools/openblade-windows-host/src/windows/scm/tests.rs
  - docs/windows-native-thermal-mutation.md
---

## Decided
- Native still does not fence on a power-setting read, but the reason is different. Managed fences nothing on a display read. It does fence the AC/DC read: that callback is its only `PowerChanged` publisher (WindowsServiceLifecycleEventSource.cs:695), and `HandlePowerSourceChangedAsync` bumps `powerAdmissionEpoch` and `pendingPowerAdmissions` (ServiceControlRuntime.cs:12327-12328). Those gate HyperBoost, drift validation, convergence, fan-curve maintenance, fan recovery, the cooling pad and fan-stop entry. Native's fence for a source change is PBT_APMPOWERSTATUSCHANGE, which still advances the epoch, so fencing the setting read too is a duplicate. That duplicate is what lost the temporary-full-charge cancel.
- C1 must keep that fence. Managed keys the automatic transition on the resolved class change (`ChangePowerSourceCore`, ServiceControlRuntime.cs:12351-12352) after bumping the admission epoch, not on the Windows power generation. C1 coalesces like managed's pending PowerChanged bit and needs no time debounce, but it must not drop the epoch advance or move it onto the setting read.
- Native reports RUNNING before it registers, so a registration failure shows SCM RUNNING and then STOPPED (failed). Managed's constructor throws before RUNNING is reported.

## Supersedes
- `2026-11-22-native-power-setting-reads-do-not-jack.md`:
  - Its first Decided bullet says "Managed fences nothing on display or AC/DC reads (ServiceLifecycleRefreshLoop.cs:97-158)". Managed does fence the AC/DC read. Native skips it because PBT_APMPOWERSTATUSCHANGE already fences.
  - Its C1 bullet says managed "keys on the power-kind generation". It keys on the resolved class change, after the admission-epoch bump.
  - Its Tried-and-ruled-out bullet says "Managed has no generation for these reads, so a fence there is a native-only behavior with nothing to match". Managed has one for AC/DC, `powerAdmissionEpoch`. The fence is ruled out because it duplicates PBT_APMPOWERSTATUSCHANGE.
  - Its watch-out says the control test must be "the only test that calls `set_ingress`". That is not the hazard. The hazard is every other `control()` caller.
- Commit f3598cb3's message repeats the wrong managed rationale; the commit that adds this learning corrects it.

## Watch out
- Once the `scm_host::control_tests` ingress is bound, every other `control()` caller also lands in its recorder. One example is `native_control_trampoline_signals_the_registered_stop_channel` in `windows/scm/tests.rs`. A panic inside `extern "system" control` aborts the whole test binary. The recorder therefore tolerates a poisoned lock and the test compares only the POWEREVENT entries it sent. Before that fix, the two tests failed 412 of 600 runs at 4 threads.
- Two things are unverified and attended-only: whether SCM drops a power event sent outside the accept mask, and whether every AC/DC switch also delivers PBT_APMPOWERSTATUSCHANGE. The no-fence choice relies on the second.

---
title: Preserve battery operation truth across release bookkeeping
author: Jack
date: 2026-11-14
branch: fix/native-tfc-outcome-publication
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/temporary_charge_release.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
---

## Decided
Publish the battery worker's actual outcome and immediate full family delta before composing a typed capacity-release pause. Keep original caller/deadline, independent confirmation, pending/fatal fences, and no replay. Derive quarantine reasons under the existing storage lock; coalesce pause causes rather than only paused booleans.

## Tried and ruled out
On frozen 202bd705, clean explicit/automatic cancellation records72/73 with temporary=false and clears the matching reservation. Automatic cancellation also consumes that origin at Prepared. Release returns NoMatchingReservation before any tagged read, so a release-read fault after clean cancellation is not a valid failing recipe. Keep it as a passing control. Use the ordinary policy62 Prepared race for confirmed-outcome deferral, and authority-refused automatic cancellation for immediate desired70-to-default80 loss.

## Watch out
Inspect publisher bytes before GetStatus/retained decoration can repair data. A Battery intent=false is not firmware confirmation. Failed settlement must keep its real pending/quarantine reason, including with stale retained proof; do not describe it as transient retry. Busy-to-read-unavailable-to-reconciliation changes must publish while all remain paused. Release-only recovery removes only its own public contribution and must preserve current SID-owned outcome and other-family degradation.

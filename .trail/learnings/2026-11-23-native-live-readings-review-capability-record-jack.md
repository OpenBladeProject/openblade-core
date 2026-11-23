---
title: "Native live readings review: capability record only beside HyperBoost, fan pair back-off"
author: Jack
date: 2026-11-23
branch: feat/native-live-readings
touches:
  - build/native_windows_fixtures.py
---

Supersedes "Native live readings: fan RPM, battery flow and health, HyperBoost capabilities" (2026-11-23) on the capability record and the fan pair's send rules; the rest of that learning still holds.

## Decided
- Native `capabilities` is null except beside a confirmed active HyperBoost. Managed's exact-model capability set would claim controls native does not admit, and null claims nothing. The managed codec refuses `hyperBoostActive: true` without a HyperBoost access, so that state alone carries the observation record (managed `Unavailable()` with `hyperBoostAccess: 0`).
- The record keeps `supportsTemporaryFullCharge: true`, the exact RZ09-0581 battery evidence. That member is not nullable, and the tray reads false as "the model has no TFC" (MainViewModel's TFC status text and the discharge warning compare `Capabilities?.SupportsTemporaryFullCharge` with false). True only keeps charge-limit actions waiting for the temporary state. `capability_tests` derives it from `Rz09_0581_02e0_3_01_BatteryProduction.json`.
- The record is a function of the published confirmed state: `Snapshot::confirm` sets both. Every projection (fresh read, retained, unavailable, standard performance, mutation rebase) goes through it, so a mutation's own read carries the same record as an ObserveState (review F3).
- The managed pin is deliberate: `ActualNativeMutationResponsesUseManagedV85CodecAndOutcomes` now asserts capabilities are present exactly when the confirmed state has an active HyperBoost.
- The 0D/88 pair backs off after an unconfirmed pair, or after an asked-for job that brought back nothing, which fails closed as the Driver-mode query does. The pause is 15 s, doubling per consecutive failure to 5 min, and a confirmed pair ends it. The pool math is in the live-readings doc section: about 21 hours to exhaust, against 8.5 minutes before.
- No 0D/88 SET is sent unless the budget covers SET + settle + GET (130 ms, `fan_rpm::EXCHANGE`), and the pair starts only with two of those. An unsent second query leaves its ID free.
- A retained fan pair never crosses a mutation: `Host::mutating` runs at both mutate entries, after the serial lock.

## Tried and ruled out
- Publishing the record on every confirmed read (the first version). It broke the native-fixture managed fact (`Assert.Null(status.Capabilities)` for Balanced/Performance frames) and flipped the tray's null-versus-false TFC checks.
- Fixed 15 s back-off alone. It still exhausts the pool in about 63 minutes of a pair that never answers.

## Watch out
- The native-fixture facts run only through build/native_windows_fixtures.py, and python is a Store stub here. A PowerShell 5.1 replica that builds both tuples, collects all five corpora and runs every fact lives in the session scratchpad (native-fixtures.ps1). The repo's build/NativeWindowsFixtures.ps1 only wraps that python runner, so it cannot drive them either.
- The managed history fact nests its private fixture deep below the run directory; under the session scratchpad it passes MAX_PATH and fails with "The native epoch path could not be retained". The replica keeps runs in a short temp root (%TEMP%\obnf\run-<8 hex>).
- `sed -i` in Git Bash rewrote a CRLF doc to LF. Normalize CRLF files with node after any shell edit.
- Live-reading rig tests cannot use a paused clock (the Reader worker is an OS thread), so the back-off is ended with `Host::end_backoff_for_tests` rather than by advancing time.

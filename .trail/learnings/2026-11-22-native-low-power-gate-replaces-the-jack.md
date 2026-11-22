---
title: Native low-power gate replaces the lifecycle awake flag
author: Jack
date: 2026-11-22
branch: feat/native-low-power-gate
touches:
  - rust/crates/openblade-domain/src/low_power_gate.rs
  - tools/openblade-windows-host/src/origin_lifetime.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
  - tests/OpenBlade.Service.Tests/LowPowerGateNativeVectorsTests.cs
  - docs/windows-native-thermal-mutation.md
---

## Decided
- The ported managed `LowPowerGate` lives in `openblade_domain::low_power_gate` as pure state with injected QPC timestamps. The shipping epoch `Lifecycle` holds it under the epoch's lock and it replaced the `awake` flag, so there is one low-power state. The unwired lifecycle worker keeps no authority: its `Activity` must read this gate before the worker is wired.
- `active()` is `epoch_for(WriteScope::Ordinary)`, so every capture, `capture_settle`, mailbox custody and status refuse in low power exactly as before. `WriteScope::LowPowerFanAutomatic { generation }` is the managed `PriorAuthorityCleanup` exception. It is admitted only while that odd generation is current, and nothing captures it yet (D1).
- Every power and session notice still advances the epoch, a stale gate signal included. `record(code, signal, timestamp)` is the entry B2 uses: DisplayOn for a decoded display on or dim notice, no signal for display-off or AC/DC.
- Vectors are recorded by `LowPowerGateNativeVectorsTests` into `tools/openblade-windows-host/tests/data/low-power-gate-vectors.json`. Inputs live in the C# recorder, and the file must match them.

## Tried and ruled out
- Putting the 5 s duplicate-resume window in the gate. Managed applies it after `TryApply`, to dispatch only; the gate's own duplicate rule is the 2 s new-resume window.
- Giving the owned Stop or shutdown handback the cleanup exception. Managed passes no cleanup admission there, so it blocks them in low power.

## Watch out
- Host tests get a strictly increasing 10 MHz counter from `qpc::now()`, so back-to-back `ingress` calls never share a timestamp. Pass explicit timestamps to `record` to test the window or ordering; mixing the two on one `Lifecycle` can make a scripted signal stale.
- Before a first boundary the gate's boundary is 0, and a first resume only sets it, however late. The `boundary > 0` check is what stops a large real QPC value from bumping the generation at startup. Only the `resume/first-late-resume-only-sets-the-boundary` vector kills a `>= 0` mutant.
- Retaining an odd generation is unobservable, so that mutant survives as an equivalent.

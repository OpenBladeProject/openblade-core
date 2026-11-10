---
title: Scope scripted battery faults and defer source changes during recovery
author: Jack
date: 2026-11-09
branch: feat/rust-battery-parity
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/src/battery_runtime.rs
---

## Decided
The isolated battery scenario clears unused injected setter faults before profile application. If power changes during unconfirmed policy intent, independent readback can select the new profile without committing the old source's intent or issuing a setter.

## Tried and ruled out
The managed scoped runtime preserves its last published confirmation after a lost SET response. Its explicit protection-enable path also refuses to retarget protection that is already enabled at a different limit. A profile reconciliation or an explicit limiter request has separate eligibility.

## Watch out
Passive battery drift follows the last comparable fields and stays visible until a matching intent or reconciliation clears it. Trail's prose matches in the development guide refer to synthetic data and do not revive production hardware actions.

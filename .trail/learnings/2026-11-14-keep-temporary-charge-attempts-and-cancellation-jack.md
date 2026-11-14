---
title: Keep temporary charge attempts and cancellation quota identity-bound
author: Jack
date: 2026-11-14
branch: feat/windows-native-temporary-charge
touches:
  - tests/OpenBlade.Contracts.Tests/NativeThermalMutationResponseTests.cs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/temporary_charge_corrections.rs
---

## Decided
Track actual synchronous Reader registration for temporary-charge requests. A future-drop guard can retire only a request that never registered; event eligibility must reconcile that prospective state and consider the confirmed original owner independently. A registered uncertain request keeps its worker, original evidence and global fence.

Bind retained cancellation journal capacity to the exact original request and typed intent hash. Release only settled, nonquarantined matching capacity after the runtime obligation ends. If bookkeeping I/O fails after the one-shot automatic path is consumed, retain the identity as release-only abandoned state: retry bookkeeping, never the setter. Preserve fan capacity, immutable rows and finite transaction IDs.

Explicit typed false may recover under the same SID in a freshly admitted current session using new actor/request/IDs and origin=None, after Reader drain and no unresolved work. Keep exact storage/custody, current console/session/LUID admission, shared fan scope and old automatic-owner authority checks.

## Tried and ruled out
Changing pending.or(owner) selection alone misses eligibility masking. Marking every dropped future joined invents worker retirement. Clearing a bare quota can clear a replacement owner. Restoring a replay-eligible owner after release I/O failure re-enables automatic cancel on a later epoch. Removing shared session gates or broadly swallowing source/custody errors exceeds the correction.

## Watch out
A newer independently false observation and deliberate abandonment end different obligations; neither guesses firmware state. Retained abandoned capacity has no automatic write authority. Test actual refusal/drop before registration in both epochs, possible submission, original fan/TFC ID separation, matching-origin capacity/replacement/roll, I/O release failure across epochs and freshly authenticated explicit session recovery. Direct domain/protocol facts must bind the final relevant source and raw shared fixtures.

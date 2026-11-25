---
title: "Native shares the managed convergence ledger, and WithPrior stays"
author: Jack
date: 2026-11-24
branch: feat/native-thermal-convergence-loop
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_convergence_ledger.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_convergence.rs
  - rust/crates/openblade-domain/src/thermal_convergence.rs
  - docs/windows-native-thermal-mutation.md
---

Supersedes the WithPrior line of 2026-11-23-native-thermal-drift-owns-only-readback-jack.md: that divergence is now permanent, not "while native has no convergence loop".

## Decided
- Lease expiry and a report over OpenBlade's own preset keep running WithPrior and applying at once, even after native gains its convergence loop. Managed pauses there and lets its loop repair it, which spends the single convergence claim for that (destination, policy revision) on OpenBlade's own preset, and under Strict, where nothing converges, strands OpenBlade's own Fixed fans until an explicit reapply. The managed vector rows stay; the native tests pin native's outcome.
- Native shares managed's `thermal-convergence.json` (schema 1, same per-user path). `openblade_domain::thermal_convergence` ports the document, the transitions and the revision hash byte for byte; `thermal-convergence-ledger-vectors.json` is recorded through managed's real transitions and store, and its `native` section holds Rust-encoded documents that managed loads.
- The store reads the ledger once at open and fails closed: unreadable, quarantined, other-user, invalid or future-schema closes convergence and is never overwritten, and so does any write whose result is unconfirmed. Writes are compare-and-swap on the held bytes. Closed stays closed until the store reopens (user switch or restart), as managed reloads only at activation.
- The status publishes Strict (saved disclosure kept) with Paused/ConfigurationQuarantined for a closed ledger, and AwaitingObservation for a reservation's declared components under either policy once power is settled and not low power, before the Strict row (managed `ThermalConvergenceReconciliationAdmitted` ignores the policy).
- `service-thermal-convergence-v1` implies the triggers and is refused by both package readers by feature and marker.

## Tried and ruled out
- Spelling Admission with struct update in the shared status tests: in the lane without the convergence feature every field is given, and clippy's needless_update fails it. Start from `admitted()` and assign fields.

## Watch out
- Revisions use native's own resolved intent (no `maximumFans`, never GPU clock tuning), so they can differ from managed's for the same saved state; switching Services re-arms the budget at most once.
- Managed record equality compares lists by reference, so its loaded tombstone never matches in `Complete`; native compares by value. Managed's own tests use Assert.Equivalent for the same reason.
- The vector generator script only writes the managed-input sections; regenerating them drops the `native` section, which comes from `OPENBLADE_PRINT_NATIVE_LEDGER_ROWS`.
- The write path is not wired yet: nothing reserves, reconciles or publishes Suppressed, budget or tombstone states, and there is no explicit reapply, so publishing any state whose only exit is reapply is still forbidden.

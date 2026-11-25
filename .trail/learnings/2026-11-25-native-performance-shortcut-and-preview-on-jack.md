---
title: Native performance shortcut and preview on the RZ09-0581 Service
author: Jack
date: 2026-11-25
branch: feat/native-thermal-recipes
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-native-thermal-mutation.md
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/service_status/unserved.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/request_triggers.rs
---

## Decided

- The RZ09-0581 thermal recipes were already complete in the native Service: Silent, Balanced, Performance, Battery Saver, Battery Balanced and Custom are admitted on exact 1532:02E0 with fixture-compared payloads. The only remaining recipe gap is leaving HyperBoost for a battery preset, which managed does with the hyperboost-restore-balanced pair, an intermediate full read and then the battery pair. Native keeps it as the stored `NoDirectPath` refusal; implementing it is a separate recipe review.
- `cyclePerformanceShortcut` and `setPerformanceShortcutPreview` are served behind `service-performance-shortcut-v1`, which implies `service-fan-mutation-v2`. Both package readers refuse the feature and its build.rs marker by name.
- A cycle is an ordinary explicit performance request whose override is staged late, inside `RequestPermit::resolve`, after the fresh 07/8C class and 0D/82 reading. `RequestPermit` now holds `requested: OnceLock<Requested>`, which is set at construction for explicit requests and in `resolve` for a shortcut. This way the shortcut gets the same gate, combination check, journal intent digest, readback and stop-on-ambiguity without a second recipe path.
- Selection is the shared domain `performance_shortcut::next`. It is replayed from managed-recorded `tests/data/performance-shortcut-vectors.json` (`ServiceControlRuntimeTests.NativePerformanceShortcutVectors`), not from hand-written expectations. The fixture corpus tuples are fixed and python is unavailable, so the vector file is the parity source.
- The preview is held by the Reader (`ReadSource::set_shortcut_preview`) and applied in `current_policy` for control-access-0 projections only. `inactive_mutation` removes it. It takes no operation gate and never reaches the runtime's thermal gate.

## Tried and ruled out

- A shortcut that keeps the persistent override beneath its temporary: managed has one performance override slot, so the staged UntilPowerSourceChange override replaces the saved persistent one. The test asserts the document's `performance` is null afterwards.
- Expecting a full-to-limited class change mid-pair to complete for a shortcut, as it does for a persistent explicit request. The staged override is temporary, and the class change retires it, so the pair stops with the pending fence. An explicit temporary request does exactly the same; the test compares the two.

## Watch out

- The low-power deferred store must stage the shortcut too (`Staged::Shortcut` in `store_performance_now`). Passing the shortcut's `value: None` through would store a clear.
- `Cause::Shortcut` replies are Blocked and are not transient. The reply rewrite (`Source::reply`) applies only to status 0 and 1, so rejected combinations (Failed) and low-power deferrals keep the performance request's text.
- The native status message is rendered from status, so a GET after a cycle does not show the "selected" text that managed publishes.
- `request_triggers_lifecycle.rs` already fails `cargo fmt --check` on the base (mod order), independent of this change.

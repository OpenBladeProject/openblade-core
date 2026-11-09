---
title: Propagate unrecoverable native faults before service cleanup
author: Jack
date: 2026-11-08
branch: feat/rust-production-activation
touches:
  - docs/rust-boundaries.md
  - rust/README.md
  - rust/Test-ProductionActivation.ps1
  - src/OpenBlade.Core.Native/ProductNativeRuntime.cs
  - src/OpenBlade.FirmwareSmoke/Program.cs
  - src/OpenBlade.Protocol/WindowsRazerHidSession.cs
  - src/OpenBlade.Service/ApplicationProfileResolution.cs
  - src/OpenBlade.Service/ServiceLifecycleRefreshLoop.cs
  - src/OpenBlade.Service/ServicePeriodicObservationLoop.cs
  - src/OpenBlade.Service/Worker.cs
---

## Decided
Claude Opus 5.5/high completed success_native_review for stage7 base69f66ba673c0523c0521b09b286a7d09f8137488 through reviewed head691ed7b539087d8af635c05233dc64b266f4acef. Keep that original review attribution separate from later tested fixes.

NativeComponentException from profile resolution can reach background tasks rather than Program.host.Run. Report a fixed critical diagnostic and set nonzero process status at the Worker boundary before finally cleanup, then rethrow. Let this unrecoverable component error escape periodic/lifecycle transient-error catches so existing task-group cancellation drains peers. Preserve bounded cleanup and no fallback/replay. Existing cached/typed thermal paths keep their own pause/outcome contracts.

Validate the original invalid legacy fan constructor branch in the host before calling native, preserving mode/actual value/message even when policy is unavailable. A third fresh-process offline probe disposes the actual policy and proves null identity, legacy error priority, and no profile publication for a valid override's native failure. Bound missing managed informational-version metadata to the existing redacted component identity failure.

## Tried and ruled out
Do not duplicate the complete merge in managed output checks: Rust is the pure implementation, independently tested against frozen managed baselines and unchanged fixtures. Current pinned Rust correctly matches all fan constants; speculative future constant changes are detected by independent parity tests, not a current defect.

Do not pool tiny policy buffers, deduplicate independent codec/policy loaders, remove the retained direct-display API, or reorder equivalent malformed-result checks solely for style or hypothetical future drift. No measured allocation regression or current incorrect result was established. Preserve both ABI negotiations and null/error contracts.

## Watch out
A successful ordinary build does not establish import sorting; the separate formatter gate caught two import-order errors. Full Windows integration, native four-host tests, package upgrade checks and exact-head hosted checks remain separate evidence. Runtime component failure tests use inert HID resources and are not physical validation.

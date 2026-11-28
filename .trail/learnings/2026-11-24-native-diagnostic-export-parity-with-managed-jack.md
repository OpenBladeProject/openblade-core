---
title: Native diagnostic export parity with managed
author: Jack
date: 2026-11-24
branch: feat/native-export-diagnostics
touches:
  - tools/openblade-windows-host/src/diagnostic_export.rs
  - tools/openblade-windows-host/src/diagnostic_export/archive.rs
  - tools/openblade-windows-host/src/diagnostic_export/history.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_export.rs
  - tools/openblade-windows-host/src/native_package.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Service.Tests/NativeDiagnosticExportVectorTests.cs
---

## Decided
- Native `exportDiagnostics` sits behind `service-diagnostics-export-v1`, which implies `service-mailbox-observation-v1` (build.rs needs exactly one installed composition) and appends its marker last. Both package readers refuse the feature and the marker by name.
- The archive reads only the status a GET would answer the caller with, the in-memory history and host metadata; no device, firmware or storage read runs on the export path. Sections with no native source write exactly managed's empty-source text (device-verification NotObserved, firmware-queries `[]`, hyperboost-ownership Missing, experimental-support absent, logs.jsonl empty).
- Parity is proved by text, not bytes: native writes its own deflate, so `NativeDiagnosticArchive_ReadsThroughTheManagedCodecAndMatchesTheManagedProvider` decodes the native response through the v85 codec and compares each entry's text with the managed provider's archive. Rerecord `tests/data/diagnostic-export-native.json` with `OPENBLADE_REGENERATE_NATIVE_DIAGNOSTIC_ARCHIVES`; only compositions without thermal mutation record, because the private projection changes the status.
- Exports are serialized by a static one-permit semaphore; the build runs on the blocking pool with a 20 s bound and a cancel flag set by disconnect, the bound and dropping the connection task (Stop).
- The profile store's Stored save is the only history producer; managed's reconciliation summaries have none yet.

## Tried and ruled out
- A "pseudo-random" detail built from `(n * k) % 26` compresses to almost nothing; the oversize test needs a xorshift stream to reach the bound by content.
- `Metadata::capture` without the cancel token left Stop and disconnect cancellation unobservable in tests.

## Watch out
- Every Service identity test that asserts the capability field exactly or with `ends_with` must allow the export marker after it (profiles.rs, live_readings_tests.rs). The driver-mode-read test still uses `ends_with` and would fail beside the export feature.
- `StatusRequest::OPERATIONS` and the `variants()` list in `service_status/unserved.rs` must both gain a new operation, or the unserved discriminator test fails only in the profile lanes.
- The Write tool turns `\uXXXX` in file content into the character; build such Rust test literals with perl and `chr(92)`.
- The export-only lane cannot be clippy-clean: the observation lane already carries unused-code errors, so CI lints the export beside a write.

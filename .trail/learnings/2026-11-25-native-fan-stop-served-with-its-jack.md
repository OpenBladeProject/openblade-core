---
title: Native Fan Stop served with its guardian unavailable (option C)
author: Jack
date: 2026-11-25
branch: feat/native-fan-stop
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-native-thermal-mutation.md
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage.rs
  - AGENTS.md
---

## Decided

- The native Rust Service serves Fan Stop as option C: managed's production behaviour while its guardian is unavailable, behind `service-fan-stop-v1`, which implies `service-profiles-v1`. Both package readers refuse it by feature and by marker. A real guardian (option A) is a separate multi-slice project and needs owner answers on InpOut (AGENTS.md rule 6), the exact driver hash pin, and rule 1 lease delegation.
- `setFanStopMode` replies Blocked with the published status message for BOTH modes. Managed's `GuardianFanStopBackend.SetGuardianModeAsync` blocks Automatic too, so "Automatic with no ownership is Stored" is wrong for this composition. Automatic still pauses automatic Stop (`ManualAuto`) first.
- Evidence on exact RZ09-0581 host metadata: `hasFanStopTrialEvidence` and `hasFanStopSourceEvidence` true, `unavailableReason` 3 (`GuardianUnavailable`), and `supportsFanStop` false. Other compositions keep the no-evidence status (reason 1).
- Fan Stop settings and the Battery Saver flag go through `Storage::commit`, the one aggregate transaction that `save_profiles` and the thermal policy save also use. Do not add a second store.
- Replies and statuses are recorded through managed (`ServiceControlRuntimeTests.NativeFanStopVectors`, `tests/data/fan-stop-vectors.json`) and replayed by `readiness_tests/fan_stop.rs`. Regenerate the file with `OPENBLADE_REGENERATE_NATIVE_FAN_STOP_VECTORS` set to the data directory. Do not hand-edit it.

## Tried and ruled out

- Seeding a quarantined aggregate before `Rig::new` fails: the rig's `Storage::open` refuses to start over a preserved aggregate. Tamper after the first `get()`, as the thermal policy tests do.
- `Storage::open` returns an error, not a quarantined store, when a recovery record exists. A restart test must expect the open to fail.
- Fault::Stranded applies only to the staged replacements (writes 0-6). On the record removal (write 7) it means something else.

## Watch out

- The private projection (saved settings and automation) shows only after this caller's own Fan Stop or thermal request has retained the store. Until then the status shows managed's defaults. The replay compares `initial` with those members reset.
- Under triggers plus fan-stop, the owner projection reads preferences 7 times rather than 5 (`only_the_owners_view_reads_the_saved_policy` is cfg-adjusted).
- `cargo fmt --check` already flags `readiness_tests/request_triggers_lifecycle.rs` on the base. Format only your own files with `rustfmt --edition 2024 <files>`. `cargo fmt --all` rewrites that file too.
- The `unclassified_external::fans` and `tfc_release` tests fail under load when other agents share the cargo target. They pass in isolation.

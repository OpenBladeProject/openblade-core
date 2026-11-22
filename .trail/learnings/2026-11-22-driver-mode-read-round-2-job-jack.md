---
title: "Driver-mode read round 2: job body, explicit instants, pool cost"
author: Jack
date: 2026-11-22
branch: feat/native-driver-mode-read
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/device_mode_guard_tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/device_mode_tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/worker.rs
  - tools/openblade-windows-host/src/native_package.rs
  - tools/openblade-windows-host/src/service_status/object_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode.rs
---

## Decided
- Supersedes two claims in `2026-11-22-feature-gated-read-only-keyboard-driver-jack.md` (left unedited): the transaction cost and the wall-clock one-instant test. Queries ride jobs that already spend a pair, so the 127-pair pool runs out after about 15m45s at one job per 15 s, 10m30s in Custom and about 7.9 min with a read every 5 s. Neither package reader may admit `service-keyboard-driver-mode-read-v1` before the pool can be reclaimed.
- `Reader::job` is the production job body of every admitted composition, so `performance/job_tests.rs` pins it in every lane: no traffic on an unbound port, retirement always (the Reader ends sealed), and a reading dropped when retirement ends past the deadline or admission is lost by then.
- Retirement keeps no deadline (`retire(None)`), as the base `run()` did before the refactor. A canceled GET that retires more than ~500 ms late costs that job's thermal read; documented, not changed.
- `ReadSource::observe_at(.., job)` takes the job instant; the rig drives an explicit `clock`, so cadence tests never depend on elapsed time. The first job of the one-instant test starts an interval in the past, which separates the job instant from any later `Instant::now()`.
- The guard (`device_mode_guard_tests.rs`) counts `RazerReport::create(` and `encode_report(` per non-test file, refuses `as` renames, and skips only `tests.rs`, `*_tests.rs` and `tests`/`*_tests` directories. Fixture names are not test modules: `fan_stop_fixture.rs`, `configuration_writer/fixture.rs` and `windows/fixture_admission.rs` are production.

## Tried and ruled out
- A wall-clock `hold` in the keyboard fixture for the one-instant test: correct code then depended on a scheduling gap below the margin.
- Telling a refused `Worker::start` apart from an asked job that returned nothing: both return `Ok(None)`, and recording null fails closed, so it is documented instead.

## Watch out
- The Rust reader's marker-name refusal is redundant with its exact capability strings (an extra marker matches no admitted composition), so its mutant survives; readers that predate the feature rely on the marker, which `driver_mode::tests` now pins in the compiled identity.
- The guard is textual: a glob import or a macro reaches an encoder unseen.

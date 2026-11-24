---
title: "Native MCU budget counts since resume, capped"
author: Jack
date: 2026-11-24
branch: feat/native-mcu-version
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/mcu_version.rs
  - tools/openblade-windows-host/src/origin_lifetime.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mcu_version.rs
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_rig.rs
---

Supersedes the budget claims in 2026-11-23-native-mcu-version-read-on-the-jack.md: that the three attempts count "under one `ReadAdmission`" and that "a new admission (resume) restores it". A `ReadAdmission`'s lifecycle epoch advances at every session change and power notice, not only at resume, so that rule refilled the budget at each lock, unlock or AC notice.

## Decided
- The MCU budget counts since the last resume, keyed by the low-power gate's generation (`Lifecycle::resume_generation`, reached through `ReadCustody::resume_generation`). The generation advances only at suspend and resume (and a display-on that recovers a missed resume), so between two admitted reads it differs only across a resume. That matches managed, which resets `mcuVersionReadAttempts` only in `BeginResumeValidationAsync` and on confirmed device loss.
- One read source sends at most `MAXIMUM_SENDS` = 12 queries (four budgets) for its life, whatever the resumes. A query that gets no terminal reply leaves its ID quarantined for the process (`Transactions`), and the pool has 254 IDs. Managed has no cap; the docs say so.
- No separate device-loss reset: a native lost lease ends the read source for the process.
- The gate is read only while a query may still be sent: `Host::mcu_query` and `Inventory::query` take a closure, called after the Absent, known-version and cap checks, so a known version costs no lease call per observation.
- The 00/87 query rechecks `EXCHANGE` after `reserve_pair` and before `sends.sending()`, as `fan_rpm` does before each SET, so a slow entry checkpoint leaves the ID unsent and free.

## Tried and ruled out
- Keying the budget on the whole `ReadAdmission`: its lifecycle epoch is a fence for every session and power notice. With a firmware that never answers 00/87, each fence spent three more quarantined IDs, and roughly 60-80 fences would starve the pool for thermal reads and mutations.
- Filtering `resume_generation` to non-suspending generations: equivalent, since a suspended job cannot authorize and sends nothing, so the filter was dropped.

## Watch out
- `driver_mode_rig::Custody::new()` now carries `lifecycle` (the epoch, which `fence` advances in the MCU tests) and, under live readings, `resume` (the gate, which `resume` advances by 2). Model a lock or AC change with the epoch only, and a resume with both.
- The fake's `mcu_edit` now recomputes the report checksum unless it edits the checksum byte (`CHECKSUM`), so each edit case fails on its own field. The checksum case is separate.
- `a_slow_entry_checkpoint_that_spends_the_exchange_sends_no_set` runs on the wall clock (a 900 ms `slow_verify` sleep on the worker thread) because the paused clock cannot advance inside a synchronous Port call.
- Mutants for this round: moving `sends.sending()` after the SET, dropping or weakening the `EXCHANGE` recheck, keying on the lifecycle epoch, ignoring the generation, the cap's bound and constant, and reading the gate before the known-version or cap checks are all killed.

---
title: Native MCU version read on the read job
author: Jack
date: 2026-11-23
branch: feat/native-mcu-version
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/mcu_version.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mcu_version.rs
  - tools/openblade-windows-host/src/windows/scm/installed/firmware_versions.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - rust/crates/openblade-protocol/src/mcu_version.rs
  - tools/openblade-windows-host/tests/data/mcu-version-vectors.json
---

Supersedes the McuVersion claim in 2026-11-23-native-firmware-versions-managed-record-mcuversion-jack.md ("McuVersion is always `Unavailable`" and "the one field left for a reviewed native MCU query"). The native Service now reads it.

## Decided
- Evidence: the 00/87 query is pinned only for RZ09-0581 1532:02E0, by the captured exchange in `evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-mcu-version-fixture.json` (tid 02, payload length 4, reply 1.5.0.0). RZ09-0528 has only summary evidence (`mcuQuery` in its evidence.json) and no native Reader, and the managed experimental battery-read evidence is null for the source-reviewed models, so all of them stay `Unavailable` natively. The admission is the performance Reader's exact identity (`reviewed_selection`); no separate model gate exists or is needed.
- The query is the read job's last (after thermal, device mode and fan speeds), with its own 1 s budget and the job's 500 ms reserve, so a failed query costs no other reading. `McuVersionRead` is NotRequested / Unconfirmed / Confirmed; only Confirmed is kept.
- Budget: managed `MaximumMcuVersionReadAttempts` = 3, counted as sent queries (`McuQuerySends`, incremented on the worker before the SET) under one `ReadAdmission`; a new admission (resume) restores it. Managed restores the attempt of a canceled read; native counts a sent query whose observation was dropped. Native has no device-loss reset because a revoked lease ends the native reader.
- The installed source asks only when the firmware Host has a source (`State::Absent` means no query, as managed reads no version without a record), the version is unknown and the budget lasts. `Host::poll` applies the known version to the Done record, so a version read before the record is published rides its first publication and a later one bumps the revision and republishes. `Host::none()` frames never change.
- `observe_at` records the version only after the reading passes the source's own admission and deadline check.
- Parity: `mcu-version-vectors.json` is written by the managed `NativeFirmwareVersionMcuVectors` test (requests, 23 reply shapes through `McuVersionProtocol`, 11 budget rows with resume) and replayed by the protocol codec test and the installed budget test.

- Mutants: 24 manual mutants over the Inventory budget, the Host gate, the Reader gates and checkpoints, the job wiring and `observe_at` are all killed. The firmware rig's `worker_admission` pin models a device change after the Reader's last checkpoint, so `a_version_the_source_does_not_accept_is_not_kept` covers the acceptance check that the checkpoint after decode otherwise hides. cargo-mutants is not installed in the pinned toolchain.

## Tried and ruled out
- Relying on the admission-change-during-query test to cover `observe_at`'s acceptance check: the Reader's checkpoint after decode rejects that change first, so a mutant recording the version before the check survived it.

## Watch out
- Under `service-keyboard-driver-mode-read-v1` the job also sends 00/84, so never assert that a job sends only class 0D; count 00/87 (`FanIo::mcu_queries`).
- Tests that wait for the query's SET must be bounded (`until_queried`): a mutant that sends no query otherwise hangs the test binary.
- The fake's `hold_mcu` blocks the SET, not the reply; `stall_mcu` stalls the reply.

---
title: Resolve handle timing from original timestamps in the saved trace
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-mailbox-calibration.md
  - docs/so6120-mailbox-trigger-test-plan.md
---

## Decided

When a UTC-to-QPC projection straddles the native open/close boundaries, replay the saved ETL with EVENT_RECORD and RAW_TIMESTAMP instead of repeating the live operation. Require the original QPC clock/frequency, input/output hashes, complete zero-loss replay and supported payload schemas. Compare ordered Object payloads with the existing export. Match PID, handle, File type and object within the original native call intervals, retaining reuse and duplicate checks.

## Tried and ruled out

Do not loosen timing windows, borrow a name from a later lifetime, or treat an unrelated object type's reused handle as the target. EVENT_HEADER.Size describes the logged record, not the size of the EVENT_RECORD callback wrapper: a sizeof(EVENT_HEADER) floor rejects valid compact/classic events. Validate the fixed callback ABI and UserDataLength/pointer contract, then the provider payload. Preserve failed offline output and the original inconclusive UTC result.

## Watch out

The controlled InpOut lifetime had an empty create name and exact device name on close. This proves one controlled zero-access lifetime was detectable; unnamed handles, other access modes and independent firmware access remain outside the finding. A named positive control does not establish mailbox exclusion or qualify a longer action-window budget. File replay includes a synthetic trace-header callback; compare actual provider counts before treating a one-event total difference as missing evidence.

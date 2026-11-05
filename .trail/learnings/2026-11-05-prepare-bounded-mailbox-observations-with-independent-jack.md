---
title: Prepare bounded mailbox observations with independent offline controls
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - tools/mailbox-observation
  - docs/so6120-mailbox-trigger-test-plan.md
  - ".github/workflows/firmware-tools.yml"
---

## Decided

Prepare caller-challenge tools under tools/mailbox-observation without running a new capture. Recording defaults to plan-only, requires explicit attended execution, owns uniquely named ETW sessions, and bounds setup, collection, disk usage and cleanup. Only System Object Handle and Kernel-Acpi providers are enabled. Physical stimuli remain operator actions.

Bind offline conclusions to actual input bytes and independent controls: export existing ETLs atomically with hashes, validate native stage results and loss counters, resolve File type from each trace, match the held rundown and paired transient close by PID/handle/type/run-ID suffix, and recompute QPC/UTC timing against the shared interval and planned deadline. Relative marker names survive copying the entire run folder. Hash the exact parsed manifest bytes rather than reopening its path.

## Tried and ruled out

Replaying the saved Object and ACPI ETLs reproduced all provider counts and the held-file control. EventLogReader omits the ETL-header pseudo-event; that one-record difference is not data loss. Version-3 duplicate-handle records can safely taint an object join through their common object prefix, but do not justify guessed process attribution. Unknown layouts remain explicit coverage failures.

The pinned offline ACPI image inspector locates one Event 7 descriptor-byte candidate and 2,043 unwind intervals. This does not decode emitter call references or prove external/nested method coverage. Do not infer coverage from ordinary ACPI activity.

## Watch out

Captured is collection status, not a clean result. Failed calibration, missing exports, lost records, incomplete cleanup, late operator marks or failed marker controls remain inconclusive. Quiet observations never automatically grant direct-PWM write authority or a collision probability. The finite caller-challenge review can still accept low practical contention concern with explicit residual risk; these tools do not add an endless absence-proof requirement.

Offline validation passed 39 Python tests, 13 fake recorder tests and 13 exporter assertions. No new live ETW session, UAC, vendor execution, AML evaluation, driver/device call or hardware change was performed. Raw ETLs and process/path identities stay in ignored local artifacts.

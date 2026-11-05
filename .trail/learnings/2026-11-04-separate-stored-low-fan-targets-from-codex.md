---
title: Separate stored low fan targets from measured fan behavior
author: Codex
date: 2026-11-04
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Protocol/BatterySaverLowFanValidationRunner.cs
  - src/OpenBlade.Protocol/BatteryValidationFactory.cs
  - src/OpenBlade.Capture/BatterySaverLowFanValidationCommand.cs
  - build/Invoke-BatterySaverLowFanValidation.ps1
  - docs/battery-saver-low-fan-validation.md
  - docs/evidence/battery-saver-1500-rz09-0581-2026-11-04.json
---

## Decided
Keep the explicitly authorized RZ09-0581 Battery Saver 1500 RPM experiment in a named developer-only path, separate from production range admission. Pinned Product 736 source establishes class-1 stored-target GET/SET and RPM/100 encoding. The 2026-11-04 BIOS 4.01 battery round trip confirmed stored targets 1500/1500 and paired BatterySaverManual, but all 30 actual-RPM samples stayed 0/0 from an already 0/0 Automatic baseline. Restore both original stored targets (3800/3800) and exact paired BatterySaverAutomatic, then independently confirm restoration and saved installed intent. This result grants no lower normal-control range or capability on another model.

## Tried and ruled out
An app constructor rejecting 1500 is not a firmware rejection. An ACK or stored target is not a measured RPM. An unchanged zero-RPM baseline does not prove that 1500 caused fan stop, so it cannot justify a universal fans-off feature. SCM Stopped can precede process exit: retain the original service process handle, wait boundedly for that exact owner to exit, and refuse recovery startup if it remains alive.

## Watch out
Admission before a durable intent can become stale. Recheck exact host/BIOS, battery, active console and ownership through RazerCommandSpec.SubmissionAllowed at native write submission; bound the candidate CPU reading age. After ambiguous submission/readback stop all further writes, including restore, and report unresolved state. Exact-model source and physical behavior remain separate; the sanitized result is docs/evidence/battery-saver-1500-rz09-0581-2026-11-04.json. Trail check lexical overlaps with older factory/stream/experimental-model notes do not reintroduce those behaviors: this path adds no retry or fallback, keeps current 0581 echo contracts, and is absent from installed service IPC.

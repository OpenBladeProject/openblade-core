---
title: RZ09-0529 LampArray hazard and LED38 probe settling
author: Jack
date: 2026-11-20
branch: feat/rz09-0529-vapor-chamber-frames
touches:
  - docs/rz09-0529-lighting-expansion.md
  - evidence/source-reviewed-blades/2026-11-13-complete-lighting-pass.json
  - evidence/source-reviewed-blades/2026-11-13-rz09-0529-keyboard-frames.json
  - src/OpenBlade.Protocol/ExperimentalBladePort.McuLighting.cs
  - src/OpenBlade.Protocol/ExperimentalLightingBaselineJournal.cs
  - src/OpenBlade.Protocol/ExperimentalVaporChamberAvailability.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeFactoryTests.Rz090529VaporChamber.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalLightingBaselineJournalTests.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.Rz090529VaporChamber.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.cs
---

## Decided
- KI8110 (RZ09-0529 MCU 1.07.00) has the same Windows Dynamic Lighting hazard as RZ09-0509/0510, and it also covers LED38. AutonomousMode is 0x100001C9 (default 1). Copy helper 0x4B80 shows the LampArray buffers (0x10001264 keyboard, 0x1000145C VC) instead of staging (0x10001B80, 0x10001CD6) when AutonomousMode is 0 and policy 0x10000179 or 0x10000188 is 0. 0F/02 mode 8 (0x9CBC) never checks it, so 0F/82 still reports 8. The LampArray handler is 0xA784. Updates force mode 8, set the flag and persist; release forces a persisted Spectrum. Admission was left unchanged for the maintainer to decide.
- The LED38 probe and the optional frame read settle availability only on a device answer. A shared-gate refusal (Exhausted/SharedGateAdmission), a queued or in-flight cancellation, Disposed, Poisoned or a Guard-phase refusal never reached the MCU. They settle nothing, so the next open or request reads again.
- An LED38 journal record for any product other than 0x02C7 makes the journal invalid. The LED38 restore returns early for any other model.

## Tried and ruled out
- Searching for the 0509 shape (ldrb global, cmp #0, strb #8 at +0x14) found only the 03/0A-style setter. Follow the callers of the copy helper and the persist routine (0xABE0) instead.

## Watch out
- In the factory, query recording is enabled before the probe runs. Probe observations therefore land in factory.GetDiagnostics(), not in LastAdmissionFailure.
- The KI8110 initialised data is uncompressed: the scatter table at 0xFA68 maps 0xFA88 to 0x100000C0 (0x110 bytes).

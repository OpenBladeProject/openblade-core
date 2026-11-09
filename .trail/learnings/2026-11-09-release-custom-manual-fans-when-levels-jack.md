---
title: Release Custom manual fans when levels cannot be restored
author: Jack
date: 2026-11-09
branch: fix/rz09-0581-manual-fan-power
touches:
  - src/OpenBlade.Protocol/ProductionBatteryPort.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
---

## Decided
Claude's review of ee5d876e caught two thermal edge cases. Keep a confirmed Custom/manual selector reportable if both level bytes decode to the unadmitted High/High combination, while leaving the levels unknown. If a fan-only handback cannot reapply the exact Custom levels, release to the admitted Balanced/Automatic pair. Manual entry continues to require restorable settings.
## Tried and ruled out
Do not continue a multi-write preset change after adapter admission is lost. The two Balanced restore action names resolve to the same wire pair. Explicit user Automatic restores saved intent through the coordinator; the fan-only cleanup path should not reapply a deferred user preset.
## Watch out
High-power mode-7 exits through the ordinary preset setter require fresh full adapter power before every write. Limit additional preset restoration before manual fans to the direct shared-selector backend; broadening it to legacy recovery added unexpected writes in three service tests.

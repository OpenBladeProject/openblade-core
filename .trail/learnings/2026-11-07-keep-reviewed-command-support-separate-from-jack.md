---
title: keep reviewed command support separate from temporary read failures
author: Jack
date: 2026-11-07
branch: feat/official-source-reviewed-support
touches:
  - docs/application-profiles.md
  - docs/diagnostics-coverage.md
  - src/OpenBlade.Contracts/Profiles.cs
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.TemporaryFullCharge.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Service/BatteryOperationPlanner.cs
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/PendingWriteConfirmationTracker.cs
  - src/OpenBlade.Service/ProfileStore.cs
---

## Decided

Keep reviewed command capability separate from temporary read availability. RZ09-0484 has an admitted temporary-full-charge query and setter even before its first successful GET. Advertise that stable capability; nullable confirmed state blocks planning and UI until the real baseline is available. Its setter still freshly reads the actual limiter and complete Power Mode Control byte before every write.

Limiter-only profiles retain an unknown override value. Ordinary policy confirmation is explicitly field-scoped, rejects a known active override, and cannot narrow an earlier All obligation. Real override and cleanup operations retain strict independent flag confirmation.

## Tried and ruled out

Masking SupportsTemporaryFullCharge after a failed or missing GET makes a query-capable backend look like a limiter-only backend. Do not infer query absence from that transient state or synthesize an Off value. Removed the cached read-admission flag instead of adding another wire field or model-specific UI condition.

## Watch out

This changes capability reporting, not the admitted command set or per-write guard. Source-reviewed status does not claim physical recovery. Trail check's lexical matches against prohibited guessing, physical-proof and recovery phrases were reviewed in both original and follow-up ranges: the implementation preserves null, strict override scope and fresh reads rather than reintroducing those prohibited behaviors. The complete branch exceeds the checker's 400 KB diff cap, so check the smaller follow-up range as well as the original implementation slice.

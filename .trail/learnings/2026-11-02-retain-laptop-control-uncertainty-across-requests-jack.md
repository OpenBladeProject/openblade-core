---
title: Retain laptop-control uncertainty across requests and restart
author: Jack
date: 2026-11-02
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - src/OpenBlade.Protocol/BatteryValidationFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ProductionBatteryFactory.cs
  - src/OpenBlade.Service/IVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Service/ServiceComposition.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Tray.Core/TelemetryChartViewport.cs
  - src/OpenBlade.Tray.Core/TelemetryHistory.cs
---

## Decided
Return typed application progress from full profile transitions: durable persistence, attempted writes, confirmed work, deferred work and unresolved submission are independent facts. A saved profile stays Stored when application is incomplete. A context operation that attempted a write but observed a different value is Failed, without pretending uncertainty remains. Carry HyperBoost boundary progress through later interruption or failed readmission.

Share typed laptop-control execution between explicit controls and profile automation. Persist a bounded machine/model-bound intent before entering the setter. Unknown completion, cancellation or missing confirmation keeps the marker across service restart. Definite differing readback resolves uncertainty but retains a per-control target tombstone against automatic replay; an existing explicit typed request may retry after ordinary read-only observation. Host-managed Gaming Mode remains a Session Agent preference. Microphone indicator has no getter: admitted successful setter completion keeps its existing limited meaning, but ambiguous submission remains paused rather than inventing recovery.

Only the ordinary observation path reconciles pending laptop controls. Capture pending state at the operation boundary, publish fresh read-only status and return before lighting, thermal or deferred recovery. Moving that capture inside ObserveCore was too late when controller refresh had already cleared the record. Keep required fresh admission, serialization and publication in the runtime; the helper grants no hardware authority.

## Tried and ruled out
Returning Applied after awaiting a void transition lost early exits. Catching each laptop-control failure and proceeding to the next setter allowed cross-control replay through newly opened ports. Port-local poisoning and process restart cannot establish an unknown outcome. A fresh baseline getter for every ordinary request was unnecessary; reconciliation reads only pending controls. InvalidDataException is not caught by an IOException filter, so malformed-size/schema cases need explicit coverage.

## Watch out
Keep saved-profile schema and IPC versions unchanged: the recovery record is new internal protected service state. Preserve known-not-applied classification and stop before submission if journal persistence fails. Full transition callers with later mutations must consume stop outcomes. Test recovery with a faulted renderer and retained lighting ownership, not only empty fixtures.

The November audit Trail check reported lexical overlaps rather than semantic reversals. The version is recalculated from fetched main 0.50.3 to0.50.4; historical version examples are not pins. Changelog replay refers to CSV selection and retained uncertainty. Hashing is read-only. Recovery metadata adds no locale key or capability. Observation intentionally performs fresh admitted read-only reconciliation, while UI state reads remain GetStatus. HyperBoost progress accounting neither maps HyperBoost to Custom nor changes contracts. Missing readings remain unknown. The shared laptop-control defects are reproduced in software; this does not claim reproduction of earlier GPU-slider startup or Lighting-Off owner reports.

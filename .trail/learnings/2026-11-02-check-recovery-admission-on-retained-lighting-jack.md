---
title: Check recovery admission on retained lighting submissions
author: Jack
date: 2026-11-02
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - src/OpenBlade.Protocol/BatteryValidationFactory.cs
  - src/OpenBlade.Protocol/BladeWriteException.cs
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/IProductionLightingPort.cs
  - src/OpenBlade.Protocol/Pid02E0MatrixReport.cs
  - src/OpenBlade.Protocol/ProductionBatteryFactory.cs
---

## Decided
Apply the service's laptop-control recovery admission at every retained lighting submission, including native queued 02E0 feature writes and 02C6/experimental rows and latch commands. A check only at IPC entry or before opening a session does not cover a worker that keeps its own port. Driver-mode cleanup keeps its separate low-power contract; a recovery pause must not trigger restoration or reset writes.

Keep PC names as diagnostic provenance, not recovery identity. The protected machine-local journal and exact model/HID contract define scope; renaming a PC must not defeat freshly admitted readback. When this process proves a command was never submitted but journal rollback fails, retain process-local proof bound to the exact intent hash and prior record. Retry storage cleanup only. A restart loses that proof and must fail closed; a replacement record must never be cleared using stale proof.

## Tried and ruled out
Port-local poisoning cannot pause another retained port. Faulting or disposing an otherwise healthy stream on a known pre-submission admission refusal creates unnecessary reactivation and may itself invoke restoration. Check again at actual native submission, and distinguish zero submission from partial batches. Do not weaken uncertainty for a write-only microphone command or silently discard corrupt/unknown recovery records.

## Watch out
Tests must revoke admission after queueing/opening, not only at method entry, and cover both acknowledged and SET-only frame paths. Preserve original report bytes, exact-model admission and independent readback. An admission predicate is software gating, not a new firmware capability or physical validation claim. Result facts describe the current operation; a historical pending record blocks a new request without pretending that request submitted a write.

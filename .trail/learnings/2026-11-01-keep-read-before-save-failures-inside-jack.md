---
title: Keep read-before-save failures inside command reporting
author: Jack
date: 2026-11-01
branch: fix/codebase-reliability-audit
touches:
  - src/OpenBlade.Capture/ExternallyLaunchedHyperBoostWorkloadSource.cs
  - src/OpenBlade.Capture/StockSteelNomadWorkload.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Protocol/ProductionLightingEvidence.cs
  - src/OpenBlade.Service/PadlessHyperBoostLifecycleValidationBuild.cs
  - src/OpenBlade.Service/ProfileStore.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/ThermalExternalDriftValidationBuild.cs
  - src/OpenBlade.Session/DisplayProfileApplier.cs
  - src/OpenBlade.SessionAgent/ApplicationProfileReporter.cs
---

## Decided
Keep read-before-save reconciliation failures inside the same user-visible error reporting path as profile mutations. A failed read must retain reconciliation-required state, release the persistence gate and return a refused save; cancellation remains observable. Otherwise a command task can retain an IOException and later interrupt MainViewModel disposal. Import validation assigns its own current failure before the window displays OperationStatusMessage. Queued gaming saves recheck the power source before constructing their candidate.

## Tried and ruled out
Do not clear reconciliation-required merely because a save returned Stored: the acknowledgement has no status revision, and an older buffered subscription can still replace the local profile document. Do not treat a failed status read as unsupported profile settings or swallow all unexpected AsyncRelayCommand faults as a substitute for fixing the new throwing path. The delayed-status regression requires a real metadata difference; a revision-only update correctly takes the live-readings shortcut.

## Watch out
The gate covers candidate construction, persistence and confirmed local state. Reconciliation uses GetStatus and never replays an uncertain save. Keep queue cancellation and client disposal ordering intact. Four Core resource catalogs carry the same two new feedback messages; import wording reuses the established WinUI translations. The final internal review verified the failure path separately from Claude's earlier frozen patch review.

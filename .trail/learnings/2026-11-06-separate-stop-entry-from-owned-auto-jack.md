---
title: Separate Stop entry from owned Auto cleanup
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - docs/so6120-supervised-fan-stop-trial.md
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/ServiceMessages.cs
  - src/OpenBlade.Protocol/ProductionBatteryFactory.cs
  - src/OpenBlade.Service/ControlRequestHandler.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/ServiceStatusSnapshotComparer.cs
---

## Decided
For the gated Stop/Auto controller, separate Stop-entry admission, explicit caller admission, and mandatory confirmation/owned Auto cleanup. A preparing low-power or shutdown boundary must prevent new Stop entry while allowing a resolved Stop to be confirmed and handed back in the same request. Keep hard hardware uncertainty fences. Give Auto its own cooperative budget after the required keyboard release attempt; do not claim a native time bound. Use a one-way runtime shutdown latch that user activation cannot reset.

## Tried and ruled out
Reusing the Stop-entry callback for post-submission confirmation can strand an otherwise resolved operation in recovery. Advancing normal fan ownership only after a returned Applied result misses confirmed Stop followed by cancellation. A generic WritesBlocked bypass for cleanup would weaken unrelated uncertainty fences. Reading durable-looking bytes after restart is not proof of ownership; no live journal reader, takeover or guardian is implemented.

## Watch out
IFanStopBackend requires command-free startup ownership disposition; unknown or unresolved starts fenced before startup controls. A later unowned direct Stop observation also fences without adopting ownership. Check caller and entry authority again before the first mapped A0; mandatory confirmation remains independent of the vanished caller. Periodic unavailable-backend work must return without taking the gate, and idle observation must not queue behind transitions. Record confirmed generations and final Auto/recovery causes, including same-call cleanup; history is not durable crash recovery. Status projection must preserve unchanged snapshot identity, and confirmed Auto must override stale normal fan presentation while preserving saved manual intent. These are simulated/shared-software guarantees; production Stop remains disabled.

---
title: Fence the managed owners before the Windows Rust handoff
author: Jack
date: 2026-11-09
branch: feat/rust-managed-owner-handoff
touches:
  - docs/windows-backend-replay.md
  - src/OpenBlade.Protocol/BoundedValidationActorDisposal.cs
  - src/OpenBlade.Protocol/PendingDisposalRegistry.cs
  - src/OpenBlade.Protocol/ProductionBatteryFactory.cs
  - src/OpenBlade.Protocol/ProductionBatteryPort.cs
  - src/OpenBlade.Protocol/ProductionLightingFactory.cs
  - src/OpenBlade.Protocol/ProductionLightingPort.cs
  - src/OpenBlade.Protocol/RazerCommandSessionGate.cs
  - src/OpenBlade.Service/ProductionLightingSession.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
---

## Decided
- Phase 3e uses an injected Windows provider scope and the real managed control and retained lighting owners. The durable marker fences ordinary admission before drain. Handback needs the retained exact child handle to signal, a fresh matching typed adapter-power read, and terminal native cleanup.

## Tried and ruled out
- The shared feature semaphore alone cannot prove terminal handle closure. An expired caller or a renderer disposal deadline leaves evidence unresolved. A restarted provider cannot reconstruct a lease from a marker.

## Watch out
- Managed tests use exclusive files and a private child lifetime. PR #920 separately covers Rust pipe and job semantics. Before physical activation, bind the strict native authority, pinned child and protected startup storage in one private supervisor and retarget the stack to current main with its ACL hardening.

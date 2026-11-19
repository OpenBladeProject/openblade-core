---
title: Bound experimental lighting transactions and idle traffic
author: Jack
date: 2026-11-12
branch: feat/issue945-lighting-parity
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.GridLighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - tests/OpenBlade.Protocol.Tests/Blade18SpatialPortTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeGridLightingTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeLightingTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeFrameBudgetTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalLightingCadenceTests.cs
---

## Decided
Experimental Blade lighting must distinguish continuous production transactions from the finite developer ValidationRunContext. A model-specific fixed frame ID applies only to production. Developer custom rows and activation always consume the unique run budget; exhaustion after any submitted row is indeterminate and pauses further writes. Existing 029F production frames retain the rolling allocator.
Uniform frames across 029F, 029D, 02C5 and 02A0 share the 50 ms software budget, with cancellation preserving successful cadence state. Reactive/Ripple poll at 50 ms for input, suppress unchanged frames, and send a deliberate five-second software keepalive. Update dedup and telemetry only after success.
## Tried and ruled out
Do not sleep for the full idle keepalive: pulse input currently mutates memory without waking the loop. Do not republish skipped frames to the keyboard mirror: its latest value persists without a TTL, and changing its sequence causes unnecessary cooling-pad writes. Writable effects need not appear in the firmware-observed list, which records evidence provenance.
## Watch out
Seed dedup from the successful initial frame, emit the changed final black frame once, and preserve keepalive deadlines across deferred writes. Ambiguous submission faults ownership and clears the mirror without replay. RequiresLightingRendererOwnership describes the backend; it is not capability admission. Unsupported settings must reject before stopping an active renderer. Exact model transport, row geometry and ACK versus SET-only behavior remain separate.

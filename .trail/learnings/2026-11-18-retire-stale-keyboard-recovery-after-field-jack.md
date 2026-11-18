---
title: Retire stale keyboard recovery after field-only lighting
author: Jack
date: 2026-11-18
branch: fix/pr-977-lighting-review
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.IndependentLighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WriteConfirmationPolicy.cs
  - src/OpenBlade.Tray.Core/LightingEditorState.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.Rz090529Frames.cs
  - tests/OpenBlade.Service.Tests/IndependentLightingControlTests.cs
  - tests/OpenBlade.Service.Tests/Rz090529IndependentZoneTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.IndependentLighting.cs
---

## Decided
An accepted unmanaged lighting result supersedes deferred keyboard-effect recovery. Use the same field-only classification for scoped and profile paths, including wrappers without independent fields and hybrid ports with no offered effects.

## Tried and ruled out
A missing requested logo stays unconfirmed, and brightness seeded during effect selection carries no edit authority. The same positive combined brightness uses the exact hybrid independent conversion so one physical raw change suffices.

## Watch out
Keep cancellation and indeterminate writes distinct from successful field reconciliation. An exact validated outside-matrix 0528 request is not produced by the native input map.

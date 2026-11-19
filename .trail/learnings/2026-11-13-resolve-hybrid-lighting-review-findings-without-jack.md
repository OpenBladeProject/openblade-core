---
title: Resolve hybrid lighting review findings without weakening MCU admission
author: Jack
date: 2026-11-13
branch: fix/issue959-lighting
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.McuLighting.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.SessionAgent/SpecialKeyActionExecutor.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.McuLighting.cs
  - tests/OpenBlade.SessionAgent.Tests/SpecialKeyActionExecutorTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.LightingIntentComparison.cs
---

## Decided
Claude reviewed PR #960 at exact head 1f7d1684 with Opus 5.5/high in a read-only isolated snapshot. Explicit Session Agent cycle-effect requests must set ControlsKeyboardEffect so hybrid models dispatch the selected effect rather than preserve unmanaged intent. A valid MCU builtin at exact raw brightness zero reports unknown complete lighting while preserving confirmed independent brightness; malformed payloads and nonzero raw values rounded to zero still reject. Firmware-versus-intent comparisons ignore the authority marker; saved-intent-versus-saved-intent comparisons retain it.

## Tried and ruled out
The missing Wave speed argument was not a defect: SO6100 A668/A66A, KI8100 9D04/9D06 and KI8110 9B70/9B72 synthesize and store 40 internally. Do not copy OpenRazer's argument shape over those exact MCU contracts. ProfileAutomationState startup equality compares remembered intent, not firmware state; removing the marker there would discard an explicit saved effect selection. Shared predicate/JSON-walker refactors were maintainability suggestions, without a demonstrated routing or migration defect.

## Watch out
Other firmware effect modes and non-40 Wave speeds remain outside the admitted full-state contract, even where getter branches exist. A blanket unknown fallback is unsafe: same-mode/direction Wave shortcuts can retain an unadmitted speed. Separately review payload bounds and transition cleanup before expanding that admission. Bridge/destination confirmation before brightness wake and legacy brightness refresh after saved-bank activation have concrete safety purposes; do not remove them as duplicate reads. These are source/software results, not physical validation. PR #960 is merge-only; do not dispatch Nightly as part of completion.

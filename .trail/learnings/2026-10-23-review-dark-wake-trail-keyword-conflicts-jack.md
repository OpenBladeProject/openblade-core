---
title: Review dark-wake trail keyword conflicts
author: Jack
date: 2026-10-23
branch: fix/lighting-wake-flash
touches:
  - Directory.Build.props
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ProductionLightingPort.cs
  - src/OpenBlade.Protocol/Rz090528BrightnessLightingFactory.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeLightingTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeWakeLightingTests.cs
  - tests/OpenBlade.Protocol.Tests/ProductionLightingPortTests.cs
---

## Decided
Reviewed the twenty trail-check matches for fix/lighting-wake-flash on 2026-10-23. They are lexical matches, not conflicting behavior: the patch stages content before brightness only on confirmed dark wake, preserves explicit readback and failure recovery, and changes no model admission, driver/catalog ledger, thermal behavior, query-content classification, display automation, caption rendering or capture provenance. The product version increments 0.41.1 to 0.41.2 as required for a defect fix; this does not prepare, dispatch or publish a Nightly.

## Tried and ruled out
The checker matches explanatory comments about brightness-first wake to the newly added learning that rejects brightness-first wake. It also matches generic fixture words such as model, raw, state, failure and color to unrelated hardware and UI learnings. Reversing the fix or deleting admission/readback checks to satisfy those matches would be incorrect.

## Watch out
Review semantic source changes and focused tests when trail reports these matches. Do not infer a physical result from simulated frame ordering or treat a product patch bump as release authorization.

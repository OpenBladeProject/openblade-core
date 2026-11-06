---
title: Trace fixed refresh mismatches to the retained target timing
author: Codex
date: 2026-11-06
touches:
  - src/OpenBlade.Display/WindowsDisplaySettingsController.cs
  - src/OpenBlade.Display/RefreshRateClassifier.cs
  - tests/OpenBlade.Display.Tests/RefreshRateClassifierTests.cs
---

## Decided
Issue #884 shows a 60 Hz desktop and a 240 Hz active signal at 2560 × 1600 with DRR off on the RZ09-0581 Intel Optimus path. In the attached 0.40.0 build, 0.50.3, and main at 2919a1de, the shared Windows controller changes path refresh and boost while keeping the supplied target timing. Fixed-mode readback accepts desktop rate alone. Classify_UsesVirtualRateForFixedMode explicitly covers 60/240 as fixed 60.

## Tried and ruled out
The issue screenshot establishes the two reported rates. The attached diagnostic ZIP identifies 0.40.0+edb5c765 and lacks a display-mode command trace. It does not establish whether Windows Settings can change the active signal on that same Optimus path or quantify battery impact.

## Watch out
Compare a 240-to-60 change made in Windows Advanced display with DRR off in the same Optimus session. A fixed-mode change needs target timing selection and independent active-signal confirmation. Preserve Dynamic availability when switching back from a real 60 Hz signal: BuildRefreshOptions currently uses the active physical rate as the candidate maximum. Keep the serialized single submission and read-only reconciliation on ambiguity.

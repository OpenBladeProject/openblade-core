---
title: Submit the panel timing when changing fixed display refresh
author: Codex
date: 2026-11-06
branch: fix/display-active-signal-refresh
touches:
  - src/OpenBlade.Display/WindowsDisplaySettingsController.cs
  - src/OpenBlade.Display/EdidDetailedTimingParser.cs
  - src/OpenBlade.Display/RefreshRateClassifier.cs
---

## Decided
Fixed refresh on issue #884 must supply the panel's requested target timing when available and require both desktop and signal readback to match. The maintainer confirmed Windows can change the signal on the reported path. For DisplayID Type VII, retain the pixel rate and total geometry, including the encoded clock's plus-one adjustment. A higher Windows preferred timing of the same geometry keeps Dynamic available after selecting a low fixed rate.

## Tried and ruled out
On the RZ09-0581 internal panel at 2560 × 1600, SDC_VALIDATE accepted the original configuration but rejected a 60 Hz request that omitted the target mode with error 87. It accepted the built controller's request containing the panel's explicit 60 Hz timing. The same Windows target reports a preferred 240 Hz timing. CDS_TEST accepted the desktop mode, which already read 60 Hz while the active signal was 240 Hz, so it was insufficient evidence for the physical signal.

## Watch out
Keep validation proportionate. The focused display tests and one validation-only check exercise the regression and the native request. A native validation result is not an applied transition or physical readback. Keep the one submission, independent query and no replay on ambiguous results. The omission path is reserved for cases where no exact target timing can be obtained and must still pass Windows validation.

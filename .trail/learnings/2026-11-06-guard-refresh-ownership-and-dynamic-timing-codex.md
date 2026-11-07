---
title: Guard refresh ownership and dynamic timing after the fixed-rate change
author: Codex
date: 2026-11-06
branch: fix/display-active-signal-refresh
touches:
  - src/OpenBlade.Session/DisplayAutomationCoordinator.cs
  - src/OpenBlade.Display/WindowsDisplaySettingsController.cs
  - src/OpenBlade.Display/EdidDetailedTimingParser.cs
---

## Decided
Claude Opus 5.5 reviewed draft PR #886 at f97c2e8e. The main source-confirmed follow-ups are the application refresh baseline, Dynamic availability when Windows prefers a low timing, and progressive-only timing selection. A mixed 60 desktop and 240 signal now has no fixed-mode key. An application profile must not take refresh ownership from that baseline and then report restoration success after skipping the missing key.

## Tried and ruled out
The current parser returned identical records when the Type VII interlace bit was toggled in a checksum-valid fixture. The scaled-desktop EDID filter already existed at the base commit. The compact mode-table omission probe also failed with error 87 during the fix, so an orphaned array entry did not explain the rejection on that panel. A possible transient readback delay was not observed in an applied transition.

## Watch out
Preserve the pause after a possibly submitted change with ambiguous readback. Windows preferred mode is not a highest-refresh guarantee. A Dynamic option must resolve to a usable same-geometry timing, and advertised progressive rates must not select an interlaced descriptor. Prior validation-only acceptance is not physical switch confirmation.

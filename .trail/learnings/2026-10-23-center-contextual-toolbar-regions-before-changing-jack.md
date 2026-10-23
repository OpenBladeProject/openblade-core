---
title: Center contextual toolbar regions before changing typography
author: Jack
date: 2026-10-23
branch: feat/telemetry-analysis-typography
touches:
  - src/OpenBlade.Tray/ContextualToolbar.cs
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
  - docs/ui-typography.md
---

## Decided
The telemetry source status and CSV commands already used the same native body typography. A native 150-percent render measured the status at y=0 and command text at y=6.6667 DIP, a ten-physical-pixel offset. Center both ContextualToolbar regions at their natural heights so padded commands and short status text align. This shared correction also centers the page toggle, inspection tools and inline range action against their context.

## Tried and ruled out
Changing font sizes, shrinking icons, fixed heights or local offsets would hide the actual stretched-container defect. The earlier physical-pixel width reservation fixes Measure/Arrange wrapping but does not fix vertical placement. Do not modify that width admission or relax overflow checks to solve an alignment issue.

## Watch out
The native regression compares rendered text positions when source and command text have the same height in a shared row, and checks region centers. Multiline recording details and stacked narrow layouts retain their own natural geometry. The new assertion failed the original layout at native DPI and passed after the shared alignment fix in fourteen real WinUI scenarios across 96/native DPI, English/Russian/Chinese/pseudo locales and live/recording/replay/disabled/dark/narrow states. Preserve body text roles, native templates and 32-DIP targets. No Nightly is authorized.

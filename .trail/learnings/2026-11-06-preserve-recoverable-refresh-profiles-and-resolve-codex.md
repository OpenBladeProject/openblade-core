---
title: Preserve recoverable refresh profiles and resolve supported dynamic timings
author: Codex
date: 2026-11-06
branch: fix/display-active-signal-refresh
touches:
  - src/OpenBlade.Session/DisplayAutomationCoordinator.cs
  - src/OpenBlade.Display/WindowsDisplaySettingsController.cs
  - src/OpenBlade.Display/EdidDetailedTimingParser.cs
---

## Decided
PR #886 review fixes gate app refresh ownership on a known baseline. A legacy checkpoint that already owns refresh without a restorable key keeps the unresolved claim and reports blocked while restoring known unrelated fields. The power profile can still apply its refresh rate when an app does not own that field.

## Tried and ruled out
Windows preferred timing alone can be lower than the panel maximum. Dynamic selection now considers same-geometry progressive DisplayID timings corroborated by the driver, keeps the active Dynamic choice when applicable, and resolves a supported saved lower maximum when explicitly requested. The selected timing travels with the option to Windows validation.

## Watch out
Do not interpret Type VII interlaced descriptors as progressive target timings. Readback still requires the actual signal rate. These follow-ups use focused software tests and do not establish a live transition on a panel whose preferred rate is lower.

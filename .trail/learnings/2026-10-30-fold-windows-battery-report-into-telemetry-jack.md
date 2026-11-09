---
title: Fold Windows battery report into Telemetry and retire the preset
author: Jack
date: 2026-10-30
branch: fix/battery-history-readability
touches:
  - Directory.Build.props
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - docs/telemetry-and-battery-history.md
  - src/OpenBlade.Tray.Core/LongBatteryLifePreset.cs
  - src/OpenBlade.Tray.Core/MainViewModel.Battery.cs
  - src/OpenBlade.Tray.Core/PageSections.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/BatteryTimeline.cs
  - src/OpenBlade.Tray/MainWindow.Battery.cs
---

## Decided
Windows battery report belongs in a compact native expander on Telemetry, outside the telemetry-enabled content so on-demand history remains available with sensor collection off. The separate Laptop Battery History card and Long battery life preset were removed at the user's explicit request. Report-only content is collapsed until data is loaded; a failed refresh preserves the last confirmed report.

The report graphic shows three labeled power-source lanes on a proportional time axis, with localized endpoint dates and optional intermediate clock labels. Interval details expose activity, start charge, and discharged energy. StartChargePercent is an interval-start observation, not evidence for a continuous charge curve. Keep the retired LaptopBatteryHistory enum member only to deserialize old preferences; omit its section definition and registration, preserving unrelated choices.

## Tried and ruled out
An always-measured report canvas and empty text/list rows waste space before load. A single strip with subtle source border differences and no time axis is hard to interpret. The preset does not belong inside history and is no longer part of the product.

Trail check's lexical matches on this change include unrelated command-strip star-column rules, fixed-text-height rules, timestamp-overflow rules, disabled StackPanels, and local ThemeResource handling. The chart's star column is its time plot, fixed heights apply to graphic lanes only, report duration is bounded to 24 hours, timestamps use PresentationTimestamp, StackPanels are not disabled, and brushes remain ThemeResource properties. Inspect actual context rather than treating these matches as equivalent behaviors.

## Watch out
Keep the expander accessible with telemetry disabled. Validate compact unloaded/loading/failure states and retained data after failed refresh, normal/narrow localized layouts, and the chart's themed lane labels/time axis with disconnected native renders. Keep runtime service, Session Agent, Contracts, IPC and hardware behavior unchanged. Removing a persisted enum value outright can reset the entire preference file; test old JSON with independent settings retained.

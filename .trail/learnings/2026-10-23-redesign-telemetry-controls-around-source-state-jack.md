---
title: Redesign telemetry controls around source state and inspection context
author: Jack
date: 2026-10-23
branch: feat/telemetry-analysis-typography
touches:
  - AGENTS.md
  - CHANGELOG.md
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - docs/telemetry-and-battery-history.md
  - docs/ui-typography.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/AppTypography.cs
  - src/OpenBlade.Tray/ContentWrapPanel.cs
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
---

## Decided
The telemetry redesign changes control hierarchy and active actions, not merely button widths. Source status and relevant recording/replay actions share a two-region responsive header. Record and Stop occupy one contextual position; Return to live appears only during replay. Sample arrows flank the selected timestamp, Follow latest is a real pause/resume toggle, metric settings is a graph tool, and Full range is a timeline reset tool. Reuse a measured ContextualToolbar for these context/action regions and the content wrap panel inside its action group.

Shared custom commands use AppCustomCommandButtonStyle for native sizing/padding without a string content template. AppCommandButtonStyle derives from it and adds only the wrapping string template. Keep icon/text content in a constrained Grid and labels in AppBodyTextStyle. Native assertions must verify the actual realized localized label, its bounds and IsTextTrimmed, not just the desired source element or a style name.

## Tried and ruled out
The user rejected making the same old button rows smaller as a redesign. Compact sizing remains useful inside the new hierarchy, but does not satisfy the product change by itself. An XAML null ContentTemplate did not reliably override the text-only style for composed content; a native render exposed the view-model type name instead of the intended icon/text. Use the proper shared custom-content base rather than page-specific template workarounds. Horizontal StackPanel measures labels without a width limit and defeats wrapping; use Auto icon and star label columns instead.

## Watch out
Validate live, recording and replay action states and narrow/translated native renders. Keep the callout, raw-sample, retention and keyboard contracts from the prior learning. The follow toggle's off action holds the visible window without pinning a sample; its on action resumes following. Snapshot CSV fixtures must use real file/VM APIs without starting a service watch, stable displayed example paths, and explicit per-destination cleanup before Environment.Exit. MainViewModel test setup must wait for HasAppliedServiceState, not the earlier IsConnected notification, before mutating state through an inline test dispatcher.

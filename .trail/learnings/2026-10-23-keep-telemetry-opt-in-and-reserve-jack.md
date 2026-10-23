---
title: Keep telemetry opt-in and reserve physical pixels for wrapped tools
author: Jack
date: 2026-10-23
branch: feat/telemetry-analysis-typography
touches:
  - src/OpenBlade.Tray.Core/MainViewModel.Telemetry.cs
  - src/OpenBlade.Tray.Core/UiPreferenceStore.cs
  - src/OpenBlade.Tray/ContentWrapPanel.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
---

Telemetry is a saved per-user opt-in, default false even for older preferences. Gate history capture and observation demand as well as CSV/replay entry points. Disable before awaiting the recording gate, drain accepted appends and finalize without cancellation, preserve existing files and partial-path errors, and leave Dashboard readings independent. Retain the bounded in-memory buffer; the switch stops collection rather than deleting history. Reject replay results after an enable-generation change or disposal. Preview routes must explicitly opt in, with a separate disabled route. Native-DPI testing reproduced a fractional-width defect in ContentWrapPanel: Grid rounded its Auto column down after Measure, then Arrange wrapped into an unmeasured second row. Round the desired width up to the XamlRoot physical-pixel grid, bounded by the available width. Keep strict geometry and overlap assertions; do not hide this with tolerance or smaller fonts. The header switch reuses CompactCardToggleSwitchStyle.

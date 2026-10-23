---
title: "Separate telemetry range edits, live recording and expired pins"
author: Jack
date: 2026-10-23
branch: feat/telemetry-analysis-typography
touches:
  - src/OpenBlade.Tray/TelemetryChart.cs
  - src/OpenBlade.Tray/TelemetryChart.Range.cs
  - src/OpenBlade.Tray.Core/MainViewModel.Telemetry.cs
  - src/OpenBlade.Tray.Core/TelemetryChartPreferences.cs
---

## Decided
Range selection and sample pinning are separate interactions. Both overview dragging and keyboard range edits use SelectTimeRange to stop following without pinning a sample. The chart source identity comes only from a replay document; live CSV recording metadata must not reset the retained live chart. Unknown named telemetry metrics are converted to an unsupported sentinel and removed by existing normalization, preserving unrelated preferences and valid metric entries.

Bounded retention keeps a surviving pinned timestamp exact and explains an adjusted time range. When the pinned reading itself expires, clear its sample selection and pin, retain an explicit expired state across incoming updates, and enable keyboard Next sample and Follow latest recovery. This avoids silently substituting a different reading. Hover or an explicit sample/range/follow action clears the notice.

## Tried and ruled out
Do not freeze or copy the whole history to preserve an arbitrarily old selection. Keep the existing bounded retention and clamp contract, with honest presentation at expiry. Do not use the recording destination SessionId as live-source identity. Numeric unknown telemetry metrics remain rejected; the converter is scoped only to the metric preference property so unrelated enum contracts stay unchanged. Removed the duplicate event refresh while retaining VisibleRangeChanged as the single path.

## Watch out
Native disconnected assertions cover drag/edit unpinning, clamped-input feedback, persistent expired selection, surviving pins and keyboard recovery, then restore fixture state including validation text/visibility. Export real expiry and retention snapshots and include them in the translator inventory. The local broker currently exposes only a complete quiet installer; it cannot perform a tray-only patch. UI-only installation must preserve the installed service/Agent and include new App.xbf, Themes/Typography.xbf and matching production satellites along with the usual tray files. Keep Nightly publication held until the user requests it.

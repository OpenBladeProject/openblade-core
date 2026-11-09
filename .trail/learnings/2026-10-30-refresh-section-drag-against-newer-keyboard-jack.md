---
title: refresh section drag against newer keyboard and telemetry settings
author: Jack
date: 2026-10-30
branch: fix/section-drag-preview
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - build/Capture-LocalizationSnapshots.ps1
  - build/Generate-ExternalPowerDischargeIcons.ps1
  - build/localization/TranslatorScreenshotMap.json
  - docs/keyboard-input-backends.md
---

## Decided
PR #868's cursor-following reflow head passed CI, but main advanced to 0.49.0 before merge. Refresh from exact main and recalculate this compatible settings-card capability to 0.50.0, then validate the new head before merging.
Preserve incoming canonical Laptop card placement and the removal of Laptop Battery History/Long battery life; the report remains in Telemetry. Add invariant CardIds for the new function-row backend, SnapTap backend, and plugged-in discharge notification rows in their existing groups. Keep CardOrders and both keyboard backend preferences/converter/save hooks. Existing saved inner orders append newly introduced IDs by the existing normalization policy.
Combine phase reporting with reflow tests, XAML hit testing on the isolated desktop, and the 60-second reorder budget. Preserve both managed resource additions and the bounded localization attribute regex.

## Tried and ruled out
Choosing one entire side of the XAML/preference conflicts would lose either incoming settings/report placement or the original grouped drag contract. A green CI result for the old head cannot authorize merging against a newly advanced version baseline. No installed hardware components need changing to refresh or merge this source PR.

## Watch out
PR statusCheckRollup was empty even though exact-head manual CI check runs existed. Verify the workflow head SHA and every required lane through the run/check-runs APIs; a skipped Component publish lane is expected for this UI-only request. Refresh main immediately before merge, use the expected head SHA, and keep cleanup limited to this PR's clean dedicated worktree/branch.

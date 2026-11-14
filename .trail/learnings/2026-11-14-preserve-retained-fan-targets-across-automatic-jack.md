---
title: Preserve retained fan targets across Automatic preset changes
author: Jack
date: 2026-11-14
branch: fix/issue-967-model-admission
touches:
  - CHANGELOG.md
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.DynamicFanRanges.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelFanControlEditorTests.cs
  - evidence/source-reviewed-blades/2026-11-13-issue-967-software-validation/claude-metadata.json
  - evidence/source-reviewed-blades/2026-11-13-issue-967-software-validation/manual-fan-controls.png
  - evidence/source-reviewed-blades/2026-11-13-issue-967-software-validation/rz09-0300-creator-5500.png
---

## Decided
On issue 967 dynamic fan paths, Automatic can retain exact target registers from another reviewed preset. Admit those independently read retained targets against the exact model's reviewed range union, then validate every new SET against fresh current-preset metadata. Confirm each staged pair by exact equality while its untouched zone retains the saved value. Manual baselines keep their existing current-preset admission.

## Tried and ruled out
Do not require a prior preset's retained targets to fit the destination's range before entering manual control; this incorrectly blocks Balanced 5200 -> Automatic -> Silent 3600. Do not guess or zero the retained target, add a preparation write, or admit unreviewed values. A range change excluding the first confirmed target still stops before the second SET.

## Watch out
The source-reviewed profile1 query bank for RZ09-0328 and RZ09-0530 is pinned in their current exact Synapse definitions; speculative profile0 fallback would weaken fresh identity and is not admitted. Reuse source-backed semantics without claiming physical recovery. Legacy Maximum validation and provider recovery phases require their own explicit mode and backend contracts; a generic fan range is not a Maximum capability or permission to retry an ambiguous write.

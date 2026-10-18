---
title: Merge Blade 18 source-backed support before nightly publication
author: Jack
date: 2026-10-18
branch: feat/blade18-observer-support
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.Tray/Strings/en-US/Resources.resw
  - src/OpenBlade.Tray/Strings/qps-ploc/Resources.resw
  - src/OpenBlade.Tray/Strings/ru-RU/Resources.resw
  - src/OpenBlade.Tray/Strings/zh-CN/Resources.resw
---

## Decided
Refresh Blade 18 PR #786 against current main before merging and publishing a nightly. Main is 0.35.0, so this compatible capability expansion is 0.36.0. Preserve both additive changelog groups and all localization keys: the only branch-side resource addition was Gaming Mode scope, while main added SnapTap setup strings. Blade 18 remains source-backed experimental support; community physical checks follow publication.

## Tried and ruled out
The 20 trail check conflicts are lexical matches, not reverted decisions. Current main legitimately adds the shared private-desktop test helper; the earlier local-build note described the old immutable private artifact, not a ban on that infrastructure. Exact-model admission and per-write guards remain. Fn defaults and explicit all-keyboard Gaming Mode do not enable function-primary inversion or a Blade 18 SnapTap backend. Source-backed read-gated full charge and startup do not claim that the Observer captured enablement or physical behavior.

## Watch out
Validate the new combined head rather than reuse the old 164540b results as final-head proof. Publish the nightly through its generated changelog PR and guarded finalization. Keep the private owner JSON out of tracked files and public release assets, and update its build prerequisite to the final published nightly. Preserve dirty landing-checkout files during synchronization and preserve build/test artifacts before branch cleanup.

---
title: Await complete HyperBoost presentation in failed-command test
author: Jack
date: 2026-10-29
branch: fix/monolith-deck-refinement
touches:
  - AGENTS.md
  - tests/OpenBlade.Tray.Tests/MainViewModelTests.cs
---

## Decided
HyperBoostFailedCommand_PreservesAuthoritativePresentation must await both HyperBoostActive and TrayIconState at each published transition. ApplyDeviceStatePresentation sets the active flag before resolving the icon, so the flag alone is not a presentation-completion barrier. Retain the failed-command feedback and authoritative-state assertions.

## Tried and ruled out
CI on PR #860 observed AcHyperBoost after the off flag became visible. The icon-only patch did not change this test or runtime code. Do not add sleeps, reorder production presentation, remove assertions, or merge over a failed check.

## Watch out
The build-and-test check is an aggregate emitted after child CI jobs finish; its temporary absence does not establish an obsolete branch-protection requirement. Preserve user-owned AGENTS.md changes and deployment artifacts during cleanup.

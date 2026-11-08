---
title: Refresh the status draft without repeating live validation
author: Jack
date: 2026-11-08
branch: chore/refresh-rust-status-cli
touches:
  - CHANGELOG.md
  - build/PawnIoPrerequisite.ps1
  - tests/Build.Tests/PawnIoPrerequisite.Tests.ps1
---

## Decided
Refresh draft PR #899 from main 71193867 after its prior final CLI head 25cf805b. The main delta is the reviewed PawnIO acquisition retry fix and nightly changelog history; the CLI, v51 wire contract, and installed product boundaries remain unchanged. Keep the prototype draft and unmerged.

## Tried and ruled out
Trail check's timeout/version lexical matches across PawnIO acquisition, CLI wire deadlines, and CPU telemetry refer to different subsystems. The retry implementation is already on main and does not alter CLI cancellation or telemetry lifetime. Earlier documented CLI scope distinctions still apply; no v78 status activation or production test hook is added.

## Watch out
Reuse the completed Claude candidate review and existing one-shot Windows probe while these paths remain unchanged. Refresh offline checks and exact-head CI for the merged draft rather than repeating live actions or paid review. Updating the draft does not authorize its merge or deployment.

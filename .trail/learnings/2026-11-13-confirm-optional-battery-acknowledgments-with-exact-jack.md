---
title: Confirm optional battery acknowledgments with exact delayed GET
author: Jack
date: 2026-11-13
branch: fix/battery-protection-963
touches:
  - Directory.Build.props
  - docs/source-reviewed-blade-models.md
  - docs/source-reviewed-blades-2021-2022.md
  - evidence/source-reviewed-blades/2026-11-07-models-2021-2022.json
  - evidence/source-reviewed-blades/2026-11-07-models.json
  - src/OpenBlade.Protocol/ExperimentalBladePort.Battery.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.BatteryConfirmation.cs
---

## Decided
Issue963's seven source-reviewed limiter profiles (RZ09-0508/0510/0509/0529/0427/0421/0423) use pinned Synapse optional SET payload semantics. Accept successful zero/one-byte ACKs without echo equality, but only independent exact requested raw07/92 confirms success. Retry only the exact pre-write raw byte, up to9 GETs250ms apart, with no SET replay. Legacy029F retains its separately reviewed convergence; other legacy and capture-backed ACK contracts stay strict. The maintainer explicitly authorized this reliability correction while owner diagnostics are pending.

## Tried and ruled out
The BatteryPolicy constructor rejects100, and tray100 routes to ProtectionOff. ExchangeAsync converts invalid post-submit ACKs to Indeterminate and MapWriteFailure latches recovery. Production desired-state battery planning resolves limiter-only null override to PolicyOnly; do not synthesize false. These conditional Claude concerns were checked and rejected.

## Watch out
This correction does not establish the physical root cause or recovery of issue963. Keep that issue open for diagnostics. A different admitted GET is not stale; stop immediately, as on failed/invalid reads or cancellation. Keep threshold-preserving Off confirmation exact. Native build wrappers at this base override CARGO_TARGET_DIR; verification must redirect to the single shared cache rather than creating a target under each worktree.

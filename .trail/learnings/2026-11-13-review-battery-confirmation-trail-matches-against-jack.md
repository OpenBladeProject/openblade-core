---
title: Review battery confirmation trail matches against actual invariants
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
The battery-confirmation patch changes optional status-ACK admission only on the seven pinned Synapse limiter profiles and adds baseline-only read retries. Exact requested raw GET confirmation, indeterminate-write recovery, nullable override handling and legacy/capture-backed contracts remain intact. Product version changes0.61.1 to0.61.2.

## Tried and ruled out
Trail check's20 matches against old version numbers, lighting rules and generic failed/raw/state terms are lexical overlaps. The patch neither accepts failed GET bytes nor broadens content-rejection classification, replays SET, guesses null overrides, changes lighting commands, or claims measured firmware delay. The PR records this rationale instead of weakening those invariants to satisfy keywords.

## Watch out
Issue963 remains open for owner diagnostics; passing software checks does not establish its physical cause or recovery.

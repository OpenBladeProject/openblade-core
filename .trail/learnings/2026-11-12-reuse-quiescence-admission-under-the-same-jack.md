---
title: Reuse quiescence admission under the same metadata writer
author: Jack
date: 2026-11-12
branch: fix/history-streaming-admission
touches:
  - src/OpenBlade.Deployment/WindowsManagedMailboxHandoff.cs
---

## Decided

The independent integrated review of 3b59eb6a found one additional same-lock full retained-run scan: PublishQuiescent acquired the managed metadata writer while the run already retained exclusion, called Selected, then immediately called RequireActive, which called Selected again. Keep the freshly validated selection and check only terminal absence at that point. Existing receipt, current-run and installation checks stay in place. No new firmware or custody authority is introduced.

## Tried and ruled out

Do not cache the Begin lease's later RequireBeginActive admissions across the metadata writer gap. AcquireForBegin retains exclusion but not the managed metadata writer; AuthenticateRetainedTerminals separately acquires and releases that writer before later preparation/native-binding steps. Their fresh checks remain required. Startup Resolve and AcquireRunExclusion already scan once. Create reuses its validated writer-protected snapshot.

## Watch out

Preserve the completed 3b59 intermediate source/harness/compiler/test artifacts separately, then bind and run final checks at the new clean head. Reuse unchanged review context by exact raw/blob binding, re-review the Handoff change and added learning, and freshly rerun the accepted 331 baseline plus18 new assertions. Existing quiescence/receipt/exclusion/current-run and terminal failure tests exercise the unchanged outcome; a counter-only implementation-mirroring test is unnecessary. This is offline source proof, not runtime/hardware validation.

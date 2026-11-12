---
title: Distinguish fixture startup guards from cancellation deadlines
author: Jack
date: 2026-11-12
branch: fix/blade14-lighting-readback
touches:
  - tests/OpenBlade.Capture.Tests/Rz090528PadlessLoadedHyperBoostValidationCommandTests.cs
---

## Decided
In CanceledBlockedReadCannotPublishAndKeepsResourcesUntilCompletion, waiting for the fake callback to signal entry is a ThreadPool startup/deadlock guard, before cancellation occurs. Use the existing ten-second completion guard rather than a two-second scheduling assumption on cold hosted runners. The fixture's five-minute lane deadline and its cancellation, no-late-publication, exactly-once disposal and resource-retention assertions remain separate.

## Tried and ruled out
The exact-head CI failure was at the pre-cancellation entered signal, while the unchanged fixture passed in the full local Capture run. This does not establish a product cancellation bug. Do not change the production two-second lane deadline or weaken post-cancellation ownership assertions to accommodate runner scheduling.

## Watch out
Inspect the failing await before treating every timeout as the same deadline. GitHub refuses a failed-job rerun while another job in the same workflow is still running; wait for that run to finish or publish a justified correction and let exact-head CI rerun normally.

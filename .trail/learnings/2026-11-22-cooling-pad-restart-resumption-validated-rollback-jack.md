---
title: Cooling pad restart resumption validated; rollback cause unproven
author: Jack
date: 2026-11-22
branch: fix/cooling-pad-restart-resume
cost: { minutes: 19, tokens: 60969 }
touches:
  - docs/cooling-pad.md
  - src/OpenBlade.Protocol/SourceBackedCoolingPadDevice.cs
  - tests/OpenBlade.Protocol.Tests/CoolingPadSourceProtocolTests.cs
---

## Decided
- Restart resumption was validated on the RZ09-0581 with an RC21-0230 (PR 1004): a broker install of a new commit stopped the service cleanly, and the new process resumed the saved keyboard-sync settings with no request.

## Tried and ruled out
- The earlier learning "Cooling pad lighting status after OpenBlade's own Apply" says an identical-build broker reinstall rolls back because the version comes from the commit. That cause is not proven: the broker logs are admin-only, and Setup's cold-start health probe can also roll back. What was observed is that the service process was untouched. Use a new commit to test a restart.

## Watch out
- A restart only resumes from a clean handback. If the old service paused the pad on an unconfirmed write, its stop logs "Cooling-pad fan cleanup: Read the cooling pad before restoring retained settings" and the new service waits in recovery. That is intended. Before a restart test, read the pad and confirm Active with no pause or recovery.

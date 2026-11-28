---
title: Rearm the driver keyboard after a killed stall
author: Jack
date: 2026-11-28
branch: fix/driver-keyboard-orphaned-hooks
cost: { minutes: 27, tokens: 594547 }
touches:
  - AGENTS.md
  - CHANGELOG.md
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - tests/OpenBlade.SessionAgent.Tests/DriverOwnershipJournalReaderTests.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapDriverSupervisorTests.cs
  - Directory.Build.props
  - src/OpenBlade.SessionAgent/DriverOwnershipJournalReader.cs
  - src/OpenBlade.SessionAgent/RazerKeyboardOwnedHookEvidence.cs
---

## Decided
After a Windows servicing restart on RZ09-0581 (2026-11-27), the driver keyboard worker missed the 1.5 s input heartbeat about 13 s after its supervisor started, then ignored the stop for the full 6 s wait. The supervisor killed it (exit -1), reconciled the hooks with readback, and still latched RecoveryRequired, so media keys stayed dead. The killed-stall rearm now applies the approved force-kill policy to the supervisor's own kill. It requires:
- IsKilledStall: exactly HeartbeatTimeoutFailure(started: true), exit -1, and no terminal status from the worker;
- successful reconciliation;
- WaitForReleasedInput (5 s);
- the shared TryAdmitStallRearm budget;
- ClearJournalLatch reopening the journal that was latched before cleanup.
On stop, it closes the journal only when input is released. A clean stall with the clean-pause exit (8) had the same latched-journal rearm defect and now reopens its journal too.

## Tried and ruled out
Raising the heartbeat or stop budgets: the stall was a whole-machine freeze. In that boot, the Tray took 46 s from process start to its first log line. Trusting killedAfterStall alone: Kill() does not throw on a worker that just exited and keeps its own exit code, so exit 0/1/8 in that gap would have been rearmed.

## Watch out
The "OpenBlade startup was delayed" report was Windows: logon came 80 s after boot (servicing restart), and the logon tasks launched OpenBlade 1 s after logon. Tray logon-to-first-log ranges from 12 to 49 s across boots. A journal already latched by an older build still needs one explicit retry (turn SnapTap off and on) or a reboot. No test drives Run, so the gate ordering is pinned only by review.

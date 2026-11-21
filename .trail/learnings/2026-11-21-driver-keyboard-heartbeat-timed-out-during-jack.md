---
title: Driver keyboard heartbeat timed out during per-keystroke admission at boot
author: Jack
date: 2026-11-21
branch: fix/function-row-foreground-pause-status
cost: { minutes: 10, tokens: 78938 }
touches:
  - CHANGELOG.md
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapDriverSupervisorTests.cs
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/SnapTapRuntimeStatus.cs
  - tests/OpenBlade.SessionAgent.Tests/KeyboardDriverStatusStoreTests.cs
---

## Decided
The driver keyboard worker heartbeats once per input-loop pass, and the supervisor allowed 1.5 s between heartbeats. Every driver mutation (each Send of a synthesized keystroke, hook set/clear, enable) first repeats full exact-device/controller admission (process scan, PnP enumeration, two driver-trust captures) on the input thread. That admission took 1.7 s at boot on RZ09-0581 (supervisor log 13:52:49.35 to 51.06) and 0.44 s later, so one key press during boot load produced HeartbeatTimeout with MayHaveSubmitted=True, a terminal Failed status and a coordinator latch until a preference toggle. Fix: the worker brackets each admission with a named manual-reset window event (heartbeat, set window, admit, heartbeat, reset window) and the supervisor applies the 10 s startup budget while the window is set. Per-mutation admission and the 1.5 s budget for everything else are unchanged.

## Tried and ruled out
Caching admission between keystrokes: it would weaken the per-mutation fresh admission rule. A heartbeat timer thread: the doc requires heartbeats from the input loop so a blocked driver request cannot be masked. Raising the input budget globally: driver requests are already bounded (250 ms plus 1 s cancel, then FailFast) and must stay under 1.5 s. Treating a heartbeat timeout followed by clean exit 0 as recoverable: the supervisor cannot prove nothing was in flight.

## Watch out
Ordering matters: the supervisor reads the window flag before consuming the auto-reset heartbeat, and the worker signals the heartbeat before clearing the flag, so a just-finished admission is never paired with the stale pre-admission heartbeat. The evidence that a key press triggered the boot stall is inferential (admission is the only unbounded call in the loop and its measured cost exceeded the budget); no log line records the key press. Not validated on hardware.

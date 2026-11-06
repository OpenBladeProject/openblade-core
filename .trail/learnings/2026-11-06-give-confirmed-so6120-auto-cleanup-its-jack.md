---
title: Give confirmed SO6120 Auto cleanup its own phase
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/So6120SilentTrial.cs
  - src/OpenBlade.Capture/So6120DirectDutySession.cs
  - src/OpenBlade.Capture/So6120SilentPwmCommand.cs
  - tests/OpenBlade.Capture.Tests/So6120SilentTrialTests.cs
---

## Decided
SO6120 entry clocks are cutoffs for new A1 publications and optional dwell at resolved boundaries. Complete an admitted write, its separate A0 confirmation and mandatory state checkpoint. Then give an explicitly approved Auto handback its own bounded phase, checked before each new logical transaction without shortening an in-flight confirmation. Record actual phase/exposure milestones rather than claiming a hard physical duration.

## Tried and ruled out
A shared entry/exposure clock must not fence an otherwise confirmed cleanup merely because entry took longer than expected. Simulated 160 and 320 millisecond metadata captures cover the expensive fresh-scope path; they are not measured live latency. Aggregate snapshot timeout between clean completed reads is a missing observation, not itself an ambiguous native command. Mandatory post-write observation failure still fences. Loss of actual device, power, scope or ownership admission is different and still blocks every mailbox command.

## Watch out
High or unknown temperature should refuse before consuming the machine attempt latch, with the fresh post-acquisition and per-write checks retained. Buffer optional console/progress output until restoration or fencing; synchronous backpressure can otherwise delay Auto. Native calls and mandatory durable storage can still stall. No clock policy guarantees physical recovery, no inspected journal permits replay, and this prepared runner has not established live PWM control.

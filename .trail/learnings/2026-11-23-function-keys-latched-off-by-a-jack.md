---
title: Function keys latched off by a mid-session driver keyboard heartbeat timeout
author: Jack
date: 2026-11-23
branch: claude/function-keys-not-working-e4a1ad
cost: { minutes: 7, tokens: 149837 }
---

## Decided
Function keys died at 2026-11-22 23:20:46Z on RZ09-0581 (build f06a65d2): the driver keyboard supervisor saw no input-loop heartbeat for over 1.5 s, published HeartbeatTimeout with MayHaveSubmitted=True and latched state Failed (5) in function-row-status-1.json, keyboard-driver-status-1.json and snap-tap-status-1.json. The worker then exited 0 after the stop request and hook reconciliation passed: Failed with exit code 0 is only reachable through the clean branch where observedWorkerFailure is set. So this was a transient stall in a worker that was still healthy, not boot admission (the Nov 21 admission window already covered that and was installed).

## Tried and ruled out
No sleep, resume or Kernel-Power events in the System log around 15:20 local. Nothing in the loop blocks by design: the status publisher is a channel, Fn publish is a non-blocking enqueue, TryRead waits 10 ms and its cancel is bounded at 1 s (a slower cancel FailFasts, which gives a non-zero exit), and Send timeouts throw, which also gives exit 1. The only correlated signal was an off-cycle GpuApps scan burst at 23:20:44Z (new GPU processes), which suggests CPU or paging starvation. The worker leaves no log, so the exact cause is unproven.

## Watch out
The user recovers by switching the function row (or SnapTap) input method away and back, or by turning SnapTap off and on. Changing how a heartbeat timeout followed by a clean exit 0 is treated would reverse the Nov 21 learning's ruled-out item, so it needs the maintainer's decision. Hardening the worker against stalls (opting out of EcoQoS and raising its priority) does not touch that rule.

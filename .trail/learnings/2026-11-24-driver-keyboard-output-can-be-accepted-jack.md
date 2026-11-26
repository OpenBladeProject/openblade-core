---
title: Driver keyboard output can be accepted without reaching Windows
author: Jack
date: 2026-11-24
branch: fix/driver-snaptap-delivery-check
---

## Decided
On 2026-11-23 an RZ09-0581 lost every A and D for about 90 minutes after a clean-pause rearm at 20:01 local: the worker reported Active and heartbeated, no send failed, and typing returned only when a 21:39 HeartbeatTimeout made the supervisor clear the owned hooks. The worker keeps no per-key record, so the cause is unproven; the leading hypothesis is the driver's original-output path accepting keys without delivering them. PR #1008 makes the worker require every key it sends through the driver to appear in Raw Input from a real device (nonzero hDevice) within 2 s and five drained pumps, counted from after Send returns; a miss poisons the processor and fails with OutputNotDelivered (MayHaveSubmitted), so the guardian clears the hooks and the state is RecoveryRequired.

## Tried and ruled out
A separate keyboard TLC registration (01/06) in the worker: Raw Input routes a device class to one window per process and a specific TLC beats a page-only one, so it starved the function-row Fn observer, which uses keyboard packets on RZ09-0484 and RZ09-0528. With a FunctionRowDriverContext the check now drains the observer's SnapTapKeyboardFeed (ReactiveKeyboardSourceOptions.ObserveKeyboardInput). A 500 ms deadline counted from before Send: each send first repeats admission, measured at 1.7 s under boot load. An ordering fast path (a later key seen while an earlier one is pending): another keyboard's matching key could mark the earlier key lost.

## Watch out
If the worker never receives hooked keys at all, nothing is sent and the check cannot fire; the Nov 22 learning notes it is unknown whether RzCommon swallows matched keys without a subscriber. In feed mode the pump guard covers only the worker thread. Input to an elevated or unreadable-integrity window is not checked. Not physically validated: Filter Keys, other keyboard filters or low-level hooks that swallow keys, lock screen and UAC typing, function-row keys on RZ09-0484.

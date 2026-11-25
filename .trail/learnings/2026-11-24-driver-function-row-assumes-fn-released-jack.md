---
title: Driver function row assumes Fn released instead of prompting
author: Jack
date: 2026-11-24
branch: fix/function-row-assume-fn-released
---

## Decided
The maintainer asked to remove the "Press Fn once" wait on the Razer-driver function row. On RZ09-0581 and RZ09-0528, `FunctionKeyObservation` now starts released and `WindowsReactiveKeyboardSource.ResetFunctionPInput` calls `ResetFunctionKeyKnowledge`, which runs `AssumeReleased()` (Blade 16) and `InvalidateKnowledge()` (Blade 18). Only a malformed Fn report, a failed input packet or message-thread exit make Blade 16 Fn unknown. This reverses the Oct 30 learning's unknown-at-start and invalidate-on-source-reset rules. RZ09-0484 still waits, because its Fn state is bound to the collection that first sends report 4.

## Tried and ruled out
Changing only the initial value did nothing in production. The first service status always calls `ResetFunctionPInput`, because `previousControlAccess` and `previousPowerSource` start null (ServiceReactiveLightingLoop.ApplyAuthoritativeStatus). Raw Input registration with RIDEV_DEVNOTIFY also posts GIDC_ARRIVAL for every existing device, and each one resets. Unit tests on the bare class stayed green. An independent reviewer's console probe against the built DLL caught it.

## Watch out
`FunctionKeyObservation.AssumeReleased()` means known-released, but `Rz090484FnInputState.Reset()` means unknown; do not conflate them. Unlike the Windows hook, which never resets its plain bool, the driver helper discards a held Fn on reset: with Multimedia primary, a press made during that hold gives a media action until Fn is released. The prompt still appears while the service has not verified the laptop. Tests cover the seam but not that `ResetFunctionPInput` calls it, or the ordering of the invalidate after the reset in the packet-exception path. Not physically validated: sign-in, backend switch, AC plug/unplug, HID hot-plug, lock/unlock.

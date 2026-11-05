---
title: Make owned direct PWM exit generic through Auto
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/DirectDutyAutomaticExit.cs
  - tests/OpenBlade.Capture.Tests/DirectDutyAutomaticExitTests.cs
  - docs/so6120-direct-pwm-handback.md
  - tools/firmware/profiles/so6120-ec-1.04.json
---

## Decided

Auto is the generic orderly exit from an owned direct-PWM experiment. Preserve the captured or explicitly intended admitted normal performance profile; do not require Silent, Balanced, or a profile round trip. Silent selector 5 remains the exact firmware fixture example, not a universal exit destination. Generic profile intent does not broaden the exact SO6120 EC 1.04 / MCU 1.07 hardware evidence.

The developer-only DirectDutyAutomaticExit coordinator has no live port, entry producer, executable route or production binding. It requires an independently validated owned-entry record, always submits both admitted Auto channels without cached no-ops, confirms actual EC selector and Automatic state, then restores changed owned override/staged duties, bank, and gate last. It preserves unrelated bits by rejecting expected-current drift before a whole-byte restoration. Custom Auto keeps power settings and clears MaximumFans.

## Tried and ruled out

Do not admit FE in the ordinary production decoder or infer ownership from observing it. Do not treat MCU target echoes, accepted submissions, matching cached settings, or process-local serialization as EC completion or exclusive ownership. A new coordinator object must not replay an unresolved operation: the future port must atomically and durably claim each OperationId once across instances/restarts. The tests model that claim; they do not establish real persistence or hardware exclusion.

## Watch out

Every mutation requires durable intent, fresh exact-device/power/session/ownership and expected-current admission, a bounded submission, independent confirmation, and durable confirmation. Uncertainty stops all further writes, including cleanup. Preserve FanHandbackConfirmed as a historical observation if later cleanup fails; CleanupConfirmed and Success stay false. Restore bank before closing an experiment-opened gate, and refuse an adapter that cannot confirm final access without reopening or writing. Real transport exclusion, durable storage, exact-model command binding, live readback and physical handback remain unimplemented. Host-loss cooling recovery is separate from orderly Auto exit.

Trail lexical matches in the branch's research prose repeat rejected raw writes and unsafe bypasses to explain why they remain unavailable. This slice adds a simulated developer coordinator, not a raw hardware API or production admission; the ordinary decoder and installed controls remain unchanged. Prior learnings are retained.
